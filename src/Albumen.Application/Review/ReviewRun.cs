using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Albumen.Application.Scan;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;

namespace Albumen.Application.Review;

internal sealed class ReviewRun : IReviewRun, IReviewResultSink
{
    private readonly CancellationTokenSource source = new();
    private readonly object lifecycle = new();
    private readonly ConcurrentQueue<Exception> faults = new();
    private readonly Channel<ReviewRevalidationRequest> work;
    private readonly Channel<Delivery> results;
    private readonly IReviewResultSink sink;
    private readonly ProtectedReviewWorker worker;
    private readonly Task producer;
    private readonly Task[] consumers;
    private readonly Task resultDrain;
    private Task? cancellation;
    private Task? disposal;
    private bool disposed;

    public ReviewRun(ReviewSessionPin session, ScanRunContext context, ImmutableArray<CandidateGroup> groups,
        IReviewResultSink sink, ProtectedReviewWorker worker, int maxConcurrency, int capacity)
    {
        this.sink = sink;
        this.worker = worker;
        work = CreateChannel<ReviewRevalidationRequest>(capacity, singleWriter: true, singleReader: maxConcurrency == 1);
        results = CreateChannel<Delivery>(capacity, singleWriter: false, singleReader: true);
        // A fixed set of tasks keeps native work and result-capacity waits off owner turns.
        resultDrain = Task.Run(DrainResultsAsync);
        producer = Task.Run(() => ProduceAsync(session, context, groups));
        consumers = Enumerable.Range(0, maxConcurrency).Select(_ => Task.Run(ConsumeAsync)).ToArray();
        Completion = ObserveWorkAsync();
    }

    public Task Completion { get; }

    private static Channel<T> CreateChannel<T>(int capacity, bool singleWriter, bool singleReader) =>
        Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = singleWriter,
            SingleReader = singleReader,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

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
            // Callbacks may themselves await Completion, so observe them alongside
            // completed work rather than making work completion depend on callbacks.
            await Task.WhenAll(Completion, callbacks).ConfigureAwait(false);
        }
        finally
        {
            lock (lifecycle) { source.Dispose(); disposed = true; }
        }
    }

    private async Task ProduceAsync(ReviewSessionPin session, ScanRunContext context, ImmutableArray<CandidateGroup> groups)
    {
        ReviewRevalidationRequest? pending = null;
        try
        {
            foreach (var group in groups)
            {
                foreach (var occurrence in group.Members)
                {
                    source.Token.ThrowIfCancellationRequested();
                    pending = new ReviewRevalidationRequest(session, context, group.GroupId, group.GroupRevision, occurrence, 1);
                    await work.Writer.WriteAsync(pending, source.Token).ConfigureAwait(false);
                    pending = null;
                }
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception exception) { RecordFault(exception); }
        finally
        {
            try
            {
                if (pending is not null) await FailRevalidationAsync(pending, ReviewUnavailableReason.Canceled).ConfigureAwait(false);
            }
            catch (Exception exception) { RecordFault(exception); }
            finally { work.Writer.TryComplete(); }
        }
    }

    private async Task ConsumeAsync()
    {
        // Cancellation ends production, but every admitted queued request still settles.
        await foreach (var request in work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (source.IsCancellationRequested)
                    await FailRevalidationAsync(request, ReviewUnavailableReason.Canceled).ConfigureAwait(false);
                else
                    await worker.RevalidateAsync(request, this, source.Token).ConfigureAwait(false);
            }
            catch (Exception exception) { RecordFault(exception); }
        }
    }

    public ValueTask<bool> CommitRevalidationAsync(ReviewRevalidationEvidence evidence) =>
        SubmitAsync(() => sink.CommitRevalidationAsync(evidence));

    public ValueTask<bool> FailRevalidationAsync(ReviewRevalidationRequest request, ReviewUnavailableReason reason) =>
        SubmitAsync(() => sink.FailRevalidationAsync(request, reason));

    private async ValueTask<bool> SubmitAsync(Func<ValueTask<bool>> callback)
    {
        var delivery = new Delivery(callback);
        // The owner sees the result only after capacity is acquired. The sink's
        // separate command inbox is never occupied by this capacity wait.
        await results.Writer.WriteAsync(delivery, CancellationToken.None).ConfigureAwait(false);
        return await delivery.Acknowledgment.Task.ConfigureAwait(false);
    }

    private async Task DrainResultsAsync()
    {
        await foreach (var delivery in results.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { delivery.Acknowledgment.TrySetResult(await delivery.Callback().ConfigureAwait(false)); }
            catch (Exception exception) { delivery.Acknowledgment.TrySetException(exception); }
        }
    }

    private void RecordFault(Exception exception)
    {
        faults.Enqueue(exception);
        // Retain the cancellation callback task without awaiting it from a worker.
        _ = CancelAsync();
    }

    private async Task ObserveWorkAsync()
    {
        try { await Task.WhenAll(consumers.Append(producer)).ConfigureAwait(false); }
        finally
        {
            results.Writer.TryComplete();
            await resultDrain.ConfigureAwait(false);
        }
        var errors = faults.ToArray();
        if (errors.Length == 1) ExceptionDispatchInfo.Throw(errors[0]);
        if (errors.Length > 1) throw new AggregateException(errors);
    }

    private sealed class Delivery(Func<ValueTask<bool>> callback)
    {
        internal Func<ValueTask<bool>> Callback { get; } = callback;
        internal TaskCompletionSource<bool> Acknowledgment { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
