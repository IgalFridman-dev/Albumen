using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
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
    private readonly ConcurrentQueue<System.Action> callbacks = new();
    public void Post(System.Action callback) => callbacks.Enqueue(callback);
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
    public System.Action? OnDispose { get; set; }
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

internal sealed class CoordinatorIdentityStore : IScanIdentityStore
{
    private readonly Channel<CoordinatorIdentityAttempt> attempts =
        Channel.CreateUnbounded<CoordinatorIdentityAttempt>();
    private readonly ConcurrentDictionary<Guid, ScanIdentityRecord> scans = new();
    public ConcurrentQueue<ScanIdentityRecord> ScanWrites { get; } = new();
    public int OccurrenceWriteCount;
    public bool HoldWrites { get; set; }

    public async ValueTask<ScanIdentityPersistResult> PersistScanAsync(
        ScanIdentityRecord record,
        CancellationToken cancellationToken)
    {
        ScanWrites.Enqueue(record);
        var outcome = ScanIdentityPersistOutcome.Persisted;
        if (HoldWrites)
        {
            var attempt = new CoordinatorIdentityAttempt(record);
            await attempts.Writer.WriteAsync(attempt, cancellationToken);
            outcome = await attempt.Completion.Task.WaitAsync(cancellationToken);
        }
        if (outcome is ScanIdentityPersistOutcome.Persisted or ScanIdentityPersistOutcome.ExactRetry)
            scans.TryAdd(record.Pin.ScanId, record);
        return new(outcome);
    }

    public ValueTask<ScanIdentityPersistResult> PersistOccurrenceAsync(
        ScanOccurrenceIdentityRecord record,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref OccurrenceWriteCount);
        return ValueTask.FromResult(new ScanIdentityPersistResult(ScanIdentityPersistOutcome.Persisted));
    }

    public ValueTask<ScanIdentityReadResult> ReadScanAsync(Guid scanId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(scans.TryGetValue(scanId, out var record)
            ? new ScanIdentityReadResult(ScanIdentityReadOutcome.Found, record)
            : new ScanIdentityReadResult(ScanIdentityReadOutcome.Missing, null));

    public ValueTask<ScanOccurrenceIdentityReadResult> ReadOccurrencesAsync(
        Guid scanId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ScanOccurrenceIdentityReadResult(
            ScanIdentityReadOutcome.Found,
            ImmutableArray<ScanOccurrenceIdentityRecord>.Empty));

    public Task<CoordinatorIdentityAttempt> NextAttemptAsync() =>
        attempts.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
}

internal sealed class CoordinatorIdentityAttempt(ScanIdentityRecord record)
{
    public ScanIdentityRecord Record { get; } = record;
    public TaskCompletionSource<ScanIdentityPersistOutcome> Completion { get; } =
        ScanTestData.Signal<ScanIdentityPersistOutcome>();
    public void Release(ScanIdentityPersistOutcome outcome = ScanIdentityPersistOutcome.Persisted) =>
        Completion.TrySetResult(outcome);
}

internal sealed class CoordinatorCheckpointStore : IScanCheckpointStore
{
    private readonly Channel<CoordinatorCheckpointAttempt> attempts =
        Channel.CreateUnbounded<CoordinatorCheckpointAttempt>();
    private readonly object stateLock = new();
    private readonly Dictionary<Guid, ScanLifecycleCheckpoint> lifecycles = [];
    private readonly Dictionary<(Guid ScanId, long OccurrenceId), ScanOccurrenceIdentityRecord> occurrences = [];
    private readonly Dictionary<(Guid ScanId, long OccurrenceId, ScanStage Stage), ScanCheckpoint> checkpoints = [];
    private int inFlight;
    private int maximumInFlight;
    public ConcurrentQueue<object> Writes { get; } = new();
    public bool HoldWrites { get; set; }
    public int MaximumInFlight => Volatile.Read(ref maximumInFlight);

    public ValueTask<ScanCheckpointWriteResult> WriteInitialOccurrenceAsync(
        InitialOccurrenceCheckpointCommand command,
        CancellationToken cancellationToken) =>
        WriteAsync(command, () => Apply(command), cancellationToken);

    public ValueTask<ScanCheckpointWriteResult> TransitionLifecycleAsync(
        ScanLifecycleTransitionCommand command,
        CancellationToken cancellationToken) =>
        WriteAsync(command, () => Apply(command), cancellationToken);

    public ValueTask<ScanCheckpointWriteResult> TransitionStageAsync(
        ScanStageTransitionCommand command,
        CancellationToken cancellationToken) =>
        WriteAsync(command, () => Apply(command), cancellationToken);

    public ValueTask<ScanCheckpointReadResult> ReadAsync(Guid scanId, CancellationToken cancellationToken)
    {
        lock (stateLock) return ValueTask.FromResult(Snapshot(scanId));
    }

    public Task<CoordinatorCheckpointAttempt> NextAttemptAsync() =>
        attempts.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();

    private async ValueTask<ScanCheckpointWriteResult> WriteAsync(
        object command,
        System.Action apply,
        CancellationToken cancellationToken)
    {
        CoordinatorCheckpointAttempt? attempt = null;
        Writes.Enqueue(command);
        var entered = Interlocked.Increment(ref inFlight);
        UpdateMaximum(entered);
        try
        {
            ScanCheckpointWriteOutcome? forcedOutcome = null;
            if (HoldWrites)
            {
                attempt = new CoordinatorCheckpointAttempt(command);
                await attempts.Writer.WriteAsync(attempt, cancellationToken);
                forcedOutcome = await attempt.Completion.Task.WaitAsync(cancellationToken);
            }
            lock (stateLock)
            {
                var outcome = forcedOutcome ?? Evaluate(command);
                if (outcome is not (
                    ScanCheckpointWriteOutcome.Accepted or ScanCheckpointWriteOutcome.Idempotent))
                {
                    return new(outcome, null);
                }
                if (outcome == ScanCheckpointWriteOutcome.Accepted) apply();
                return new(outcome, Snapshot(ScanId(command)));
            }
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
            attempt?.Returned.TrySetResult();
        }
    }

    private ScanCheckpointWriteOutcome Evaluate(object command) => command switch
    {
        InitialOccurrenceCheckpointCommand initial => Evaluate(initial),
        ScanLifecycleTransitionCommand lifecycle => Evaluate(lifecycle),
        ScanStageTransitionCommand stage => Evaluate(stage),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    private ScanCheckpointWriteOutcome Evaluate(InitialOccurrenceCheckpointCommand command)
    {
        var token = command.Occurrence.Token;
        if (!lifecycles.ContainsKey(token.Pin.ScanId)) return ScanCheckpointWriteOutcome.Missing;
        return occurrences.ContainsKey((token.Pin.ScanId, token.OccurrenceId))
            ? ScanCheckpointWriteOutcome.Conflict
            : ScanCheckpointWriteOutcome.Accepted;
    }

    private ScanCheckpointWriteOutcome Evaluate(ScanLifecycleTransitionCommand command)
    {
        if (!lifecycles.TryGetValue(command.ScanId, out var current))
        {
            return command.ExpectedRevision == 1 && command.NextState == ScanLifecycleState.Preflight
                ? ScanCheckpointWriteOutcome.Accepted
                : ScanCheckpointWriteOutcome.Missing;
        }
        if (current.TransitionId == command.TransitionId)
            return current.State == command.NextState
                ? ScanCheckpointWriteOutcome.Idempotent
                : ScanCheckpointWriteOutcome.Conflict;
        if (current.Revision != command.ExpectedRevision) return ScanCheckpointWriteOutcome.Stale;
        return ScanTransitionPolicy.CanTransition(current.State, command.NextState)
            ? ScanCheckpointWriteOutcome.Accepted
            : ScanCheckpointWriteOutcome.IllegalTransition;
    }

    private ScanCheckpointWriteOutcome Evaluate(ScanStageTransitionCommand command)
    {
        if (!occurrences.ContainsKey((command.ScanId, command.OccurrenceId)))
            return ScanCheckpointWriteOutcome.Missing;
        if (!checkpoints.TryGetValue((command.ScanId, command.OccurrenceId, command.Stage), out var current))
            return ScanCheckpointWriteOutcome.Missing;
        if (current.TransitionId == command.TransitionId)
            return current.Outcome == command.Outcome
                ? ScanCheckpointWriteOutcome.Idempotent
                : ScanCheckpointWriteOutcome.Conflict;
        if (current.Revision != command.ExpectedRevision) return ScanCheckpointWriteOutcome.Stale;
        return ScanTransitionPolicy.CanTransition(command.Stage, current.Outcome, command.Outcome)
            ? ScanCheckpointWriteOutcome.Accepted
            : ScanCheckpointWriteOutcome.IllegalTransition;
    }

    private void Apply(ScanLifecycleTransitionCommand command)
    {
        var revision = command.NextState == ScanLifecycleState.Preflight
            ? 1
            : checked(lifecycles[command.ScanId].Revision + 1);
        lifecycles[command.ScanId] = new(
            command.ScanId, revision, command.TransitionId, command.NextState);
    }

    private void Apply(InitialOccurrenceCheckpointCommand command)
    {
        var token = command.Occurrence.Token;
        occurrences[(token.Pin.ScanId, token.OccurrenceId)] = command.Occurrence;
        checkpoints[(token.Pin.ScanId, token.OccurrenceId, ScanStage.Enumeration)] = command.Enumeration;
        checkpoints[(token.Pin.ScanId, token.OccurrenceId, ScanStage.Exact)] = command.Exact;
    }

    private void Apply(ScanStageTransitionCommand command)
    {
        var key = (command.ScanId, command.OccurrenceId, command.Stage);
        var current = checkpoints[key];
        checkpoints[key] = new(
            command.ScanId,
            command.OccurrenceId,
            command.Stage,
            checked(current.Revision + 1),
            command.TransitionId,
            command.Outcome,
            current.Prerequisite);
    }

    private ScanCheckpointReadResult Snapshot(Guid scanId)
    {
        if (!lifecycles.TryGetValue(scanId, out var lifecycle))
            return new(ScanCheckpointReadOutcome.Missing, null, [], null);
        var rows = checkpoints.Values
            .Where(row => row.ScanId == scanId)
            .OrderBy(row => row.OccurrenceId)
            .ThenBy(row => row.Stage)
            .ToArray();
        var accounting = rows.Length == 0
            ? new ScanAccounting(0, 0, 0, 0, 0, 0)
            : ScanTransitionPolicy.TryRecomputeAccounting(rows, out var recomputed)
                ? recomputed
                : throw new InvalidOperationException("The checkpoint double received contradictory stage rows.");
        return new(
            ScanCheckpointReadOutcome.Found,
            lifecycle,
            rows.Select(row => new ScanCheckpointProjection(row, row.Outcome, false)).ToImmutableArray(),
            accounting);
    }

    private static Guid ScanId(object command) => command switch
    {
        InitialOccurrenceCheckpointCommand initial => initial.Occurrence.Token.Pin.ScanId,
        ScanLifecycleTransitionCommand lifecycle => lifecycle.ScanId,
        ScanStageTransitionCommand stage => stage.ScanId,
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    private void UpdateMaximum(int value)
    {
        var current = Volatile.Read(ref maximumInFlight);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref maximumInFlight, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}

internal sealed class CoordinatorCheckpointAttempt(object command)
{
    public object Command { get; } = command;
    public TaskCompletionSource<ScanCheckpointWriteOutcome?> Completion { get; } =
        ScanTestData.Signal<ScanCheckpointWriteOutcome?>();
    public TaskCompletionSource Returned { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release(ScanCheckpointWriteOutcome? outcome = null) =>
        Completion.TrySetResult(outcome);
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
