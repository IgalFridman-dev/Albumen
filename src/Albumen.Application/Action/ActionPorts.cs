using Albumen.Application.Review;
using Albumen.Domain.Scan;

namespace Albumen.Application.Action;

public interface IActionCoordinator : IAsyncDisposable
{
    ActionSnapshot Current { get; }
    event EventHandler<ActionSnapshot>? SnapshotChanged;
    Task<ActionCommandResult> ApproveAsync(ApprovedActionRequest request, CancellationToken cancellationToken);
    Task ReportObserverFaultAsync(Guid requestId);
}
public interface IActionJournalStore
{
    ValueTask<ActionJournalWriteResult> PersistRequestedAsync(ActionRequestedFact fact, CancellationToken cancellationToken);
    ValueTask<ActionJournalWriteResult> PersistExecutingAsync(ActionExecutingFact fact, CancellationToken cancellationToken);
    ValueTask<ActionJournalWriteResult> PersistTerminalAsync(ActionTerminalFact fact, CancellationToken cancellationToken);
}
public interface IActionRecordStore
{
    ValueTask<ActionRecordLoadResult> LoadAsync(CancellationToken cancellationToken);
    ValueTask<ActionRevalidationWriteResult> AppendRevalidationAsync(ActionRevalidationFact fact, CancellationToken cancellationToken);
}
public sealed record ActionRevalidationRequest(Guid ActionItemId, string ResolvedPath,
    PhysicalFileKey ReviewedKey, long ReviewedLength, string ReviewedSha256);
public sealed record ActionRevalidationProbeResult(ActionRevalidationKind Kind,
    ActionRevalidationReason Reason, ActionRevalidationObservation Observation);
public interface IActionRevalidationProbe
{
    ValueTask<ActionRevalidationProbeResult> ProbeAsync(ActionRevalidationRequest request,
        CancellationToken cancellationToken);
}
public interface IRecycleBinExecutor
{
    // Cancellation can deny permission. Once permission is granted the adapter
    // must retain callbacks, classify evidence and clean up before settling;
    // it must not cancel this acknowledgment or perform another attempt.
    ValueTask<RecycleBinResult> ExecuteAsync(RecycleBinRequest request, CancellationToken cancellationToken);
}
