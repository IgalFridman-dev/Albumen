using System.Collections.Immutable;
using PhotoSift.Application.Scan;

namespace PhotoSift.Infrastructure.Windows.Scan;

internal sealed class WindowsProtectedFileLease(
    FileStream content,
    ProtectedFileFacts openedFacts,
    ProtectedOpenRequest request,
    WindowsFileFactsReader facts,
    ImmutableDictionary<string, WindowsDirectoryProof> openedChain) : IProtectedFileLease
{
    private bool disposed;
    private bool recoveryStable = openedFacts.Allocation.Stable;

    public Stream Content => content;

    public ProtectedFileFacts OpenedFacts { get; } = openedFacts;

    public ValueTask<ProtectedFileFacts> RefreshAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        using var chain = facts.ValidateScope(request, openedChain, cancellationToken);
        var refreshed = facts.ReadContent(content.SafeFileHandle, request, OpenedFacts);
        // Once recovery metadata is inconsistent, a later return to the old values cannot
        // erase that observation. Content identity and length have separate validation.
        recoveryStable &= refreshed.Allocation.Stable;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(refreshed with
        {
            Allocation = refreshed.Allocation with { Stable = recoveryStable },
        });
    }

    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        return content.DisposeAsync();
    }
}
