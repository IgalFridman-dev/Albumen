using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using Xunit;

namespace PhotoSift.Application.Tests.Review;

public sealed class E1S4ConcurrencyAcceptanceTests
{
    [Fact]
    public async Task ReplacementRejectsBlockedWorkerReceiptBeforeLeaseDisposalAndSuccessfulPersistedEvidenceRereadSealsApproval()
    {
        await using var scheduler = new AcceptanceOwnerScheduler();
        var context = ScanTestData.Context();
        var group = ReviewData.Groups(context, 2).Single();
        var replacementGroup = group with
        {
            GroupRevision = checked(group.GroupRevision + 1),
        };
        var reader = new ScanTestReader();
        var executor = new ReviewExecutor();
        var evidenceStore = new GatedAcceptanceEvidenceStore();
        var owner = new ReviewCoordinator(scheduler, executor, evidenceStore, resultCapacity: 1);
        var protectedWork = new List<AcceptanceProtectedReceipt>();
        Exception? testFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            var opened = owner.OpenAsync(
                OpenRequest(context, group), TestContext.Current.CancellationToken);
            Assert.Equal(ReviewCommandOutcome.Committed,
                (await scheduler.DriveAsync(opened)).Outcome);
            await scheduler.DriveAsync(executor.Arrived.Task);

            var initial = owner.Current;
            var first = StartProtectedWork(owner, reader,
                Revalidation(initial.Pin!, context, group, occurrenceId: 1));
            var second = StartProtectedWork(owner, reader,
                Revalidation(initial.Pin!, context, group, occurrenceId: 2));
            protectedWork.AddRange([first, second]);
            first.ReleaseHash();
            await AcceptanceOwnerScheduler.WaitAsync(first.Submitted.Task);
            second.ReleaseHash();
            await AcceptanceOwnerScheduler.WaitAsync(second.Submitted.Task);
            var staleLease = reader.Leases.ElementAt(1);

            var oldUiCommand = Selection(initial, group, []);
            var neutralUiCommand = owner.ChangeSelectionAsync(
                oldUiCommand, TestContext.Current.CancellationToken);
            executor.Arrived = ScanTestData.Signal<ReviewTestRun>();
            var replacementPublished = new TaskCompletionSource<ReviewSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void ObserveReplacement(object? sender, ReviewSnapshot snapshot)
            {
                if (snapshot.Pin != initial.Pin
                    && snapshot.Groups.Single().Candidate.GroupRevision
                    == replacementGroup.GroupRevision)
                {
                    replacementPublished.TrySetResult(snapshot);
                }
            }

            owner.SnapshotChanged += ObserveReplacement;
            var replace = owner.OpenAsync(OpenRequest(context, replacementGroup),
                TestContext.Current.CancellationToken);
            await scheduler.DriveAsync(replacementPublished.Task);
            owner.SnapshotChanged -= ObserveReplacement;
            await AcceptanceOwnerScheduler.WaitAsync(replace);
            Assert.Equal(ReviewCommandOutcome.Committed, (await replace).Outcome);
            Assert.Equal(ReviewCommandOutcome.Neutral, (await neutralUiCommand).Outcome);
            Assert.Equal(replacementGroup.GroupRevision,
                owner.Current.Groups.Single().Candidate.GroupRevision);
            Assert.NotEqual(initial.Pin, owner.Current.Pin);
            Assert.False(second.Acknowledged.Task.IsCompleted);
            Assert.False(staleLease.Disposed);

            var staleUiCommand = owner.ChangeSelectionAsync(
                oldUiCommand, TestContext.Current.CancellationToken);
            Assert.False(await scheduler.DriveAsync(second.Acknowledged.Task));
            await AcceptanceOwnerScheduler.WaitAsync(second.Work);
            Assert.True(staleLease.Disposed);
            Assert.Equal(ReviewCommandOutcome.Stale,
                (await scheduler.DriveAsync(staleUiCommand)).Outcome);

            await scheduler.DriveAsync(executor.Arrived.Task);
            var current = owner.Current;
            var replacementFirst = StartProtectedWork(owner, reader,
                Revalidation(current.Pin!, context, replacementGroup, occurrenceId: 1));
            var replacementSecond = StartProtectedWork(owner, reader,
                Revalidation(current.Pin!, context, replacementGroup, occurrenceId: 2));
            protectedWork.AddRange([replacementFirst, replacementSecond]);
            replacementFirst.ReleaseHash();
            replacementSecond.ReleaseHash();
            Assert.True(await scheduler.DriveAsync(replacementFirst.Acknowledged.Task));
            Assert.True(await scheduler.DriveAsync(replacementSecond.Acknowledged.Task));
            Assert.Equal(ReviewStatus.Ready, owner.Current.Status);

            current = owner.Current;
            var proposed = await scheduler.DriveAsync(owner.ChangeSelectionAsync(
                Selection(current, replacementGroup, [1, 2]),
                TestContext.Current.CancellationToken));
            Assert.Equal(ReviewCommandOutcome.OverrideRequired, proposed.Outcome);

            var evidenceId = new OverrideEvidenceId(Guid.NewGuid());
            var affirm = owner.AffirmOverrideAsync(new(
                proposed.OverrideProposal!, evidenceId,
                new DateTimeOffset(2026, 9, 10, 8, 15, 0, TimeSpan.Zero)),
                TestContext.Current.CancellationToken);
            var persisted = await scheduler.DriveAsync(evidenceStore.PersistEntered.Task);
            Assert.False(affirm.IsCompleted);
            Assert.Equal(proposed.OverrideProposal!.Pin, persisted.Pin);
            Assert.Equal([1L, 2L], persisted.SelectedOccurrenceIds);

            var laneRemainsAvailable = owner.ChangeSelectionAsync(new(
                proposed.OverrideProposal.Pin,
                SelectionCommandKind.ReplaceFocusedGroup,
                [],
                Guid.NewGuid()), TestContext.Current.CancellationToken);
            Assert.Equal(ReviewCommandOutcome.Neutral,
                (await scheduler.DriveAsync(laneRemainsAvailable)).Outcome);
            evidenceStore.ReleasePersist(OverridePersistOutcome.Persisted);
            var affirmed = await scheduler.DriveAsync(affirm);
            Assert.Equal(ReviewCommandOutcome.Committed, affirmed.Outcome);
            Assert.Equal(evidenceId,
                affirmed.Snapshot.Groups.Single().OverrideEvidenceId);

            var approvalCommand = new ApproveSelectionCommand(
                affirmed.Snapshot.Pin!, affirmed.Snapshot.SelectionRevision,
                Guid.NewGuid(),
                new DateTimeOffset(2026, 9, 10, 8, 30, 0, TimeSpan.Zero));
            var approved = await scheduler.DriveAsync(owner.ApproveAsync(
                approvalCommand, TestContext.Current.CancellationToken));
            Assert.Equal(ReviewCommandOutcome.Approved, approved.Outcome);
            Assert.True(approved.Snapshot.Sealed);
            var request = Assert.IsType<ApprovedActionRequest>(approved.ApprovedRequest);
            Assert.Equal(approvalCommand.IdempotencyKey, request.RequestId);
            Assert.Equal([1L, 2L], request.SelectedOccurrenceIds);
            Assert.All(request.Items,
                item => Assert.Equal(evidenceId, item.OverrideEvidenceId));
            Assert.Equal(evidenceId, Assert.Single(evidenceStore.ReadEvidenceIds));
            Assert.Equal(persisted, evidenceStore.StoredEvidence);
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }
        finally
        {
            foreach (var receipt in protectedWork)
            {
                receipt.ReleaseHash();
            }

            evidenceStore.ReleasePersist(OverridePersistOutcome.Persisted);
            var cleanup = Task.WhenAll(protectedWork.Select(receipt => receipt.Work)
                .Append(owner.DisposeAsync().AsTask()));
            try
            {
                await scheduler.DriveCleanupAsync(cleanup);
            }
            catch (Exception exception)
            {
                cleanupFailure = cleanup.Exception is { } failures
                    ? new AggregateException(failures.Flatten().InnerExceptions)
                    : exception;
            }
        }

        if (testFailure is not null && cleanupFailure is not null)
        {
            throw new AggregateException(testFailure, cleanupFailure);
        }

        var failure = testFailure ?? cleanupFailure;
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static AcceptanceProtectedReceipt StartProtectedWork(
        IReviewResultSink owner,
        ScanTestReader reader,
        ReviewRevalidationRequest request)
    {
        var receipt = new AcceptanceProtectedReceipt();
        var analyzer = new ScanTestAnalyzer
        {
            Hash = async (_, cancellationToken) =>
                await receipt.HashReleased.Task.WaitAsync(cancellationToken),
        };
        receipt.Work = new ProtectedReviewWorker(reader, analyzer)
            .RevalidateAsync(request,
                new AcceptanceForwardingSink(owner, receipt),
                TestContext.Current.CancellationToken);
        return receipt;
    }

    private static ReviewRevalidationRequest Revalidation(
        ReviewSessionPin session,
        ScanRunContext context,
        CandidateGroup group,
        long occurrenceId) =>
        new(session, context, group.GroupId, group.GroupRevision,
            group.Members.Single(member =>
                member.Token.OccurrenceId == occurrenceId), Attempt: 1);

    private static SelectionCommand Selection(
        ReviewSnapshot snapshot,
        CandidateGroup group,
        ImmutableArray<long> occurrenceIds) =>
        new(new(snapshot.Pin!, group.GroupId, group.GroupRevision,
                snapshot.SelectionRevision),
            SelectionCommandKind.ReplaceFocusedGroup,
            occurrenceIds,
            Guid.NewGuid());

    private static ReviewOpenRequest OpenRequest(
        ScanRunContext context,
        CandidateGroup group)
    {
        var evidence = new ScopePreflightEvidence(
            context.Scope, ScopeVolumeKind.Fixed, ScopeCloudState.Local,
            ScopeTraversalState.Complete, new(2, 0, 0, 0), new(0, 0));
        var scope = new ShellSnapshot(
            [], "Ready", ScopeSessionState.Ready, context.Scope.Selection,
            ScopePreflightPolicy.Evaluate(evidence), Revision: context.Pin.ScopeRevision);
        var scan = new ScanSnapshot(
            context.Pin, ScanStatus.Complete, ScanTestData.Success,
            group.Members, [group], new(2, 2, 0, 0, 0, 0));
        return new(scope,
            new(context.Pin.ScopeRevision, context.Pin.RuleSetRevision), scan);
    }
}

internal sealed class AcceptanceProtectedReceipt
{
    internal TaskCompletionSource<StreamDigest> HashReleased { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Submitted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource<bool> Acknowledged { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Work { get; set; } = Task.CompletedTask;

    internal void ReleaseHash() => HashReleased.TrySetResult(
        new StreamDigest(1, ScanTestData.Digest));
}

internal sealed class AcceptanceForwardingSink(
    IReviewResultSink inner,
    AcceptanceProtectedReceipt receipt) : IReviewResultSink
{
    public async ValueTask<bool> CommitRevalidationAsync(
        ReviewRevalidationEvidence evidence)
    {
        var operation = inner.CommitRevalidationAsync(evidence);
        receipt.Submitted.TrySetResult();
        var accepted = await operation;
        receipt.Acknowledged.TrySetResult(accepted);
        return accepted;
    }

    public async ValueTask<bool> FailRevalidationAsync(
        ReviewRevalidationRequest request,
        ReviewUnavailableReason reason)
    {
        var operation = inner.FailRevalidationAsync(request, reason);
        receipt.Submitted.TrySetResult();
        var accepted = await operation;
        receipt.Acknowledged.TrySetResult(accepted);
        return accepted;
    }
}

internal sealed class GatedAcceptanceEvidenceStore : IOverrideEvidenceStore
{
    private readonly TaskCompletionSource<OverridePersistResult> persistReceipt =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<DeliberateOverrideEvidence> PersistEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ConcurrentQueue<OverrideEvidenceId> ReadEvidenceIds { get; } = new();
    internal DeliberateOverrideEvidence? StoredEvidence { get; private set; }

    public async ValueTask<OverridePersistResult> PersistAsync(
        DeliberateOverrideEvidence evidence,
        CancellationToken cancellationToken)
    {
        PersistEntered.TrySetResult(evidence);
        var receipt = await persistReceipt.Task.WaitAsync(cancellationToken);
        if (receipt.Outcome is OverridePersistOutcome.Persisted
            or OverridePersistOutcome.ExactRetry)
        {
            StoredEvidence = evidence;
        }

        return receipt;
    }

    public ValueTask<OverrideReadResult> ReadAsync(
        OverrideEvidenceId evidenceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadEvidenceIds.Enqueue(evidenceId);
        return ValueTask.FromResult(
            StoredEvidence is { } evidence && evidence.EvidenceId == evidenceId
                ? new OverrideReadResult(OverrideReadOutcome.Found, evidence)
                : new OverrideReadResult(OverrideReadOutcome.Missing, null));
    }

    internal void ReleasePersist(OverridePersistOutcome outcome) =>
        persistReceipt.TrySetResult(new(outcome));
}

internal sealed class AcceptanceOwnerScheduler : IWorkflowScheduler, IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Queue<Action> callbacks = new();
    private readonly SemaphoreSlim permits = new(0);
    private readonly SemaphoreSlim completed = new(0);
    private readonly CancellationTokenSource lifetime = new();
    private TaskCompletionSource callbackPosted = NewSignal();
    private Task? inFlightStep;
    private readonly Task pump;

    internal AcceptanceOwnerScheduler()
    {
        pump = Task.Run(PumpAsync, CancellationToken.None);
    }

    public void Post(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (sync)
        {
            callbacks.Enqueue(callback);
            callbackPosted.TrySetResult();
        }
    }

    internal async Task DriveAsync(Task target)
    {
        using var deadline = OperationDeadline();
        try
        {
            await DriveCoreAsync(target, deadline.Token);
            await target;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The owner-lane drive did not settle before its deadline.");
        }
    }

    internal async Task<T> DriveAsync<T>(Task<T> target)
    {
        using var deadline = OperationDeadline();
        try
        {
            await DriveCoreAsync(target, deadline.Token);
            return await target;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The owner-lane drive did not settle before its deadline.");
        }
    }

    internal static async Task WaitAsync(Task target)
    {
        using var deadline = OperationDeadline();
        try
        {
            await target.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The acceptance barrier did not settle before its deadline.");
        }
    }

    internal async Task DriveCleanupAsync(Task target)
    {
        using var deadline = new CancellationTokenSource();
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await DriveCoreAsync(target, deadline.Token);
            await target;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The cleanup drain did not settle before its deadline.");
        }
    }

    private async Task DriveCoreAsync(Task target, CancellationToken cancellationToken)
    {
        await SettleInFlightStepAsync(cancellationToken);
        while (!target.IsCompleted)
        {
            var posted = WaitForCallbackAsync();
            var ready = await Task.WhenAny(target, posted).WaitAsync(cancellationToken);
            if (ready == target)
            {
                break;
            }

            await RunOneStepAsync(cancellationToken);
        }
    }

    private async Task RunOneStepAsync(CancellationToken cancellationToken)
    {
        await SettleInFlightStepAsync(cancellationToken);
        // This scheduler-owned waiter must outlive a canceled assertion drive.
        inFlightStep = completed.WaitAsync(CancellationToken.None);
        permits.Release();
        await SettleInFlightStepAsync(cancellationToken);
    }

    private async Task SettleInFlightStepAsync(CancellationToken cancellationToken)
    {
        if (inFlightStep is not { } step)
        {
            return;
        }

        await step.WaitAsync(cancellationToken);
        if (ReferenceEquals(inFlightStep, step))
        {
            inFlightStep = null;
        }
    }

    private Task WaitForCallbackAsync()
    {
        lock (sync)
        {
            return callbacks.Count == 0 ? callbackPosted.Task : Task.CompletedTask;
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await permits.WaitAsync(lifetime.Token);
                Action callback;
                lock (sync)
                {
                    if (callbacks.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "A pump permit requires a queued owner callback.");
                    }

                    callback = callbacks.Dequeue();
                    if (callbacks.Count == 0)
                    {
                        callbackPosted = NewSignal();
                    }
                }

                try
                {
                    callback();
                }
                finally
                {
                    completed.Release();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task StopAsync()
    {
        lifetime.Cancel();
        await pump;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lifetime.Dispose();
        permits.Dispose();
        completed.Dispose();
    }

    private static CancellationTokenSource OperationDeadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        return deadline;
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
