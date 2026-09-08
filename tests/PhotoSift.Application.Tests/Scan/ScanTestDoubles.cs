using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using Xunit;

namespace PhotoSift.Application.Tests.Scan;

internal static class ScanTestData
{
    public static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static Task Bound(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    public static Task<T> Bound<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    public static readonly StageOutcome Success = new(ScanStageState.Succeeded, ScanReason.None);
    public static readonly StageOutcome Canceled = new(ScanStageState.Canceled, ScanReason.Canceled);
    public static readonly StageOutcome ReadFailure = new(ScanStageState.FailedRetryable, ScanReason.ReadFailure);
    public static readonly string Digest = new('A', 64);
    public static ScanRunContext Context() => new(new ScanPin(Guid.NewGuid(), 1, 1, 1),
        new ResolvedScope(new ScopeSelection(@"C:\Photos", ScopeRecursion.IncludeSubfolders),
            @"C:\Photos", new DirectoryIdentity(1, 2, 3)));
    public static OccurrenceWork Work(long id = 1, ScanRunContext? context = null)
    {
        context ??= Context();
        return new(context, new OccurrenceToken(context.Pin, id, 1, 1),
            new EnumeratedFile($@"C:\Photos\{id}.jpg", $@"C:\Photos\{id}.jpg",
                new PhysicalFileKey(1, new FileId128(0, (ulong)id)), Success));
    }
    public static ContentRevision Revision(OccurrenceWork work) => new(work.File.PhysicalFileKey!.Value, 1, Digest);
    public static ComparisonWork Comparison(OccurrenceWork left) => new(left, Revision(left),
        Work(2, left.Context), Revision(Work(2, left.Context)));
}

internal sealed class ManualScanScheduler : IWorkflowScheduler
{
    private readonly ConcurrentQueue<Action> callbacks = new();
    public void Post(Action callback) => callbacks.Enqueue(callback);
    public bool TryRunOne()
    {
        if (!callbacks.TryDequeue(out var callback)) return false;
        callback();
        return true;
    }
    public void RunOne() => Assert.True(TryRunOne());
    public async Task PumpUntilAsync(Task completion)
    {
        while (!completion.IsCompleted)
        {
            if (!TryRunOne()) await Task.Yield();
        }
        await completion;
    }
}

internal sealed class ScanTestSink : IScanResultSink
{
    public Func<ScanRunContext, EnumeratedFile, ValueTask<OccurrenceWork?>>? Register { get; set; }
    public Func<SignatureEvidence, ValueTask<SignatureCommit>> Signature { get; set; } =
        _ => ValueTask.FromResult(new SignatureCommit(true, []));
    public Func<ComparisonEvidence, ValueTask<bool>> Comparison { get; set; } = _ => ValueTask.FromResult(true);
    public Func<OccurrenceToken, StageOutcome, ValueTask<bool>>? Failure { get; set; }
    public ConcurrentQueue<(OccurrenceToken Token, StageOutcome Outcome)> Failures { get; } = new();
    public ConcurrentQueue<StageOutcome> Endings { get; } = new();
    private long nextId;
    public ValueTask<OccurrenceWork?> RegisterAsync(ScanRunContext context, EnumeratedFile file) =>
        Register?.Invoke(context, file) ?? ValueTask.FromResult<OccurrenceWork?>(
            new(context, new OccurrenceToken(context.Pin, Interlocked.Increment(ref nextId), 1, 1), file));
    public ValueTask<SignatureCommit> CommitSignatureAsync(SignatureEvidence evidence) => Signature(evidence);
    public ValueTask<bool> CommitComparisonAsync(ComparisonEvidence evidence) => Comparison(evidence);
    public ValueTask<bool> FailAsync(OccurrenceToken token, StageOutcome outcome)
    {
        Failures.Enqueue((token, outcome));
        return Failure?.Invoke(token, outcome) ?? ValueTask.FromResult(true);
    }
    public ValueTask EnumerationEndedAsync(ScanRunContext context, StageOutcome outcome)
    {
        Endings.Enqueue(outcome);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ScanTestReader : IProtectedFileReader
{
    public Func<int, ProtectedOpenRequest, ScanTestLease>? Create { get; set; }
    public ConcurrentQueue<ScanTestLease> Leases { get; } = new();
    private int opens;
    private int live;
    public int MaximumLive { get; private set; }
    public int Live => Volatile.Read(ref live);
    public ValueTask<IProtectedFileLease> OpenAsync(ProtectedOpenRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = Create?.Invoke(Interlocked.Increment(ref opens), request) ?? new ScanTestLease(request);
        MaximumLive = Math.Max(MaximumLive, Interlocked.Increment(ref live));
        lease.OnDispose = () => Interlocked.Decrement(ref live);
        Leases.Enqueue(lease);
        return ValueTask.FromResult<IProtectedFileLease>(lease);
    }
}

internal sealed class ScanTestLease(ProtectedOpenRequest request) : IProtectedFileLease
{
    public Stream Content { get; set; } = new MemoryStream([42]);
    public ProtectedFileFacts OpenedFacts { get; set; } = new(request.ExpectedPhysicalFileKey, 1,
        request.ResolvedPath, DateTimeOffset.UnixEpoch, new AllocationEvidence(4096, 1, true));
    public Func<CancellationToken, ValueTask<ProtectedFileFacts>>? Refresh { get; set; }
    public Action? OnDispose { get; set; }
    public bool Disposed { get; private set; }
    public ValueTask<ProtectedFileFacts> RefreshAsync(CancellationToken cancellationToken) =>
        Refresh?.Invoke(cancellationToken) ?? ValueTask.FromResult(OpenedFacts);
    public async ValueTask DisposeAsync()
    {
        Assert.False(Disposed);
        Disposed = true;
        await Content.DisposeAsync();
        OnDispose?.Invoke();
    }
}

internal sealed class ScanTestAnalyzer : IExactContentAnalyzer
{
    public Func<Stream, CancellationToken, ValueTask<StreamDigest>>? Hash { get; set; }
    public Func<Stream, Stream, CancellationToken, ValueTask<StreamEquality>>? Compare { get; set; }
    public ValueTask<StreamDigest> HashAsync(Stream content, long expectedLength, CancellationToken cancellationToken) =>
        Hash?.Invoke(content, cancellationToken) ?? ValueTask.FromResult(new StreamDigest(expectedLength, ScanTestData.Digest));
    public async ValueTask<StreamEquality> CompareAsync(Stream left, Stream right, long expectedLength, CancellationToken cancellationToken)
    {
        if (Compare is not null) return await Compare(left, right, cancellationToken);
        var buffer = new byte[1];
        await left.ReadExactlyAsync(buffer, cancellationToken);
        await right.ReadExactlyAsync(buffer, cancellationToken);
        var leftExtra = await left.ReadAsync(buffer, cancellationToken);
        var rightExtra = await right.ReadAsync(buffer, cancellationToken);
        if (leftExtra != 0 || rightExtra != 0) throw new IOException("Unexpected extra bytes.");
        var digest = new StreamDigest(expectedLength, ScanTestData.Digest);
        return new(true, digest, digest);
    }
}

internal sealed class ScanTestEnumerator(int count) : IScanEnumerator
{
    public Func<int, CancellationToken, Task>? BeforeFile { get; set; }
    public Exception? EndException { get; set; }
    public bool IncludeEnd { get; set; } = true;
    public async IAsyncEnumerable<ScanEnumerationEvent> EnumerateAsync(ScanRunContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeFile is not null) await BeforeFile(i, cancellationToken);
            yield return new ScanFileFound(ScanTestData.Work(i, context).File);
        }
        if (EndException is not null) throw EndException;
        if (IncludeEnd) yield return new ScanEnumerationEnded(ScanTestData.Success);
    }
}

internal sealed class FailingScanStream : MemoryStream
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<int>(new IOException("injected read failure"));
}

internal sealed class ShortReadScanStream() : MemoryStream([1, 2, 3])
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}
