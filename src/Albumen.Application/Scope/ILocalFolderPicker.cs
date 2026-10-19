namespace Albumen.Application.Scope;

public interface ILocalFolderPicker
{
    Task<string?> PickSingleAsync(CancellationToken cancellationToken);
}
