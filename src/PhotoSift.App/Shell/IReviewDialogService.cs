using System.Windows;
using PhotoSift.Domain.Review;

namespace PhotoSift.App.Shell;

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
