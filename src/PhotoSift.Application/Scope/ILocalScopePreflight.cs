using PhotoSift.Domain.Scope;

namespace PhotoSift.Application.Scope;

public interface ILocalScopePreflight
{
    Task<ScopePreflightEvidence> InspectAsync(
        ScopeSelection selection,
        CancellationToken cancellationToken);
}
