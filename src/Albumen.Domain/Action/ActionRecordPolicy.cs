using System.Collections.Immutable;
using Albumen.Domain.Scan;

namespace Albumen.Domain.Action;

public enum ActionRecordEvidenceState
{
    Requested,
    Executing,
    Recycled,
    NotRemoved,
    Unknown,
    ConfirmedPresent,
}

public enum ActionRecordCertainty
{
    Resolved,
    HasUnknown,
}

public enum ActionRecordOutcomeMix
{
    AllMoved,
    NoneMoved,
    Partial,
}

public sealed record ActionRecordEvidence(
    PhysicalFileKey Key,
    long? AllocatedBytes,
    ActionRecordEvidenceState State);

public sealed record ActionRecordSummary(
    ActionRecordCertainty Certainty,
    ActionRecordOutcomeMix OutcomeMix,
    long RecoveredBytes);

public static class ActionRecordPolicy
{
    public static ActionRecordSummary Derive(ImmutableArray<ActionRecordEvidence> rows)
    {
        if (rows.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Action record evidence must not be empty.", nameof(rows));
        }

        var allocations = new Dictionary<PhysicalFileKey, long?>();
        var inconsistentKeys = new HashSet<PhysicalFileKey>();
        foreach (var row in rows)
        {
            if (allocations.TryGetValue(row.Key, out var allocatedBytes))
            {
                if (allocatedBytes != row.AllocatedBytes)
                {
                    inconsistentKeys.Add(row.Key);
                }
            }
            else
            {
                allocations.Add(row.Key, row.AllocatedBytes);
            }
        }

        var hasUnknown = false;
        var movedCount = 0;
        var recoveredBytes = 0L;
        var recoveredKeys = new HashSet<PhysicalFileKey>();

        foreach (var row in rows)
        {
            var (moved, unknown) = row.State switch
            {
                ActionRecordEvidenceState.Requested => (false, true),
                ActionRecordEvidenceState.Executing => (false, true),
                ActionRecordEvidenceState.Recycled => (true, false),
                ActionRecordEvidenceState.NotRemoved => (false, false),
                ActionRecordEvidenceState.Unknown => (false, true),
                ActionRecordEvidenceState.ConfirmedPresent => (false, false),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(rows),
                    row.State,
                    "Action record evidence state is invalid."),
            };

            hasUnknown |= unknown;
            if (!moved)
            {
                continue;
            }

            movedCount++;
            if (row.AllocatedBytes is > 0
                && !inconsistentKeys.Contains(row.Key)
                && recoveredKeys.Add(row.Key))
            {
                recoveredBytes = checked(recoveredBytes + row.AllocatedBytes.Value);
            }
        }

        var outcomeMix = movedCount switch
        {
            0 => ActionRecordOutcomeMix.NoneMoved,
            _ when movedCount == rows.Length => ActionRecordOutcomeMix.AllMoved,
            _ => ActionRecordOutcomeMix.Partial,
        };

        return new ActionRecordSummary(
            hasUnknown ? ActionRecordCertainty.HasUnknown : ActionRecordCertainty.Resolved,
            outcomeMix,
            recoveredBytes);
    }
}
