using System.Collections.Immutable;
using Albumen.Domain.Action;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Domain.Tests.Action;

public sealed class ActionRecordPolicyTests
{
    [Fact]
    public void AggregatesOutcomesFromRowsRatherThanDistinctPhysicalFiles()
    {
        var key = Key(1);
        var rows = ImmutableArray.Create(
            new ActionRecordEvidence(key, 4096, ActionRecordEvidenceState.Recycled),
            new ActionRecordEvidence(key, 4096, ActionRecordEvidenceState.NotRemoved));

        var summary = ActionRecordPolicy.Derive(rows);

        Assert.Equal(ActionRecordCertainty.Resolved, summary.Certainty);
        Assert.Equal(ActionRecordOutcomeMix.Partial, summary.OutcomeMix);
        Assert.Equal(4096, summary.RecoveredBytes);
    }

    [Theory]
    [InlineData(ActionRecordEvidenceState.Requested)]
    [InlineData(ActionRecordEvidenceState.Executing)]
    [InlineData(ActionRecordEvidenceState.Unknown)]
    public void UnresolvedEvidenceNeverBecomesMoved(ActionRecordEvidenceState state)
    {
        var summary = ActionRecordPolicy.Derive(
            [new ActionRecordEvidence(Key(1), 4096, state)]);

        Assert.Equal(ActionRecordCertainty.HasUnknown, summary.Certainty);
        Assert.Equal(ActionRecordOutcomeMix.NoneMoved, summary.OutcomeMix);
        Assert.Equal(0, summary.RecoveredBytes);
    }

    [Fact]
    public void RecycledAndUnresolvedRowsProduceAnUnknownPartialOutcome()
    {
        var rows = ImmutableArray.Create(
            new ActionRecordEvidence(Key(1), 4096, ActionRecordEvidenceState.Recycled),
            new ActionRecordEvidence(Key(2), 8192, ActionRecordEvidenceState.Executing));

        var summary = ActionRecordPolicy.Derive(rows);

        Assert.Equal(ActionRecordCertainty.HasUnknown, summary.Certainty);
        Assert.Equal(ActionRecordOutcomeMix.Partial, summary.OutcomeMix);
        Assert.Equal(4096, summary.RecoveredBytes);
    }

    [Theory]
    [InlineData(ActionRecordEvidenceState.NotRemoved)]
    [InlineData(ActionRecordEvidenceState.ConfirmedPresent)]
    [InlineData(ActionRecordEvidenceState.Unknown)]
    public void NonRecycledRowsContributeNoRecoveredBytes(ActionRecordEvidenceState state)
    {
        var summary = ActionRecordPolicy.Derive(
            [new ActionRecordEvidence(Key(1), 4096, state)]);

        Assert.Equal(0, summary.RecoveredBytes);
        Assert.Equal(ActionRecordOutcomeMix.NoneMoved, summary.OutcomeMix);
    }

    [Fact]
    public void RecycledHardLinksContributeOnePhysicalAllocation()
    {
        var key = Key(1);
        var rows = ImmutableArray.Create(
            new ActionRecordEvidence(key, 4096, ActionRecordEvidenceState.Recycled),
            new ActionRecordEvidence(key, 4096, ActionRecordEvidenceState.Recycled),
            new ActionRecordEvidence(Key(2), 8192, ActionRecordEvidenceState.Recycled));

        var summary = ActionRecordPolicy.Derive(rows);

        Assert.Equal(ActionRecordOutcomeMix.AllMoved, summary.OutcomeMix);
        Assert.Equal(12288, summary.RecoveredBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void MissingOrNonPositiveAllocationContributesZero(long? allocatedBytes)
    {
        var summary = ActionRecordPolicy.Derive(
            [new ActionRecordEvidence(Key(1), allocatedBytes, ActionRecordEvidenceState.Recycled)]);

        Assert.Equal(ActionRecordOutcomeMix.AllMoved, summary.OutcomeMix);
        Assert.Equal(0, summary.RecoveredBytes);
    }

    [Fact]
    public void InconsistentAllocationForOnePhysicalFileContributesZero()
    {
        var key = Key(1);
        var rows = ImmutableArray.Create(
            new ActionRecordEvidence(key, 4096, ActionRecordEvidenceState.Recycled),
            new ActionRecordEvidence(key, 8192, ActionRecordEvidenceState.Recycled),
            new ActionRecordEvidence(Key(2), 16384, ActionRecordEvidenceState.Recycled));

        var summary = ActionRecordPolicy.Derive(rows);

        Assert.Equal(16384, summary.RecoveredBytes);
    }

    [Fact]
    public void EmptyEvidenceIsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            ActionRecordPolicy.Derive(ImmutableArray<ActionRecordEvidence>.Empty));
    }

    private static PhysicalFileKey Key(ulong low) => new(7, new FileId128(0, low));
}
