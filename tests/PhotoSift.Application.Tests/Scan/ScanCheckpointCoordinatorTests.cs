using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
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

    private static Task AssertOwnerResponsiveWhilePersistenceWaits(CoordinatorFixture fixture) =>
        fixture.Pump(fixture.Owner.InvalidateAsync(0, 0));
}
