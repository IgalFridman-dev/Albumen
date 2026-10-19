using Albumen.Domain.Scope;

namespace Albumen.Application.Scope;

public interface ILocalScopePreflight
{
    Task<ScopePreflightEvidence> InspectAsync(
        ScopeSelection selection,
        CancellationToken cancellationToken);
}
