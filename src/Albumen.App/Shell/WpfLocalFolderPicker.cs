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
            Title = "Choose one local photo folder",
            ValidateNames = true,
        };
        var owner = ownerAccessor();
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return Task.FromResult(accepted is true ? dialog.FolderName : null);
    }
}
