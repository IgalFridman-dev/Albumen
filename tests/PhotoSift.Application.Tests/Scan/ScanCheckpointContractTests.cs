using System.Collections.Immutable;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Application.Tests.Scan;

public sealed class ScanCheckpointContractTests
{
    private static readonly Guid ScanId = Guid.Parse("d318e25e-0c09-42df-918c-d63337ad1b9f");
    private static readonly Guid TransitionId = Guid.Parse("f2945a1f-9f81-4a44-bc91-d44958254cee");
    private static readonly ScanPin Pin = new(ScanId, 1, 2, 3);
    private static readonly StageOutcome Succeeded = new(ScanStageState.Succeeded, ScanReason.None);

    [Fact]
    public void LifecycleCheckpointRejectsEmptyIdsNonpositiveRevisionAndUndefinedState()
    {
        Assert.Throws<ArgumentException>(() =>
            new ScanLifecycleCheckpoint(Guid.Empty, 1, TransitionId, ScanLifecycleState.Preflight));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScanLifecycleCheckpoint(ScanId, 0, TransitionId, ScanLifecycleState.Preflight));
        Assert.Throws<ArgumentException>(() =>
            new ScanLifecycleCheckpoint(ScanId, 1, Guid.Empty, ScanLifecycleState.Preflight));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScanLifecycleCheckpoint(ScanId, 1, TransitionId, (ScanLifecycleState)999));
    }

    [Fact]
    public void StageCheckpointRejectsInvalidIdentityRevisionAndEnums()
    {
        Assert.Throws<ArgumentException>(() => Checkpoint(scanId: Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => Checkpoint(occurrenceId: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Checkpoint(revision: 0));
        Assert.Throws<ArgumentException>(() => Checkpoint(transitionId: Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => Checkpoint(stage: (ScanStage)999));
    }

    [Fact]
    public void ExactCheckpointRequiresCanonicalEnumerationPrerequisite()
    {
        Assert.Throws<ArgumentException>(() => new ScanCheckpoint(
            ScanId,
            7,
            ScanStage.Exact,
            1,
            TransitionId,
            Succeeded,
            null,
            false));
        Assert.Throws<ArgumentException>(() => Checkpoint(
            prerequisite: new ScanStagePrerequisite(ScanStage.Exact, Succeeded)));
        Assert.Throws<ArgumentException>(() => Checkpoint(
            prerequisite: new ScanStagePrerequisite(
                ScanStage.Enumeration,
                new StageOutcome(ScanStageState.Running, ScanReason.None))));
        Assert.Throws<ArgumentException>(() => Checkpoint(
            outcome: new StageOutcome(ScanStageState.Running, ScanReason.None),
            prerequisite: new ScanStagePrerequisite(
                ScanStage.Enumeration,
                new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure))));
        Assert.Throws<ArgumentException>(() => Checkpoint(
            outcome: new StageOutcome(ScanStageState.NotApplicable, ScanReason.ReadFailure),
            prerequisite: new ScanStagePrerequisite(ScanStage.Enumeration, Succeeded)));
        Assert.Throws<ArgumentException>(() => Checkpoint(
            outcome: new StageOutcome(ScanStageState.NotApplicable, ScanReason.Changed),
            prerequisite: new ScanStagePrerequisite(
                ScanStage.Enumeration,
                new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure))));
    }

    [Fact]
    public void EnumerationCheckpointIsTerminalAndHasNoPrerequisite()
    {
        Assert.Throws<ArgumentException>(() => Checkpoint(
            stage: ScanStage.Enumeration,
            outcome: new StageOutcome(ScanStageState.Running, ScanReason.None),
            prerequisite: null));
        Assert.Throws<ArgumentException>(() => Checkpoint(
            stage: ScanStage.Enumeration,
            outcome: Succeeded,
            prerequisite: new ScanStagePrerequisite(ScanStage.Enumeration, Succeeded)));
    }

    [Fact]
    public void InterruptionMarkerBelongsOnlyToReadFailureProjection()
    {
        Assert.Throws<ArgumentException>(() => Checkpoint(recoveredFromInterruption: true));

        var recovered = Checkpoint(
            outcome: new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure),
            recoveredFromInterruption: true);

        Assert.True(recovered.RecoveredFromInterruption);
    }

    [Fact]
    public void AccountingRejectsNegativeAndContradictoryCounts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanAccounting(-1, 0, 0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanAccounting(0, -1, 0, 0, 0, 1));
        Assert.Throws<ArgumentException>(() => new ScanAccounting(2, 1, 0, 0, 0, 0));

        Assert.Equal(2, new ScanAccounting(2, 1, 0, 1, 0, 0).Discovered);
    }

    [Fact]
    public void TransitionCommandsRejectStaleShapesAndInitialOccurrenceCarriesIdentity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanLifecycleTransitionCommand(
            ScanId, 0, TransitionId, ScanLifecycleState.Enumerating));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanStageTransitionCommand(
            ScanId, 7, ScanStage.Exact, 0, TransitionId,
            new StageOutcome(ScanStageState.Succeeded, ScanReason.None)));

        var occurrence = new ScanOccurrenceIdentityRecord(
            new OccurrenceToken(Pin, 7, 1, 1), @"C:\photos\a.jpg", @"C:\photos\a.jpg", null);
        var enumeration = Checkpoint(stage: ScanStage.Enumeration, prerequisite: null);
        var exact = Checkpoint();
        var command = new InitialOccurrenceCheckpointCommand(occurrence, enumeration, exact);

        Assert.Same(occurrence, command.Occurrence);
        Assert.Equal(ScanStage.Enumeration, command.Enumeration.Stage);
        Assert.Equal(ScanStage.Exact, command.Exact.Stage);
    }

    [Fact]
    public void ResultsAreClosedAndPortContainsNoSqliteTypes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScanCheckpointWriteResult((ScanCheckpointWriteOutcome)999, null));
        Assert.Throws<ArgumentException>(() =>
            new ScanCheckpointWriteResult(ScanCheckpointWriteOutcome.Accepted, null));
        Assert.Throws<ArgumentException>(() => new ScanCheckpointReadResult(
            ScanCheckpointReadOutcome.Found,
            null,
            ImmutableArray<ScanCheckpoint>.Empty,
            null));

        var sqliteTypes = typeof(IScanCheckpointStore).GetMethods()
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType))
            .Where(type => type.FullName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) is true)
            .ToArray();

        Assert.Empty(sqliteTypes);
    }

    private static ScanCheckpoint Checkpoint(
        Guid? scanId = null,
        long occurrenceId = 7,
        ScanStage stage = ScanStage.Exact,
        long revision = 1,
        Guid? transitionId = null,
        StageOutcome? outcome = null,
        ScanStagePrerequisite? prerequisite = default,
        bool recoveredFromInterruption = false)
    {
        var actualOutcome = outcome ?? Succeeded;
        var actualPrerequisite = prerequisite ?? (stage == ScanStage.Exact
            ? new ScanStagePrerequisite(ScanStage.Enumeration, Succeeded)
            : null);
        return new ScanCheckpoint(
            scanId ?? ScanId,
            occurrenceId,
            stage,
            revision,
            transitionId ?? TransitionId,
            actualOutcome,
            actualPrerequisite,
            recoveredFromInterruption);
    }
}
