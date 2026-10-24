using System.Windows;
using Microsoft.Win32;
using Albumen.Application.Scope;

namespace Albumen.App.Shell;

public sealed class WpfLocalFolderPicker(Func<Window?> ownerAccessor)
    : ILocalFolderPicker
{
    public Task<string?> PickSingleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OpenFolderDialog dialog = new()
        {
            Multiselect = false,
            // The Windows folder picker lists folders only; the folder card names the photos.
            Title = "Choose the folder that holds your photos (files aren't listed here)",
            ValidateNames = true,
        };
        var owner = ownerAccessor();
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return Task.FromResult(accepted is true ? dialog.FolderName : null);
    }
}
