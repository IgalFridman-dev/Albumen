using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using Xunit;

namespace PhotoSift.Application.Tests.Scan;

public sealed class ScanCheckpointCoordinatorTests
{
    private static readonly StageOutcome Running =
        new(ScanStageState.Running, ScanReason.None);
    private static readonly StageOutcome Changed =
        new(ScanStageState.FailedRetryable, ScanReason.Changed);

    [Fact]
    public async Task IdentityPreflightAndEnumeratingAreDurableBeforeRunningPublication()
    {
        await using var fixture = new CoordinatorFixture();
        fixture.Identities.HoldWrites = true;
        fixture.Checkpoints.HoldWrites = true;

        await fixture.Start();
        var identityAttempt = await PumpNext(fixture, fixture.Identities.NextAttemptAsync());

        Assert.Equal(ScanStatus.Checking, fixture.Owner.Current.Status);
        Assert.Empty(fixture.Executor.Started);
        Assert.Equal(fixture.Scope.Selection, identityAttempt.Record.Selection);
        Assert.Equal(fixture.Scope.ResolvedPath, identityAttempt.Record.ResolvedPath);
        Assert.Equal(fixture.Scope.RootIdentity, identityAttempt.Record.RootIdentity);
        Assert.Equal(TimeSpan.Zero, identityAttempt.Record.CreatedUtc.Offset);
        await AssertOwnerResponsiveWhilePersistenceWaits(fixture);

        identityAttempt.Release();
        var preflightAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
        var preflight = Assert.IsType<ScanLifecycleTransitionCommand>(preflightAttempt.Command);
        Assert.Equal(identityAttempt.Record.Pin.ScanId, preflight.ScanId);
        Assert.Equal(1, preflight.ExpectedRevision);
        Assert.Equal(ScanLifecycleState.Preflight, preflight.NextState);
        Assert.Equal(ScanStatus.Checking, fixture.Owner.Current.Status);

        preflightAttempt.Release();
        var enumeratingAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
        var enumerating = Assert.IsType<ScanLifecycleTransitionCommand>(enumeratingAttempt.Command);
        Assert.Equal(preflight.ScanId, enumerating.ScanId);
        Assert.Equal(1, enumerating.ExpectedRevision);
        Assert.Equal(ScanLifecycleState.Enumerating, enumerating.NextState);
        Assert.Equal(ScanStatus.Checking, fixture.Owner.Current.Status);

        fixture.Checkpoints.HoldWrites = false;
        enumeratingAttempt.Release();
        var run = await fixture.Run();

        Assert.Equal(run.Context.Pin, identityAttempt.Record.Pin);
        Assert.Equal(ScanStatus.Running, fixture.Owner.Current.Status);
        Assert.Equal(ScanStageState.Running, fixture.Owner.Current.Enumeration.State);
    }

    [Fact]
    public async Task RegistrationWritesIdentityAndBothInitialRowsAtomicallyBeforeVisibility()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        fixture.Checkpoints.HoldWrites = true;
        var file = ScanTestData.Work(1, run.Context).File;

        var registration = fixture.Owner.RegisterAsync(run.Context, file).AsTask();
        var attempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
        var command = Assert.IsType<InitialOccurrenceCheckpointCommand>(attempt.Command);

        Assert.False(registration.IsCompleted);
        Assert.Empty(fixture.Owner.Current.Occurrences);
        Assert.Equal(0, fixture.Identities.OccurrenceWriteCount);
        Assert.Equal(run.Context.Pin, command.Occurrence.Token.Pin);
        Assert.Equal(file.DiscoveredPath, command.Occurrence.DiscoveredPath);
        Assert.Equal(file.ResolvedPath, command.Occurrence.ResolvedPath);
        Assert.Equal(file.PhysicalFileKey, command.Occurrence.PhysicalFileKey);
        Assert.Equal(ScanStage.Enumeration, command.Enumeration.Stage);
        Assert.Equal(file.Outcome, command.Enumeration.Outcome);
        Assert.Equal(ScanStage.Exact, command.Exact.Stage);
        Assert.Equal(Running, command.Exact.Outcome);
        Assert.Equal(file.Outcome, command.Exact.Prerequisite!.Outcome);
        await AssertOwnerResponsiveWhilePersistenceWaits(fixture);

        fixture.Checkpoints.HoldWrites = false;
        attempt.Release();
        await fixture.Pump(registration);

        Assert.NotNull(await registration);
        var row = Assert.Single(fixture.Owner.Current.Occurrences);
        Assert.Equal(command.Occurrence.Token, row.Token);
        Assert.Equal(ScanStageState.Running, row.Exact.State);
    }

    [Fact]
    public async Task ConcurrentCallbacksPersistOneAtATimeAndApplyInAdmittedOrder()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        fixture.Checkpoints.HoldWrites = true;

        var first = fixture.Owner.RegisterAsync(run.Context, ScanTestData.Work(1, run.Context).File).AsTask();
        var second = fixture.Owner.RegisterAsync(run.Context, ScanTestData.Work(2, run.Context).File).AsTask();
        var firstAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
        var firstCommand = Assert.IsType<InitialOccurrenceCheckpointCommand>(firstAttempt.Command);
        var secondAttemptTask = fixture.Checkpoints.NextAttemptAsync();

        await AssertOwnerResponsiveWhilePersistenceWaits(fixture);
        Assert.False(secondAttemptTask.IsCompleted);
        Assert.Equal(1, fixture.Checkpoints.MaximumInFlight);
        Assert.Empty(fixture.Owner.Current.Occurrences);

        firstAttempt.Release();
        var secondAttempt = await PumpNext(fixture, secondAttemptTask);
        var secondCommand = Assert.IsType<InitialOccurrenceCheckpointCommand>(secondAttempt.Command);
        Assert.Equal(1, firstCommand.Occurrence.Token.OccurrenceId);
        Assert.Equal(2, secondCommand.Occurrence.Token.OccurrenceId);
        Assert.Equal(1, fixture.Checkpoints.MaximumInFlight);
        Assert.Equal(1, fixture.Owner.Current.Counts.Discovered);
        Assert.False(second.IsCompleted);

        fixture.Checkpoints.HoldWrites = false;
        secondAttempt.Release();
        await fixture.Pump(Task.WhenAll(first, second));

        Assert.Equal(2, fixture.Owner.Current.Counts.Discovered);
        Assert.Equal(new long[] { 1, 2 },
            fixture.Owner.Current.Occurrences.Select(row => row.Token.OccurrenceId).ToArray());
    }

    [Fact]
    public async Task StaleCheckpointIsNeverAppliedOrPublished()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        fixture.Checkpoints.HoldWrites = true;
        var registration = fixture.Owner.RegisterAsync(
            run.Context, ScanTestData.Work(1, run.Context).File).AsTask();
        var attempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());

        fixture.Checkpoints.HoldWrites = false;
        attempt.Release(ScanCheckpointWriteOutcome.Stale);
        await fixture.Pump(registration);
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Faulted);

        Assert.Null(await registration);
        Assert.Empty(fixture.Owner.Current.Occurrences);
        Assert.Equal(0, fixture.Owner.Current.Counts.Discovered);
    }

    [Fact]
    public async Task UnavailableCheckpointFaultsScanAndRequestsNativeCancellation()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        fixture.Checkpoints.HoldWrites = true;
        var registration = fixture.Owner.RegisterAsync(
            run.Context, ScanTestData.Work(1, run.Context).File).AsTask();
        var attempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());

        fixture.Checkpoints.HoldWrites = false;
        attempt.Release(ScanCheckpointWriteOutcome.Unavailable);
        await fixture.Pump(Task.WhenAll(registration, run.CancellationRequested.Task));
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Faulted);

        Assert.Null(await registration);
        Assert.Empty(fixture.Owner.Current.Occurrences);
        Assert.True(run.CancellationRequestCount >= 1);
        Assert.Equal(ScanReason.ProgrammingFault, fixture.Owner.Current.Enumeration.Reason);
    }

    [Fact]
    public async Task CancellationSignalsNativeWorkImmediatelyButWaitsForAdmittedPersistenceBeforeTerminalPublication()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        fixture.Checkpoints.HoldWrites = true;
        var registration = fixture.Owner.RegisterAsync(
            run.Context, ScanTestData.Work(1, run.Context).File).AsTask();
        var attempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());

        var cancellation = fixture.Owner.CancelAsync();
        await fixture.Pump(run.CancellationRequested.Task);

        Assert.False(registration.IsCompleted);
        Assert.Equal(ScanStatus.Running, fixture.Owner.Current.Status);
        fixture.Checkpoints.HoldWrites = false;
        attempt.Release();
        await fixture.Pump(Task.WhenAll(registration, cancellation));
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Incomplete);

        Assert.Equal(1, fixture.Owner.Current.Counts.Discovered);
        Assert.Equal(0, fixture.Owner.Current.Counts.Outstanding);
        Assert.Equal(1, fixture.Owner.Current.Counts.Canceled);
    }

    [Fact]
    public async Task NewRequestAfterCancellationRunsFreshPreflightWithNewIdentityAndNoReplay()
    {
        await using var fixture = new CoordinatorFixture();
        var preflightCount = 0;
        fixture.Preflight.Inspect = (_, _) =>
        {
            preflightCount++;
            return Task.FromResult(fixture.Evidence);
        };

        await fixture.Start();
        var interrupted = await fixture.Run();
        var registration = fixture.Owner.RegisterAsync(
            interrupted.Context, ScanTestData.Work(1, interrupted.Context).File).AsTask();
        await fixture.Pump(registration);
        Assert.NotNull(await registration);

        await fixture.Pump(fixture.Owner.CancelAsync());
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Incomplete);
        var interruptedCheckpoint = await fixture.Checkpoints.ReadAsync(
            interrupted.Context.Pin.ScanId, TestContext.Current.CancellationToken);

        fixture.Executor.Arrived = ScanTestData.Signal<CoordinatorRun>();
        await fixture.Start();
        var fresh = await fixture.Run();

        Assert.Equal(2, preflightCount);
        Assert.NotEqual(interrupted.Context.Pin.ScanId, fresh.Context.Pin.ScanId);
        Assert.Empty(fixture.Owner.Current.Occurrences);
        Assert.Equal(ScanLifecycleState.Incomplete, interruptedCheckpoint.Lifecycle!.State);
        Assert.Equal(2, interruptedCheckpoint.Stages.Length);
        Assert.Equal(ScanStageState.Canceled, interruptedCheckpoint.Stages.Single(
            row => row.Stored.Stage == ScanStage.Exact).Stored.Outcome.State);
    }

    [Fact]
    public async Task ReplacementLetsAdmittedOldWriteFinishWithoutUpdatingActiveSnapshot()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var old = await fixture.Run();
        fixture.Checkpoints.HoldWrites = true;
        var oldRegistration = fixture.Owner.RegisterAsync(
            old.Context, ScanTestData.Work(1, old.Context).File).AsTask();
        var oldAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());

        fixture.Identities.HoldWrites = true;
        fixture.Executor.Arrived = ScanTestData.Signal<CoordinatorRun>();
        await fixture.Start(2, 2);
        var replacementPin = fixture.Owner.Current.Pin!;
        var replacementIdentityTask = fixture.Identities.NextAttemptAsync();
        await AssertOwnerResponsiveWhilePersistenceWaits(fixture);

        Assert.Equal(ScanStatus.Checking, fixture.Owner.Current.Status);
        Assert.NotEqual(old.Context.Pin, replacementPin);
        Assert.False(replacementIdentityTask.IsCompleted);
        fixture.Checkpoints.HoldWrites = false;
        oldAttempt.Release();
        await fixture.Pump(oldRegistration);

        Assert.Equal(replacementPin, fixture.Owner.Current.Pin);
        Assert.Empty(fixture.Owner.Current.Occurrences);
        var replacementIdentity = await PumpNext(fixture, replacementIdentityTask);
        Assert.Equal(replacementPin, replacementIdentity.Record.Pin);
        fixture.Identities.HoldWrites = false;
        replacementIdentity.Release();
        var replacement = await fixture.Run();

        Assert.Equal(replacementPin, replacement.Context.Pin);
        Assert.Equal(replacementPin, fixture.Owner.Current.Pin);
        Assert.Empty(fixture.Owner.Current.Occurrences);
    }

    [Fact]
    public async Task AliasEvidenceCorrectionPersistsEveryAffectedExactRowBeforeApplyingEither()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        var key = new PhysicalFileKey(7, new FileId128(8, 9));
        var first = await Register(fixture, run, 1, key);
        var second = await Register(fixture, run, 2, key);
        var firstSignature = fixture.Owner.CommitSignatureAsync(StoreFixture.Signature(first)).AsTask();
        await fixture.Pump(firstSignature);
        Assert.True((await firstSignature).Accepted);
        Assert.Equal(1, fixture.Owner.Current.Counts.Analyzed);

        fixture.Checkpoints.HoldWrites = true;
        var baseline = StoreFixture.Signature(second);
        var conflict = baseline with
        {
            Content = new ContentRevision(
                baseline.Content.PhysicalFileKey,
                baseline.Content.ByteLength,
                new string('B', 64)),
        };
        var correction = fixture.Owner.CommitSignatureAsync(conflict).AsTask();
        var firstAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
        var firstCommand = Assert.IsType<ScanStageTransitionCommand>(firstAttempt.Command);

        Assert.Equal(1, firstCommand.OccurrenceId);
        Assert.Equal(2, firstCommand.ExpectedRevision);
        Assert.Equal(Changed, firstCommand.Outcome);
        Assert.Equal(
            new[] { ScanStageState.Succeeded, ScanStageState.Running },
            fixture.Owner.Current.Occurrences.Select(row => row.Exact.State).ToArray());
        firstAttempt.Release();

        var secondAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
        var secondCommand = Assert.IsType<ScanStageTransitionCommand>(secondAttempt.Command);
        Assert.Equal(2, secondCommand.OccurrenceId);
        Assert.Equal(1, secondCommand.ExpectedRevision);
        Assert.Equal(Changed, secondCommand.Outcome);
        Assert.False(correction.IsCompleted);
        Assert.Equal(
            new[] { ScanStageState.Succeeded, ScanStageState.Running },
            fixture.Owner.Current.Occurrences.Select(row => row.Exact.State).ToArray());

        fixture.Checkpoints.HoldWrites = false;
        secondAttempt.Release();
        await fixture.Pump(correction);

        Assert.False((await correction).Accepted);
        Assert.All(fixture.Owner.Current.Occurrences, row => Assert.Equal(Changed, row.Exact));
        Assert.Equal(new ScanCounts(2, 0, 0, 2, 0, 0), fixture.Owner.Current.Counts);
    }

    [Fact]
    public async Task ComparisonReadFailureInvalidatesEvidenceWithoutFaultingTheScan()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        var first = await Register(fixture, run, 1,
            new PhysicalFileKey(7, new FileId128(8, 1)));
        var second = await Register(fixture, run, 2,
            new PhysicalFileKey(7, new FileId128(8, 2)));
        var firstSignature = fixture.Owner.CommitSignatureAsync(StoreFixture.Signature(first)).AsTask();
        await fixture.Pump(firstSignature);
        Assert.True((await firstSignature).Accepted);
        var secondSignature = fixture.Owner.CommitSignatureAsync(StoreFixture.Signature(second)).AsTask();
        await fixture.Pump(secondSignature);
        var comparison = Assert.Single((await secondSignature).Comparisons);
        var failing = comparison.Left.Token == second.Token ? comparison.Right.Token : comparison.Left.Token;

        var failure = fixture.Owner.FailAsync(failing, ScanTestData.ReadFailure).AsTask();
        await fixture.Pump(failure);

        Assert.True(await failure);
        Assert.Equal(ScanStatus.Running, fixture.Owner.Current.Status);
        var failed = fixture.Owner.Current.Occurrences.Single(row => row.Token == failing);
        var peer = fixture.Owner.Current.Occurrences.Single(row => row.Token != failing);
        Assert.Equal(Changed, failed.Exact);
        Assert.Equal(ScanTestData.Success, peer.Exact);
        var durable = await fixture.Checkpoints.ReadAsync(
            run.Context.Pin.ScanId, TestContext.Current.CancellationToken);
        Assert.Equal(Changed, durable.Stages.Single(
            row => row.Stored.OccurrenceId == failing.OccurrenceId
                && row.Stored.Stage == ScanStage.Exact).Stored.Outcome);
    }

    [Fact]
    public async Task ReplacedScanAppliesCommittedTerminalRowPrivatelyBeforeFinalizingLifecycle()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var old = await fixture.Run();
        var work = await Register(fixture, old, 1,
            new PhysicalFileKey(7, new FileId128(8, 1)));
        fixture.Checkpoints.HoldWrites = true;
        CoordinatorCheckpointAttempt? terminalAttempt = null;
        CoordinatorCheckpointAttempt? finalAttempt = null;
        var replacementPreflight = ScanTestData.Signal<ScopePreflightEvidence>();
        try
        {
            var signature = fixture.Owner.CommitSignatureAsync(StoreFixture.Signature(work)).AsTask();
            terminalAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
            var terminal = Assert.IsType<ScanStageTransitionCommand>(terminalAttempt.Command);
            Assert.Equal(ScanTestData.Success, terminal.Outcome);

            fixture.Preflight.Inspect = (_, _) => replacementPreflight.Task;
            fixture.Executor.Arrived = ScanTestData.Signal<CoordinatorRun>();
            await fixture.Start(2, 2);
            var replacementPin = fixture.Owner.Current.Pin;
            Assert.Equal(ScanStatus.Checking, fixture.Owner.Current.Status);
            terminalAttempt.Release();
            await fixture.Pump(terminalAttempt.Returned.Task);
            await fixture.Pump(signature);
            await fixture.Pump(old.Disposed.Task);
            finalAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());

            var lifecycle = Assert.IsType<ScanLifecycleTransitionCommand>(finalAttempt.Command);
            Assert.Equal(old.Context.Pin.ScanId, lifecycle.ScanId);
            Assert.Equal(ScanLifecycleState.Incomplete, lifecycle.NextState);
            fixture.Checkpoints.HoldWrites = false;
            finalAttempt.Release();
            await fixture.Pump(finalAttempt.Returned.Task);
            Assert.Equal(replacementPin, fixture.Owner.Current.Pin);
            Assert.Equal(ScanStatus.Checking, fixture.Owner.Current.Status);
            replacementPreflight.SetResult(fixture.Evidence);
            _ = await fixture.Run();
            var durable = await fixture.Checkpoints.ReadAsync(
                old.Context.Pin.ScanId, TestContext.Current.CancellationToken);
            Assert.Equal(ScanLifecycleState.Incomplete, durable.Lifecycle!.State);
            Assert.Equal(ScanTestData.Success, durable.Stages.Single(
                row => row.Stored.OccurrenceId == work.Token.OccurrenceId
                    && row.Stored.Stage == ScanStage.Exact).Stored.Outcome);
        }
        finally
        {
            fixture.Checkpoints.HoldWrites = false;
            terminalAttempt?.Release();
            finalAttempt?.Release();
            replacementPreflight.TrySetResult(fixture.Evidence);
        }
    }

    [Fact]
    public async Task FreshPreflightRejectionIsDurableBeforeIncompletePublication()
    {
        await using var fixture = new CoordinatorFixture();
        fixture.Preflight.Inspect = (_, _) => Task.FromResult(fixture.Evidence with
        {
            Scope = fixture.Scope with { RootIdentity = new DirectoryIdentity(9, 9, 9) },
        });
        fixture.Identities.HoldWrites = true;
        fixture.Checkpoints.HoldWrites = true;
        var terminal = ScanTestData.Signal<ScanSnapshot>();
        fixture.Owner.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Status is ScanStatus.Incomplete or ScanStatus.Faulted)
                terminal.TrySetResult(snapshot);
        };

        await fixture.Start();
        var identityTask = fixture.Identities.NextAttemptAsync();
        var first = Task.WhenAny(identityTask, terminal.Task);
        await fixture.Pump(first);
        var winner = await first;
        CoordinatorIdentityAttempt? identityAttempt = null;
        if (identityTask.IsCompletedSuccessfully)
        {
            identityAttempt = await identityTask;
            fixture.Identities.HoldWrites = false;
            identityAttempt.Release();
        }
        Assert.Same(identityTask, winner);
        Assert.Equal(ScanStatus.Checking, fixture.Owner.Current.Status);
        Assert.Equal(fixture.Scope.Selection, identityAttempt!.Record.Selection);
        Assert.Equal(fixture.Scope.ResolvedPath, identityAttempt.Record.ResolvedPath);
        Assert.Equal(fixture.Scope.RootIdentity, identityAttempt.Record.RootIdentity);

        var preflightAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
        var preflight = Assert.IsType<ScanLifecycleTransitionCommand>(preflightAttempt.Command);
        Assert.Equal(ScanLifecycleState.Preflight, preflight.NextState);
        Assert.False(terminal.Task.IsCompleted);
        preflightAttempt.Release();
        var incompleteAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
        var incomplete = Assert.IsType<ScanLifecycleTransitionCommand>(incompleteAttempt.Command);
        Assert.Equal(ScanLifecycleState.Incomplete, incomplete.NextState);
        Assert.False(terminal.Task.IsCompleted);

        fixture.Checkpoints.HoldWrites = false;
        incompleteAttempt.Release();
        await fixture.Pump(terminal.Task);
        Assert.Equal(ScanStatus.Incomplete, (await terminal.Task).Status);
        Assert.Equal(ScanReason.UnknownSafety, (await terminal.Task).Enumeration.Reason);
        Assert.Empty(fixture.Executor.Started);
    }

    [Theory]
    [InlineData(false, ScanCheckpointWriteOutcome.Stale)]
    [InlineData(false, ScanCheckpointWriteOutcome.Conflict)]
    [InlineData(true, ScanCheckpointWriteOutcome.Stale)]
    [InlineData(true, ScanCheckpointWriteOutcome.Conflict)]
    public async Task StartupSecondTransitionLogicalRejectionDurablyFaultsCanonicalNonterminalLifecycle(
        bool rejectedPreflight,
        ScanCheckpointWriteOutcome expectedRejection)
    {
        await using var fixture = new CoordinatorFixture();
        if (rejectedPreflight)
        {
            fixture.Preflight.Inspect = (_, _) => Task.FromResult(fixture.Evidence with
            {
                Scope = fixture.Scope with { RootIdentity = new DirectoryIdentity(9, 9, 9) },
            });
        }
        fixture.Checkpoints.HoldWrites = true;
        var terminal = ObserveTerminal(fixture);
        CoordinatorCheckpointAttempt? preflightAttempt = null;
        CoordinatorCheckpointAttempt? secondAttempt = null;
        CoordinatorCheckpointAttempt? faultAttempt = null;
        try
        {
            await fixture.Start();
            preflightAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
            var preflight = Assert.IsType<ScanLifecycleTransitionCommand>(preflightAttempt.Command);
            Assert.Equal(ScanLifecycleState.Preflight, preflight.NextState);
            preflightAttempt.Release();
            secondAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
            var second = Assert.IsType<ScanLifecycleTransitionCommand>(secondAttempt.Command);
            Assert.Equal(
                rejectedPreflight ? ScanLifecycleState.Incomplete : ScanLifecycleState.Enumerating,
                second.NextState);

            long canonicalRevision = expectedRejection == ScanCheckpointWriteOutcome.Stale ? 2 : 1;
            fixture.Checkpoints.SetCanonicalLifecycle(expectedRejection switch
            {
                ScanCheckpointWriteOutcome.Stale => new(
                    second.ScanId,
                    canonicalRevision,
                    Guid.Parse("0ac6c60f-54dd-4560-bd8e-e31c9262cd5a"),
                    ScanLifecycleState.Enumerating),
                ScanCheckpointWriteOutcome.Conflict => new(
                    second.ScanId,
                    canonicalRevision,
                    second.TransitionId,
                    ScanLifecycleState.Preflight),
                _ => throw new ArgumentOutOfRangeException(nameof(expectedRejection)),
            });

            secondAttempt.Release();
            var faultAttemptTask = fixture.Checkpoints.NextAttemptAsync();
            var winner = await PumpNext(fixture, Task.WhenAny(faultAttemptTask, terminal));
            Assert.Same(faultAttemptTask, winner);
            Assert.False(terminal.IsCompleted);
            faultAttempt = await faultAttemptTask;
            var fault = Assert.IsType<ScanLifecycleTransitionCommand>(faultAttempt.Command);
            Assert.Equal(second.ScanId, fault.ScanId);
            Assert.Equal(canonicalRevision, fault.ExpectedRevision);
            Assert.Equal(ScanLifecycleState.Faulted, fault.NextState);

            fixture.Checkpoints.HoldWrites = false;
            faultAttempt.Release();
            await fixture.Pump(terminal);
            Assert.Equal(ScanStatus.Faulted, (await terminal).Status);
            var durable = await fixture.Checkpoints.ReadAsync(
                second.ScanId, TestContext.Current.CancellationToken);
            Assert.Equal(ScanLifecycleState.Faulted, durable.Lifecycle!.State);
            Assert.Equal(canonicalRevision + 1, durable.Lifecycle.Revision);
            Assert.Equal(3, LifecycleWrites(fixture, second.ScanId).Length);
            Assert.Empty(fixture.Executor.Started);
        }
        finally
        {
            fixture.Checkpoints.HoldWrites = false;
            preflightAttempt?.Release();
            secondAttempt?.Release();
            faultAttempt?.Release();
        }
    }

    [Fact]
    public async Task StartupIllegalTransitionLeavesTerminalCanonicalLifecycleUnchanged()
    {
        await using var fixture = new CoordinatorFixture();
        fixture.Checkpoints.HoldWrites = true;
        fixture.Checkpoints.HoldReads = true;
        var terminal = ObserveTerminal(fixture);
        CoordinatorCheckpointAttempt? preflightAttempt = null;
        CoordinatorCheckpointAttempt? secondAttempt = null;
        CoordinatorCheckpointReadAttempt? readAttempt = null;
        try
        {
            await fixture.Start();
            preflightAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
            preflightAttempt.Release();
            secondAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
            var second = Assert.IsType<ScanLifecycleTransitionCommand>(secondAttempt.Command);
            fixture.Checkpoints.SetCanonicalLifecycle(new(
                second.ScanId,
                1,
                Guid.Parse("66582e84-57b0-4c38-8be3-0898fe19ea71"),
                ScanLifecycleState.Incomplete));

            fixture.Checkpoints.HoldWrites = false;
            secondAttempt.Release();
            var readAttemptTask = fixture.Checkpoints.NextReadAttemptAsync();
            var winner = await PumpNext(fixture, Task.WhenAny(readAttemptTask, terminal));
            Assert.Same(readAttemptTask, winner);
            Assert.False(terminal.IsCompleted);
            readAttempt = await readAttemptTask;
            Assert.Equal(second.ScanId, readAttempt.ScanId);
            fixture.Checkpoints.HoldReads = false;
            readAttempt.Release();

            await fixture.Pump(terminal);
            Assert.Equal(ScanStatus.Faulted, (await terminal).Status);
            var durable = await fixture.Checkpoints.ReadAsync(
                second.ScanId, TestContext.Current.CancellationToken);
            Assert.Equal(ScanLifecycleState.Incomplete, durable.Lifecycle!.State);
            Assert.Equal(1, durable.Lifecycle.Revision);
            Assert.Equal(
                Guid.Parse("66582e84-57b0-4c38-8be3-0898fe19ea71"),
                durable.Lifecycle.TransitionId);
            Assert.Equal(2, LifecycleWrites(fixture, second.ScanId).Length);
            Assert.DoesNotContain(
                LifecycleWrites(fixture, second.ScanId),
                command => command.NextState == ScanLifecycleState.Faulted);
        }
        finally
        {
            fixture.Checkpoints.HoldWrites = false;
            fixture.Checkpoints.HoldReads = false;
            preflightAttempt?.Release();
            secondAttempt?.Release();
            readAttempt?.Release();
        }
    }

    [Fact]
    public async Task StartupFaultSettlementLogicalRejectionIsNotRetried()
    {
        await using var fixture = new CoordinatorFixture();
        fixture.Checkpoints.HoldWrites = true;
        var terminal = ObserveTerminal(fixture);
        CoordinatorCheckpointAttempt? preflightAttempt = null;
        CoordinatorCheckpointAttempt? secondAttempt = null;
        CoordinatorCheckpointAttempt? faultAttempt = null;
        try
        {
            await fixture.Start();
            preflightAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
            preflightAttempt.Release();
            secondAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
            var second = Assert.IsType<ScanLifecycleTransitionCommand>(secondAttempt.Command);
            fixture.Checkpoints.SetCanonicalLifecycle(new(
                second.ScanId,
                2,
                Guid.Parse("1943110c-6291-4428-8cf9-14210ec695ef"),
                ScanLifecycleState.Enumerating));

            secondAttempt.Release();
            var faultAttemptTask = fixture.Checkpoints.NextAttemptAsync();
            var winner = await PumpNext(fixture, Task.WhenAny(faultAttemptTask, terminal));
            Assert.Same(faultAttemptTask, winner);
            faultAttempt = await faultAttemptTask;
            var fault = Assert.IsType<ScanLifecycleTransitionCommand>(faultAttempt.Command);
            Assert.Equal(2, fault.ExpectedRevision);
            Assert.Equal(ScanLifecycleState.Faulted, fault.NextState);
            fixture.Checkpoints.SetCanonicalLifecycle(new(
                second.ScanId,
                3,
                Guid.Parse("398a0e8b-f636-4a71-95dd-dbe3d426da4b"),
                ScanLifecycleState.Pausing));

            fixture.Checkpoints.HoldWrites = false;
            faultAttempt.Release();
            await fixture.Pump(terminal);
            await fixture.Pump(fixture.Owner.DisposeAsync().AsTask());

            Assert.Equal(ScanStatus.Faulted, fixture.Owner.Current.Status);
            var durable = await fixture.Checkpoints.ReadAsync(
                second.ScanId, TestContext.Current.CancellationToken);
            Assert.Equal(ScanLifecycleState.Pausing, durable.Lifecycle!.State);
            Assert.Equal(3, durable.Lifecycle.Revision);
            Assert.Equal(
                Guid.Parse("398a0e8b-f636-4a71-95dd-dbe3d426da4b"),
                durable.Lifecycle.TransitionId);
            Assert.Equal(3, LifecycleWrites(fixture, second.ScanId).Length);
        }
        finally
        {
            fixture.Checkpoints.HoldWrites = false;
            preflightAttempt?.Release();
            secondAttempt?.Release();
            faultAttempt?.Release();
        }
    }

    [Theory]
    [InlineData(ScanCheckpointWriteOutcome.Stale)]
    [InlineData(ScanCheckpointWriteOutcome.Conflict)]
    [InlineData(ScanCheckpointWriteOutcome.IllegalTransition)]
    public async Task LogicalCheckpointRejectionDurablyFaultsReachableScan(
        ScanCheckpointWriteOutcome rejection)
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        var work = await Register(fixture, run, 1,
            new PhysicalFileKey(7, new FileId128(8, 1)));
        fixture.Checkpoints.HoldWrites = true;
        var signature = fixture.Owner.CommitSignatureAsync(StoreFixture.Signature(work)).AsTask();
        var rejectedAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());

        fixture.Checkpoints.HoldWrites = false;
        rejectedAttempt.Release(rejection);
        await fixture.Pump(Task.WhenAll(signature, run.CancellationRequested.Task));
        Assert.False((await signature).Accepted);
        await fixture.Pump(fixture.Owner.DisposeAsync().AsTask());

        var durable = await fixture.Checkpoints.ReadAsync(
            run.Context.Pin.ScanId, TestContext.Current.CancellationToken);
        Assert.Equal(ScanLifecycleState.Faulted, durable.Lifecycle!.State);
        Assert.Equal(new StageOutcome(
            ScanStageState.FailedTerminal, ScanReason.ProgrammingFault), durable.Stages.Single(
                row => row.Stored.OccurrenceId == work.Token.OccurrenceId
                    && row.Stored.Stage == ScanStage.Exact).Stored.Outcome);
    }

    [Fact]
    public async Task EnumerationAndTerminalSnapshotsWaitForLifecyclePersistence()
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        fixture.Checkpoints.HoldWrites = true;

        var ending = fixture.Owner.EnumerationEndedAsync(run.Context, ScanTestData.Success).AsTask();
        var exactAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
        var exact = Assert.IsType<ScanLifecycleTransitionCommand>(exactAttempt.Command);
        Assert.Equal(ScanLifecycleState.Exact, exact.NextState);
        Assert.Equal(2, exact.ExpectedRevision);
        Assert.Equal(Running, fixture.Owner.Current.Enumeration);

        exactAttempt.Release();
        await fixture.Pump(ending);
        Assert.Equal(ScanTestData.Success, fixture.Owner.Current.Enumeration);
        run.Done.TrySetResult();
        var completeAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
        var complete = Assert.IsType<ScanLifecycleTransitionCommand>(completeAttempt.Command);
        Assert.Equal(ScanLifecycleState.Complete, complete.NextState);
        Assert.Equal(3, complete.ExpectedRevision);
        Assert.NotEqual(ScanStatus.Complete, fixture.Owner.Current.Status);

        fixture.Checkpoints.HoldWrites = false;
        completeAttempt.Release();
        await fixture.Until(snapshot => snapshot.Status == ScanStatus.Complete);
    }

    [Fact]
    public Task CompleteObserverFailureCannotReplaceDurableTerminalSnapshotWithFault() =>
        AssertTerminalObserverFailureIsIsolatedAsync(
            endEnumeration: true,
            ScanStatus.Complete,
            ScanLifecycleState.Complete);

    [Fact]
    public Task IncompleteObserverFailureCannotReplaceDurableTerminalSnapshotWithFault() =>
        AssertTerminalObserverFailureIsIsolatedAsync(
            endEnumeration: false,
            ScanStatus.Incomplete,
            ScanLifecycleState.Incomplete);

    [Theory]
    [InlineData(true, ScanStatus.Complete, ScanLifecycleState.Complete)]
    [InlineData(false, ScanStatus.Incomplete, ScanLifecycleState.Incomplete)]
    public async Task CallbackArrivingDuringTerminalPersistenceCannotOverwriteTerminalTruth(
        bool endEnumeration,
        ScanStatus expectedStatus,
        ScanLifecycleState expectedLifecycle)
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        if (endEnumeration)
            await fixture.Pump(
                fixture.Owner.EnumerationEndedAsync(run.Context, ScanTestData.Success).AsTask());

        fixture.Checkpoints.HoldWrites = true;
        var terminal = ScanTestData.Signal<ScanSnapshot>();
        fixture.Owner.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Status == expectedStatus)
                terminal.TrySetResult(snapshot);
        };
        CoordinatorCheckpointAttempt? terminalAttempt = null;
        CoordinatorCheckpointAttempt? lateAttempt = null;
        try
        {
            run.Done.TrySetResult();
            terminalAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
            var terminalCommand = Assert.IsType<ScanLifecycleTransitionCommand>(
                terminalAttempt.Command);
            Assert.Equal(expectedLifecycle, terminalCommand.NextState);

            var late = fixture.Owner.RegisterAsync(
                run.Context, ScanTestData.Work(1, run.Context).File).AsTask();
            await AssertOwnerResponsiveWhilePersistenceWaits(fixture);
            var admissionClosedBeforeCommit = late.IsCompleted;

            if (admissionClosedBeforeCommit)
                fixture.Checkpoints.HoldWrites = false;
            terminalAttempt.Release();
            if (!admissionClosedBeforeCommit)
            {
                lateAttempt = await PumpNext(fixture, fixture.Checkpoints.NextAttemptAsync());
                Assert.IsType<InitialOccurrenceCheckpointCommand>(lateAttempt.Command);
                fixture.Checkpoints.HoldWrites = false;
                lateAttempt.Release(ScanCheckpointWriteOutcome.IllegalTransition);
            }
            await fixture.Pump(Task.WhenAll(late, terminal.Task));

            Assert.Equal(expectedStatus, fixture.Owner.Current.Status);
            Assert.True(admissionClosedBeforeCommit,
                "Finalization must close new result admission before awaiting its terminal write.");
            Assert.Null(await late);
            var durable = await fixture.Checkpoints.ReadAsync(
                run.Context.Pin.ScanId, TestContext.Current.CancellationToken);
            Assert.Equal(expectedLifecycle, durable.Lifecycle!.State);
            Assert.Empty(durable.Stages);
            Assert.DoesNotContain(
                fixture.Checkpoints.Writes,
                command => command is InitialOccurrenceCheckpointCommand);
        }
        finally
        {
            fixture.Checkpoints.HoldWrites = false;
            terminalAttempt?.Release();
            lateAttempt?.Release();
        }
    }

    private static async Task AssertTerminalObserverFailureIsIsolatedAsync(
        bool endEnumeration,
        ScanStatus expectedStatus,
        ScanLifecycleState expectedLifecycle)
    {
        await using var fixture = new CoordinatorFixture();
        await fixture.Start();
        var run = await fixture.Run();
        if (endEnumeration)
            await fixture.Pump(
                fixture.Owner.EnumerationEndedAsync(run.Context, ScanTestData.Success).AsTask());

        var observed = ScanTestData.Signal<ScanSnapshot>();
        fixture.Owner.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Status == expectedStatus)
                throw new InvalidOperationException("terminal observer failure");
        };
        fixture.Owner.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Status == expectedStatus)
                observed.TrySetResult(snapshot);
        };

        run.Done.TrySetResult();
        await fixture.Pump(observed.Task);

        Assert.Equal(expectedStatus, (await observed.Task).Status);
        Assert.Equal(expectedStatus, fixture.Owner.Current.Status);
        var durable = await fixture.Checkpoints.ReadAsync(
            run.Context.Pin.ScanId, TestContext.Current.CancellationToken);
        Assert.Equal(expectedLifecycle, durable.Lifecycle!.State);
        Assert.DoesNotContain(
            LifecycleWrites(fixture, run.Context.Pin.ScanId),
            command => command.NextState == ScanLifecycleState.Faulted);
    }

    private static async Task<OccurrenceWork> Register(
        CoordinatorFixture fixture,
        CoordinatorRun run,
        long occurrenceId,
        PhysicalFileKey key)
    {
        var file = ScanTestData.Work(occurrenceId, run.Context).File with { PhysicalFileKey = key };
        var registration = fixture.Owner.RegisterAsync(run.Context, file).AsTask();
        await fixture.Pump(registration);
        return Assert.IsType<OccurrenceWork>(await registration);
    }

    private static async Task<T> PumpNext<T>(CoordinatorFixture fixture, Task<T> task)
    {
        await fixture.Pump(task);
        return await task;
    }

    private static Task<ScanSnapshot> ObserveTerminal(CoordinatorFixture fixture)
    {
        var terminal = ScanTestData.Signal<ScanSnapshot>();
        fixture.Owner.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Status is ScanStatus.Incomplete or ScanStatus.Faulted)
                terminal.TrySetResult(snapshot);
        };
        return terminal.Task;
    }

    private static ScanLifecycleTransitionCommand[] LifecycleWrites(
        CoordinatorFixture fixture,
        Guid scanId) =>
        fixture.Checkpoints.Writes.OfType<ScanLifecycleTransitionCommand>()
            .Where(command => command.ScanId == scanId)
            .ToArray();

    private static Task AssertOwnerResponsiveWhilePersistenceWaits(CoordinatorFixture fixture) =>
        fixture.Pump(fixture.Owner.InvalidateAsync(0, 0));
}
