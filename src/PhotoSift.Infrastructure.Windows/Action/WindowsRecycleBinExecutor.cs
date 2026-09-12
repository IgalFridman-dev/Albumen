using PhotoSift.Application.Action;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Action;

namespace PhotoSift.Infrastructure.Windows.Action;

public sealed class WindowsRecycleBinExecutor : IRecycleBinExecutor
{
    public ValueTask<RecycleBinResult> ExecuteAsync(RecycleBinRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The Action owner supplies the exact approved item after its newly
        // acknowledged Executing transaction. No replacement path is resolved.
        return new(StaFileOperationRunner.RunAsync(request.Safety.Item.DiscoveredPath,
            () => new NativeFileOperationDriver(),
            () => new ActionRecycleLease(request.Safety, cancellationToken), cancellationToken));
    }

    private sealed class ActionRecycleLease : IRecycleBinLease
    {
        private readonly ActionSafetyEnvelope safety;
        private readonly WindowsActionLease lease;
        internal ActionRecycleLease(ActionSafetyEnvelope safety, CancellationToken cancellationToken)
        {
            this.safety = safety;
            // Hashing may use asynchronous I/O during preparation; no COM object
            // is created until this returns to the sole STA owner.
            lease = WindowsActionLease.OpenAsync(safety, cancellationToken).GetAwaiter().GetResult();
        }
        public bool NamespaceHandoffOccurred => lease.NamespaceHandoffOccurred;
        public ActionSafetyReason PrepareForPerformOperations() => lease.PrepareForPerformOperations();
        public ActionSafetyReason ValidateAndReleaseForPreDelete() => lease.ValidateAndReleaseForPreDelete();
        public bool ProveOriginalUnchanged()
        {
            try
            {
                using var proof = WindowsActionLease.OpenAsync(safety, CancellationToken.None).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception) { return false; }
        }
        public void Dispose() => lease.Dispose();
    }
}
