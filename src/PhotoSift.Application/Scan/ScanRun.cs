using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using PhotoSift.Domain.Scan;

namespace PhotoSift.Application.Scan;

internal sealed class ScanRun : IScanRun
{
    private readonly CancellationTokenSource source = new();
    private readonly object lifecycle = new();
    private readonly ConcurrentQueue<Exception> faults = new();
    private readonly Channel<OccurrenceWork> work = Channel.CreateBounded<OccurrenceWork>(
        new BoundedChannelOptions(BoundedScanExecutor.WorkCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });
    private readonly ScanRunContext context;
    private readonly IScanResultSink sink;
    private readonly IScanEnumerator enumerator;
    private readonly ProtectedExactWorker worker;
    private readonly Task producer;
    private readonly Task consumer;
    private Task? cancellation;
    private Task? disposal;
    private bool disposed;

    public ScanRun(ScanRunContext context, IScanResultSink sink, IScanEnumerator enumerator, ProtectedExactWorker worker)
    {
        this.context = context;
        this.sink = sink;
        this.enumerator = enumerator;
        this.worker = worker;
        // Two fixed background tasks per run. Native enumeration/open/refresh
        // never execute on the caller's owner turn or fan out per occurrence.
        producer = Task.Run(ProduceAsync);
        consumer = Task.Run(ConsumeAsync);
        Completion = ObserveWorkAsync();
    }

    public Task Completion { get; }

    public Task CancelAsync()
    {
        lock (lifecycle)
        {
            if (disposed) return cancellation ?? Task.CompletedTask;
            return cancellation ??= source.CancelAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycle)
        {
            disposal ??= DisposeCoreAsync();
            return new ValueTask(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var callbacks = CancelAsync();
        try
        {
            // WhenAll observes work and callbacks even when either faults. The
            // owner invokes this outside a turn, after committing replacement.
            await Task.WhenAll(Completion, callbacks).ConfigureAwait(false);
        }
        finally
        {
            lock (lifecycle)
            {
                source.Dispose();
                disposed = true;
            }
        }
    }

    private async Task ProduceAsync()
    {
        OccurrenceWork? registered = null;
        var outcome = IncompleteEnumeration();
        try
        {
            await foreach (var item in enumerator.EnumerateAsync(context, source.Token).ConfigureAwait(false))
            {
                source.Token.ThrowIfCancellationRequested();
                switch (item)
                {
                    case ScanFileFound found:
                        registered = await sink.RegisterAsync(context, found.File).ConfigureAwait(false);
                        if (registered is not null)
                        {
                            await work.Writer.WriteAsync(registered, source.Token).ConfigureAwait(false);
                            registered = null;
                        }
                        break;
                    case ScanEnumerationEnded ended:
                        outcome = ended.Outcome;
                        return;
                    default:
                        throw new InvalidOperationException("Unknown scan enumeration event.");
                }
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            outcome = Canceled();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            outcome = IncompleteEnumeration();
        }
        catch (Exception exception)
        {
            outcome = new StageOutcome(ScanStageState.FailedTerminal, ScanReason.ProgrammingFault);
            RecordFault(exception);
        }
        finally
        {
            // Register's acknowledgment can arrive after cancellation. The row
            // waiting to enter a full channel still belongs to this producer.
            try
            {
                if (registered is not null) await sink.FailAsync(registered.Token, Canceled()).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RecordFault(exception);
            }
            try
            {
                if (source.IsCancellationRequested && outcome.Reason != ScanReason.ProgrammingFault) outcome = Canceled();
                await sink.EnumerationEndedAsync(context, outcome).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RecordFault(exception);
            }
            finally
            {
                work.Writer.TryComplete();
            }
        }
    }

    private async Task ConsumeAsync()
    {
        // Do not cancel the reader: it must settle every accepted queued row.
        await foreach (var occurrence in work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (source.IsCancellationRequested)
                    await sink.FailAsync(occurrence.Token, Canceled()).ConfigureAwait(false);
                else
                    await worker.AnalyzeAsync(occurrence, sink, source.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                RecordFault(exception);
            }
        }
    }

    private void RecordFault(Exception exception)
    {
        faults.Enqueue(exception);
        // Store the callback task; disposal and CancelAsync observe it. Waiting
        // here could deadlock a callback waiting for worker completion.
        _ = CancelAsync();
    }

    private async Task ObserveWorkAsync()
    {
        await Task.WhenAll(producer, consumer).ConfigureAwait(false);
        var errors = faults.ToArray();
        if (errors.Length == 1) ExceptionDispatchInfo.Throw(errors[0]);
        if (errors.Length > 1) throw new AggregateException(errors);
    }

    private static StageOutcome Canceled() => new(ScanStageState.Canceled, ScanReason.Canceled);
    private static StageOutcome IncompleteEnumeration() => new(ScanStageState.FailedTerminal, ScanReason.IncompleteEnumeration);
}
