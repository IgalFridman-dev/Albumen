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
            null));
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
        Assert.Null(typeof(ScanCheckpoint).GetProperty("RecoveredFromInterruption"));
        var stored = Checkpoint(outcome: new StageOutcome(ScanStageState.Running, ScanReason.None));

        Assert.Throws<ArgumentException>(() => new ScanCheckpointProjection(
            stored,
            Succeeded,
            true));
        var recovered = new ScanCheckpointProjection(
            stored,
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure),
            true);

        Assert.True(recovered.RecoveredFromInterruption);
        Assert.Equal(ScanStageState.Running, recovered.Stored.Outcome.State);
        Assert.Equal(ScanStageState.FailedRetryable, recovered.Outcome.State);
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
    public void AccountingRejectsCategorizedTotalOverflow()
    {
        Assert.Throws<ArgumentException>(() =>
            new ScanAccounting(long.MaxValue, long.MaxValue, 1, 0, 0, 0));
    }

    [Fact]
    public void SnapshotProjectionNamesUnsupportedExplicitlyAndPreservesSixCategories()
    {
        var rows = ImmutableArray.Create(
            Occurrence(1, Succeeded, Succeeded),
            Occurrence(2, Succeeded,
                new StageOutcome(ScanStageState.Unsupported, ScanReason.UnsupportedExtension)),
            Occurrence(3,
                new StageOutcome(ScanStageState.Unsupported, ScanReason.UnsupportedExtension),
                new StageOutcome(ScanStageState.NotApplicable, ScanReason.UnsupportedExtension)),
            Occurrence(4, Succeeded,
                new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure)),
            Occurrence(5, Succeeded,
                new StageOutcome(ScanStageState.Canceled, ScanReason.Canceled)),
            Occurrence(6, Succeeded,
                new StageOutcome(ScanStageState.Running, ScanReason.None)));

        var snapshot = ScanSnapshotProjector.Create(
            Pin,
            ScanStatus.Running,
            new StageOutcome(ScanStageState.Running, ScanReason.None),
            rows,
            ImmutableArray<CandidateGroup>.Empty);

        Assert.Equal(new ScanCounts(6, 1, 2, 1, 1, 1), snapshot.Counts);
        var properties = typeof(ScanCounts).GetProperties().Select(property => property.Name).ToArray();
        Assert.Equal(
            ["Discovered", "Analyzed", "Unsupported", "Failed", "Canceled", "Outstanding"],
            properties);
        Assert.DoesNotContain("Excluded", properties);
    }

    [Theory]
    [InlineData(ScanStageState.FailedRetryable, ScanReason.ReadFailure, 1, 0)]
    [InlineData(ScanStageState.FailedTerminal, ScanReason.UnknownSafety, 1, 0)]
    [InlineData(ScanStageState.Canceled, ScanReason.Canceled, 0, 1)]
    public void NotApplicableProjectionUsesItsEnumerationPrerequisiteCategory(
        ScanStageState enumerationState,
        ScanReason reason,
        long failed,
        long canceled)
    {
        var enumeration = new StageOutcome(enumerationState, reason);
        var rows = ImmutableArray.Create(Occurrence(
            1,
            enumeration,
            new StageOutcome(ScanStageState.NotApplicable, reason)));

        var snapshot = ScanSnapshotProjector.Create(
            Pin,
            ScanStatus.Running,
            new StageOutcome(ScanStageState.Running, ScanReason.None),
            rows,
            ImmutableArray<CandidateGroup>.Empty);

        Assert.Equal(new ScanCounts(1, 0, 0, failed, canceled, 0), snapshot.Counts);
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
    public void InitialOccurrenceRejectsContradictorySiblingPrerequisiteAndProjectionMetadata()
    {
        var occurrence = new ScanOccurrenceIdentityRecord(
            new OccurrenceToken(Pin, 7, 1, 1), @"C:\photos\a.jpg", @"C:\photos\a.jpg", null);
        var enumeration = Checkpoint(stage: ScanStage.Enumeration, prerequisite: null);
        var failed = new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure);
        var contradictoryExact = Checkpoint(
            outcome: new StageOutcome(ScanStageState.NotApplicable, ScanReason.ReadFailure),
            prerequisite: new ScanStagePrerequisite(ScanStage.Enumeration, failed));

        Assert.Throws<ArgumentException>(() =>
            new InitialOccurrenceCheckpointCommand(occurrence, enumeration, contradictoryExact));
        Assert.DoesNotContain(
            typeof(InitialOccurrenceCheckpointCommand).GetProperties(),
            property => property.PropertyType == typeof(ScanCheckpointProjection));
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
            ImmutableArray<ScanCheckpointProjection>.Empty,
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
        ScanStagePrerequisite? prerequisite = default)
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
            actualPrerequisite);
    }

    private static ScanFileOccurrence Occurrence(
        long occurrenceId,
        StageOutcome enumeration,
        StageOutcome exact)
    {
        var physical = exact.State == ScanStageState.Succeeded
            ? new PhysicalFileKey(1, new FileId128(0, (ulong)occurrenceId))
            : (PhysicalFileKey?)null;
        var content = physical is { } key
            ? new ContentRevision(key, 1, new string('0', 64))
            : null;
        return new ScanFileOccurrence(
            new OccurrenceToken(Pin, occurrenceId, 1, 1),
            $@"C:\photos\{occurrenceId}.jpg",
            $@"C:\photos\{occurrenceId}.jpg",
            physical,
            enumeration,
            exact,
            content,
            new AllocationEvidence(null, null, false));
    }
}
