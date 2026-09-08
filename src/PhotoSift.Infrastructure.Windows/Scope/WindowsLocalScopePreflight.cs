using PhotoSift.Application.Scope;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Infrastructure.Windows.Scope;

public sealed class WindowsLocalScopePreflight : ILocalScopePreflight
{
    private readonly WindowsScopeTraversal traversal;

    public WindowsLocalScopePreflight()
        : this(new WindowsScopeNative(
            new WicCodecCapability(new ComClassAvailability())))
    {
    }

    internal WindowsLocalScopePreflight(IWindowsScopeNative native)
    {
        traversal = new WindowsScopeTraversal(
            native ?? throw new ArgumentNullException(nameof(native)));
    }

    public Task<ScopePreflightEvidence> InspectAsync(
        ScopeSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return Task.Run(
            () => Inspect(selection, cancellationToken),
            cancellationToken);
    }

    private ScopePreflightEvidence Inspect(
        ScopeSelection selection,
        CancellationToken cancellationToken)
    {
        ScopePreflightEvidence? finished = null;
        foreach (var item in traversal.Walk(selection, cancellationToken))
        {
            if (item is WindowsTraversalFinished terminal)
            {
                if (finished is not null)
                {
                    throw new InvalidOperationException(
                        "Windows scope traversal produced more than one terminal event.");
                }

                finished = terminal.Evidence;
            }
        }

        return finished ?? throw new InvalidOperationException(
            "Windows scope traversal ended without terminal evidence.");
    }
}
