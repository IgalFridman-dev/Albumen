using Albumen.Domain.Scope;

namespace Albumen.Application.Shell;

// Reference identity, not value equality: retrying identical inputs is still a
// different operation. Revision is assigned once, when the owner accepts it.
internal sealed class ScopeOperation(
    ScopeRequest request, ScopeSelection selection, long revision)
{
    public ScopeRequest Request { get; } = request;

    public ScopeSelection Selection { get; } = selection;

    public long Revision { get; } = revision;
}
