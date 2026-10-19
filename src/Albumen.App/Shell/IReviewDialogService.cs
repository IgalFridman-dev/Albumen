using System.Windows;
using Albumen.Domain.Review;

namespace Albumen.App.Shell;

public enum SurvivorOverrideDialogResult
{
    Affirm,
    Cancel,
}

public interface IReviewDialogService
{
    Task<SurvivorOverrideDialogResult> ShowSurvivorOverrideAsync(
        OverrideProposalToken proposal,
        IInputElement invokingFocus,
        CancellationToken cancellationToken);
}
