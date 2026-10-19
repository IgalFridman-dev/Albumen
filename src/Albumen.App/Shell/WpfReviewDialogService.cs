using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Albumen.Domain.Review;

namespace Albumen.App.Shell;

public sealed class WpfReviewDialogService(Func<Window?> ownerProvider) : IReviewDialogService
{
    private readonly Func<Window?> ownerProvider = ownerProvider ?? throw new ArgumentNullException(nameof(ownerProvider));
    private int blockingLayer;

    public async Task<SurvivorOverrideDialogResult> ShowSurvivorOverrideAsync(
        OverrideProposalToken proposal,
        IInputElement invokingFocus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(invokingFocus);
        cancellationToken.ThrowIfCancellationRequested();

        var owner = ownerProvider();
        var dispatcher = owner?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        if (!dispatcher.CheckAccess())
        {
            return await dispatcher.InvokeAsync(
                () => ShowCore(proposal, invokingFocus, owner, cancellationToken),
                DispatcherPriority.Normal, cancellationToken);
        }

        return ShowCore(proposal, invokingFocus, owner, cancellationToken);
    }

    private SurvivorOverrideDialogResult ShowCore(
        OverrideProposalToken proposal,
        IInputElement invokingFocus,
        Window? owner,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref blockingLayer, 1, 0) != 0)
            return SurvivorOverrideDialogResult.Cancel;

        try
        {
            var dispatcher = owner?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            var dialog = new SurvivorOverrideDialog(proposal);
            if (owner is { IsVisible: true }) dialog.Owner = owner;
            var focusTransitionOwner = owner as MainWindow;
            var reviewFocusToken = focusTransitionOwner?.CaptureReviewFocusRestorationToken(invokingFocus);
            var focusIdentity = focusTransitionOwner is null ? CaptureFocusIdentity(invokingFocus) : null;
            var focusTransitionGeneration = focusTransitionOwner?.BeginReviewDialogFocusTransition() ?? 0;
            try
            {
                try
                {
                    using var registration = cancellationToken.Register(() => dialog.Dispatcher.BeginInvoke(
                        DispatcherPriority.Send, new Action(() =>
                        {
                            if (dialog.IsVisible) dialog.Close();
                        })));
                    dialog.ShowDialog();
                    return dialog.Result;
                }
                finally
                {
                    focusTransitionOwner?.EndReviewDialogFocusTransition();
                }
            }
            finally
            {
                if (!dispatcher.HasShutdownStarted)
                {
                    if (focusTransitionOwner is not null)
                    {
                        if (reviewFocusToken is not null)
                            focusTransitionOwner.QueueReviewFocusRestoration(
                                reviewFocusToken, focusTransitionGeneration);
                    }
                    else
                    {
                        RestoreFocus(owner, invokingFocus, focusIdentity);
                    }
                }
            }
        }
        finally
        {
            Volatile.Write(ref blockingLayer, 0);
        }
    }

    private static ReviewFocusIdentity? CaptureFocusIdentity(IInputElement invokingFocus)
    {
        if (invokingFocus is not FrameworkElement { DataContext: ScanOccurrenceViewModel member } element)
            return null;
        var group = FindDataContext<ExactGroupViewModel>(element);
        return group is null ? null : new(group.GroupId, member.OccurrenceId);
    }

    private static void RestoreFocus(
        Window? owner,
        IInputElement invokingFocus,
        ReviewFocusIdentity? identity)
    {
        if (owner is not { IsVisible: true } || owner.Dispatcher.HasShutdownStarted) return;
        owner.Activate();
        owner.UpdateLayout();
        var target = CanReceiveFocus(invokingFocus)
            ? invokingFocus
            : identity is { } key ? FindCurrentSelectionControl(owner, key) : null;
        if (target is null || !target.Focus()) return;
        Keyboard.Focus(target);
    }

    private static bool CanReceiveFocus(IInputElement element) =>
        element is UIElement { IsVisible: true, IsEnabled: true, Focusable: true } visual
        && PresentationSource.FromVisual(visual) is not null;

    private static ReviewSelectionCheckBox? FindCurrentSelectionControl(
        DependencyObject root,
        ReviewFocusIdentity identity)
    {
        foreach (var control in Descendants<ReviewSelectionCheckBox>(root))
        {
            if (control.DataContext is not ScanOccurrenceViewModel member
                || member.OccurrenceId != identity.OccurrenceId)
                continue;
            var group = FindDataContext<ExactGroupViewModel>(control);
            if (group?.GroupId == identity.GroupId && CanReceiveFocus(control)) return control;
        }
        return null;
    }

    private static T? FindDataContext<T>(DependencyObject? current) where T : class
    {
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: T match }) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private sealed record ReviewFocusIdentity(Guid GroupId, long OccurrenceId);
}
