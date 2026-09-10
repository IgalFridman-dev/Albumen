using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoSift.App.Shell;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.App;

public partial class MainWindow : Window
{
    private ShellViewModel? shellViewModel;
    private int dataContextGeneration;
    private bool isClosed;
    private int selectionSummaryAnnouncementGeneration;
    private long reviewFailureAnnouncementSequence;
    private ReviewFocusIdentity? reviewFocusIdentity;
    private long reviewFocusGeneration;
    private bool isRestoringReviewFocus;
    private int reviewDialogFocusTransitionDepth;
    private long reviewDialogFocusTransitionGeneration;
    private bool closeAfterLifetime;
    private Task? closeCompletionTask;
    private Task? exitLifetimeObservationTask;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
        AddHandler(Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnReviewKeyboardFocusChanged), true);
        if (System.Windows.Application.Current is { } application)
            application.Exit += OnApplicationExit;
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        dataContextGeneration++;
        reviewFocusIdentity = null;
        reviewFocusGeneration++;
        if (shellViewModel is not null)
        {
            shellViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        shellViewModel = eventArgs.NewValue as ShellViewModel;
        if (shellViewModel is not null)
        {
            shellViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (shellViewModel?.IsBlocked is true)
        {
            FocusBlockingNotice();
        }
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if ((eventArgs.PropertyName == nameof(ShellViewModel.CanConfirmExactRules)
                && shellViewModel is { IsConfirmRulesPhase: true, CanConfirmExactRules: false, CanStartScan: true })
            || (eventArgs.PropertyName == nameof(ShellViewModel.IsScanBusy)
                && shellViewModel is { IsScanPhase: true, IsScanBusy: true }))
        {
            QueuePhaseFocus();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.CurrentPhase))
        {
            QueuePhaseFocus();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.ScanStatus))
        {
            QueueScanStatusAnnouncement();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.ScopeStatus))
        {
            QueueScopeStatusAnnouncement();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.SelectionSummary))
        {
            QueueSelectionSummaryAnnouncement();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.ReviewAnnouncement))
        {
            QueueReviewFailureAnnouncement();
            QueueReviewFocusRestoration();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.ExactGroups))
        {
            QueueReviewFocusRestoration();
        }

        if (eventArgs.PropertyName == nameof(ShellViewModel.IsBlocked) &&
            shellViewModel?.IsBlocked is true)
        {
            FocusBlockingNotice();
        }
    }

    private void OnReviewKeyboardFocusChanged(object sender, KeyboardFocusChangedEventArgs eventArgs)
    {
        if (isRestoringReviewFocus
            || reviewDialogFocusTransitionDepth != 0
            || shellViewModel is not { IsReviewPhase: true, ReviewSession: { } session }
            || eventArgs.NewFocus is not FrameworkElement element
            || !ReferenceEquals(Keyboard.FocusedElement, element)
            || !CanReceiveFocus(element))
            return;
        if (element.DataContext is ScanOccurrenceViewModel member
            && FindDataContext<ExactGroupViewModel>(element) is { } group)
        {
            reviewFocusGeneration++;
            reviewFocusIdentity = new(session, group.GroupId, member.OccurrenceId);
            return;
        }
        if (reviewFocusIdentity?.Session != session || !IsDeliberateReviewFocusTarget(element)) return;
        reviewFocusGeneration++;
        reviewFocusIdentity = null;
    }

    private bool IsDeliberateReviewFocusTarget(FrameworkElement element) =>
        ReferenceEquals(element, ReviewHeading)
        || ReferenceEquals(element, ApprovalPanel)
        || element is ButtonBase;

    internal long BeginReviewDialogFocusTransition()
    {
        reviewDialogFocusTransitionDepth++;
        return ++reviewDialogFocusTransitionGeneration;
    }

    internal void EndReviewDialogFocusTransition() => reviewDialogFocusTransitionDepth--;

    private void OnCanSelectFocusedReviewGroup(object sender, CanExecuteRoutedEventArgs eventArgs)
    {
        eventArgs.CanExecute = FindDataContext<ExactGroupViewModel>(eventArgs.OriginalSource as DependencyObject) is not null;
        eventArgs.Handled = eventArgs.CanExecute;
    }

    private void OnSelectFocusedReviewGroup(object sender, ExecutedRoutedEventArgs eventArgs)
    {
        var group = FindDataContext<ExactGroupViewModel>(eventArgs.OriginalSource as DependencyObject);
        if (group is null) return;
        group.SelectAllCommand.Execute(null);
        eventArgs.Handled = true;
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

    private void FocusBlockingNotice() =>
        Dispatcher.InvokeAsync(
            () =>
            {
                Activate();
                BlockingNotice.Focus();
                Keyboard.Focus(BlockingNotice);
            },
            DispatcherPriority.Input);

    private void QueueScopeStatusAnnouncement()
    {
        var expectedViewModel = shellViewModel;
        var expectedStatus = expectedViewModel?.ScopeStatus;
        if (expectedStatus is not "Checking location and access" and
            not "Ready" and
            not "Blocked")
        {
            return;
        }

        var expectedGeneration = dataContextGeneration;
        Dispatcher.InvokeAsync(
            () =>
            {
                if (isClosed ||
                    expectedGeneration != dataContextGeneration ||
                    !ReferenceEquals(expectedViewModel, shellViewModel) ||
                    ScopeStatusText.Text != expectedStatus ||
                    AutomationProperties.GetItemStatus(ScopeStatusText) !=
                        expectedStatus)
                {
                    return;
                }

                var peer = UIElementAutomationPeer.FromElement(ScopeStatusText) ??
                    UIElementAutomationPeer.CreatePeerForElement(ScopeStatusText);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            },
            DispatcherPriority.Background);
    }

    private void QueuePhaseFocus()
    {
        var generation = dataContextGeneration;
        var expected = shellViewModel;
        if (expected is null) return;
        var phase = expected.CurrentPhase;
        Dispatcher.InvokeAsync(() =>
        {
            if (isClosed || generation != dataContextGeneration || !ReferenceEquals(expected, shellViewModel)
                || phase != shellViewModel?.CurrentPhase) return;
            UIElement? target = phase switch
            {
                WorkflowPhase.ChooseScope => expected.IsBlocked ? BlockingNotice : ChooseAnotherScopeButton,
                WorkflowPhase.ConfirmRules => expected.CanConfirmExactRules ? ConfirmExactRulesButton : StartScanButton,
                WorkflowPhase.Scan => expected.IsScanBusy ? CancelScanButton : null,
                WorkflowPhase.Review => ReviewHeading,
                _ => null,
            };
            if (target is not null && target.IsVisible && target.IsEnabled)
            {
                Activate();
                target.Focus();
                Keyboard.Focus(target);
            }
        }, DispatcherPriority.Input);
    }

    private void QueueScanStatusAnnouncement()
    {
        var expected = shellViewModel;
        var status = expected?.ScanStatus;
        var generation = dataContextGeneration;
        Dispatcher.InvokeAsync(() =>
        {
            var statusElement = expected?.IsReviewPhase is true ? ReviewStatusText : ScanStatusText;
            if (isClosed || generation != dataContextGeneration || !ReferenceEquals(expected, shellViewModel)
                || !statusElement.IsVisible || statusElement.Text != status || AutomationProperties.GetItemStatus(statusElement) != status) return;
            var peer = UIElementAutomationPeer.FromElement(statusElement)
                ?? UIElementAutomationPeer.CreatePeerForElement(statusElement);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }, DispatcherPriority.Background);
    }

    private void QueueSelectionSummaryAnnouncement()
    {
        var generation = dataContextGeneration;
        var expected = shellViewModel;
        var announcement = Interlocked.Increment(ref selectionSummaryAnnouncementGeneration);
        Dispatcher.InvokeAsync(() =>
        {
            if (isClosed || generation != dataContextGeneration
                || announcement != selectionSummaryAnnouncementGeneration
                || !ReferenceEquals(expected, shellViewModel) || expected?.IsReviewPhase is not true) return;
            if (!SelectionSummaryText.IsVisible) return;
            var peer = UIElementAutomationPeer.FromElement(SelectionSummaryText)
                ?? UIElementAutomationPeer.CreatePeerForElement(SelectionSummaryText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }, DispatcherPriority.Background);
    }

    private void QueueReviewFailureAnnouncement()
    {
        var generation = dataContextGeneration;
        var expected = shellViewModel;
        var payload = expected?.ReviewAnnouncement;
        if (string.IsNullOrEmpty(payload)) return;
        var sequence = ++reviewFailureAnnouncementSequence;
        ReviewAnnouncementText.CaptureDiagnostic(
            ReviewAnnouncementDiagnosticStage.Queue, sequence, payload, generation);
        Dispatcher.InvokeAsync(() =>
        {
            string? skipReason = null;
            if (isClosed) skipReason = "closed";
            else if (generation != dataContextGeneration) skipReason = "data-context-generation";
            else if (!ReferenceEquals(expected, shellViewModel)) skipReason = "data-context-identity";
            else if (expected?.IsReviewPhase is not true) skipReason = "not-review-phase";
            else if (!ReviewAnnouncementText.IsVisible) skipReason = "not-visible";
            if (skipReason is not null)
            {
                ReviewAnnouncementText.CaptureDiagnostic(
                    ReviewAnnouncementDiagnosticStage.Skip, sequence, payload, generation,
                    skipReason: skipReason);
                return;
            }
            ReviewAnnouncementText.CaptureDiagnostic(
                ReviewAnnouncementDiagnosticStage.Execute, sequence, payload, generation);
            ReviewAnnouncementText.Announce(sequence, payload);
        }, DispatcherPriority.Background);
    }

    private void QueueReviewFocusRestoration()
    {
        var token = CaptureReviewFocusRestorationToken(reviewFocusIdentity);
        if (token is not null)
            QueueReviewFocusRestoration(token, reviewDialogFocusTransitionGeneration);
    }

    internal ReviewFocusRestorationToken? CaptureReviewFocusRestorationToken(IInputElement invokingFocus)
    {
        if (invokingFocus is not FrameworkElement { DataContext: ScanOccurrenceViewModel member } element
            || shellViewModel?.ReviewSession is not { } session
            || FindDataContext<ExactGroupViewModel>(element) is not { } group)
            return null;
        var identity = new ReviewFocusIdentity(session, group.GroupId, member.OccurrenceId);
        return identity == reviewFocusIdentity ? CaptureReviewFocusRestorationToken(identity) : null;
    }

    private ReviewFocusRestorationToken? CaptureReviewFocusRestorationToken(ReviewFocusIdentity? identity)
    {
        var expected = shellViewModel;
        if (identity is null || expected?.IsReviewPhase is not true
            || expected.ReviewSession != identity.Session)
            return null;
        return new(expected, dataContextGeneration, identity.Session, identity.GroupId,
            identity.OccurrenceId, reviewFocusGeneration);
    }

    internal void QueueReviewFocusRestoration(
        ReviewFocusRestorationToken token,
        long expectedDialogFocusTransitionGeneration)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsReviewFocusRestorationRelevant(token, expectedDialogFocusTransitionGeneration)) return;
            UpdateLayout();
            if (!IsReviewFocusRestorationRelevant(token, expectedDialogFocusTransitionGeneration)) return;
            var identity = new ReviewFocusIdentity(token.Session, token.GroupId, token.OccurrenceId);
            var target = FindCurrentSelectionControl(this, identity);
            if (target is null
                || !IsReviewFocusRestorationRelevant(token, expectedDialogFocusTransitionGeneration))
                return;
            isRestoringReviewFocus = true;
            try
            {
                Activate();
                if (IsReviewFocusRestorationRelevant(token, expectedDialogFocusTransitionGeneration)
                    && CanReceiveFocus(target) && target.Focus())
                    Keyboard.Focus(target);
            }
            finally
            {
                isRestoringReviewFocus = false;
            }
        }, DispatcherPriority.Input);
    }

    private bool IsReviewFocusRestorationRelevant(
        ReviewFocusRestorationToken token,
        long expectedDialogFocusTransitionGeneration) =>
        !isClosed
        && token.DataContextGeneration == dataContextGeneration
        && token.FocusGeneration == reviewFocusGeneration
        && reviewDialogFocusTransitionDepth == 0
        && expectedDialogFocusTransitionGeneration == reviewDialogFocusTransitionGeneration
        && IsVisible
        && !Dispatcher.HasShutdownStarted
        && ReferenceEquals(token.ExpectedViewModel, shellViewModel)
        && token.ExpectedViewModel.IsReviewPhase
        && token.ExpectedViewModel.ReviewSession == token.Session
        && reviewFocusIdentity == new ReviewFocusIdentity(token.Session, token.GroupId, token.OccurrenceId);

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

    private static bool CanReceiveFocus(UIElement element) =>
        element.IsVisible && element.IsEnabled && element.Focusable
        && PresentationSource.FromVisual(element) is not null;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        CancelBlockingReviewLayer();
        if (closeAfterLifetime || shellViewModel is null) return;
        var disposal = shellViewModel.DisposeAsync();
        if (disposal.IsCompletedSuccessfully)
        {
            closeAfterLifetime = true;
            return;
        }

        eventArgs.Cancel = true;
        closeCompletionTask ??= CompleteLifetimeAndCloseAsync(disposal.AsTask());
    }

    public void OnApplicationExit(object? sender, EventArgs eventArgs)
    {
        CancelBlockingReviewLayer();
        if (shellViewModel is null) return;
        var disposal = shellViewModel.DisposeAsync();
        if (!disposal.IsCompletedSuccessfully)
            exitLifetimeObservationTask ??= ObserveLifetimeAsync(disposal.AsTask());
    }

    private async Task CompleteLifetimeAndCloseAsync(Task disposal)
    {
        try
        {
            await disposal;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine(exception);
        }
        finally
        {
            if (!isClosed && !Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (isClosed) return;
                    closeAfterLifetime = true;
                    Close();
                });
            }
        }
    }

    private static async Task ObserveLifetimeAsync(Task disposal)
    {
        try
        {
            await disposal.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine(exception);
        }
    }

    private void CancelBlockingReviewLayer()
    {
        foreach (var dialog in OwnedWindows.OfType<SurvivorOverrideDialog>().ToArray())
            dialog.Close();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        isClosed = true;
        dataContextGeneration++;
        if (shellViewModel is not null)
        {
            shellViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        if (System.Windows.Application.Current is { } application)
            application.Exit -= OnApplicationExit;
        reviewFocusIdentity = null;
        reviewFocusGeneration++;
    }

    internal sealed record ReviewFocusRestorationToken(
        ShellViewModel ExpectedViewModel,
        int DataContextGeneration,
        ReviewSessionPin Session,
        Guid GroupId,
        long OccurrenceId,
        long FocusGeneration);

    private sealed record ReviewFocusIdentity(ReviewSessionPin Session, Guid GroupId, long OccurrenceId);
}

public class ReviewAutomationBorder : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ReviewAutomationBorderPeer(this);
}

internal sealed class ReviewAutomationBorderPeer(ReviewAutomationBorder owner)
    : FrameworkElementAutomationPeer(owner)
{
    protected override string GetClassNameCore() => nameof(ReviewAutomationBorder);
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
}

public class ReviewAnnouncementTextBlock : TextBlock
{
    private string itemStatus = string.Empty;
    private ReviewAnnouncementDiagnosticBuffer? diagnosticBuffer;
    private AutomationPeer? expectedDiagnosticPeer;

    internal string ItemStatus => itemStatus;

    internal void EnableDiagnosticCapture()
    {
        diagnosticBuffer = new ReviewAnnouncementDiagnosticBuffer(Environment.CurrentManagedThreadId);
        expectedDiagnosticPeer = null;
    }

    internal ReviewAnnouncementDiagnosticSnapshot StopDiagnosticCapture()
    {
        var buffer = diagnosticBuffer;
        diagnosticBuffer = null;
        expectedDiagnosticPeer = null;
        return buffer?.Snapshot() ?? new([], false, false, 0);
    }

    internal void CaptureDiagnostic(
        ReviewAnnouncementDiagnosticStage stage,
        long sequence,
        string? payload,
        int dataContextGeneration = 0,
        string? previous = null,
        string? current = null,
        string? skipReason = null,
        AutomationPeer? peer = null)
    {
        diagnosticBuffer?.Append(new(
            stage,
            sequence,
            System.Diagnostics.Stopwatch.GetTimestamp(),
            Environment.CurrentManagedThreadId,
            payload,
            dataContextGeneration,
            previous,
            current,
            skipReason,
            peer?.GetType(),
            peer is null ? 0 : RuntimeHelpers.GetHashCode(peer),
            expectedDiagnosticPeer?.GetType(),
            expectedDiagnosticPeer is null ? 0 : RuntimeHelpers.GetHashCode(expectedDiagnosticPeer)));
    }

    internal void Announce(long sequence, string payload)
    {
        var previous = itemStatus;
        itemStatus = payload;
        CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.OwnerStatusSet,
            sequence, payload, previous: previous, current: itemStatus);
        var peer = UIElementAutomationPeer.FromElement(this)
            ?? UIElementAutomationPeer.CreatePeerForElement(this);
        CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.PeerResolved,
            sequence, payload, previous: previous, current: itemStatus, peer: peer);
        if (peer is ReviewAnnouncementTextBlockAutomationPeer announcementPeer)
            announcementPeer.RaiseAnnouncement(sequence, previous, payload);
    }

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        var peer = new ReviewAnnouncementTextBlockAutomationPeer(this);
        if (diagnosticBuffer is not null) expectedDiagnosticPeer = peer;
        _ = peer.GetItemStatus();
        return peer;
    }
}

internal sealed class ReviewAnnouncementTextBlockAutomationPeer(ReviewAnnouncementTextBlock owner)
    : TextBlockAutomationPeer(owner)
{
    protected override string GetItemStatusCore()
    {
        var current = owner.ItemStatus;
        owner.CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.GetItemStatusCore,
            sequence: 0, payload: current, current: current, peer: this);
        return current;
    }

    internal void RaiseAnnouncement(long sequence, string previous, string current)
    {
        owner.CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.PropertyRaiseBegin,
            sequence, current, previous: previous, current: current, peer: this);
        _ = GetItemStatus();
        RaisePropertyChangedEvent(AutomationElementIdentifiers.ItemStatusProperty, previous, current);
        owner.CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.PropertyRaiseReturn,
            sequence, current, previous: previous, current: current, peer: this);
        owner.CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.LiveRaiseBegin,
            sequence, current, previous: previous, current: current, peer: this);
        RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        owner.CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.LiveRaiseReturn,
            sequence, current, previous: previous, current: current, peer: this);
    }
}

internal enum ReviewAnnouncementDiagnosticStage
{
    Queue,
    Execute,
    Skip,
    OwnerStatusSet,
    PeerResolved,
    PropertyRaiseBegin,
    PropertyRaiseReturn,
    LiveRaiseBegin,
    LiveRaiseReturn,
    GetItemStatusCore,
}

internal readonly record struct ReviewAnnouncementDiagnosticRecord(
    ReviewAnnouncementDiagnosticStage Stage,
    long NotificationSequence,
    long Timestamp,
    int ThreadId,
    string? Payload,
    int DataContextGeneration,
    string? Previous,
    string? Current,
    string? SkipReason,
    Type? PeerType,
    int PeerIdentity,
    Type? ExpectedPeerType,
    int ExpectedPeerIdentity);

internal sealed record ReviewAnnouncementDiagnosticSnapshot(
    ReviewAnnouncementDiagnosticRecord[] Records,
    bool CapacityOverflow,
    bool WrongThread,
    int OwnerThreadId);

internal sealed class ReviewAnnouncementDiagnosticBuffer(int ownerThreadId)
{
    private const int Capacity = 256;
    private readonly ReviewAnnouncementDiagnosticRecord[] records = new ReviewAnnouncementDiagnosticRecord[Capacity];
    private int writeIndex;
    private bool capacityOverflow;
    private bool wrongThread;

    internal void Append(ReviewAnnouncementDiagnosticRecord record)
    {
        if (record.ThreadId != ownerThreadId)
        {
            wrongThread = true;
            return;
        }
        if (writeIndex >= records.Length)
        {
            capacityOverflow = true;
            return;
        }
        records[writeIndex++] = record;
    }

    internal ReviewAnnouncementDiagnosticSnapshot Snapshot()
    {
        var captured = new ReviewAnnouncementDiagnosticRecord[writeIndex];
        Array.Copy(records, captured, writeIndex);
        return new(captured, capacityOverflow, wrongThread, ownerThreadId);
    }
}

public class ReviewSelectionCheckBox : CheckBox
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ReviewSelectionCheckBoxAutomationPeer(this);

    protected override void OnToggle()
    {
        // Selection is projected exclusively from the coordinator snapshot.
        // OnClick still executes the bound command for every activation path.
    }

    internal void ActivateFromAutomation() => OnClick();
}

internal sealed class ReviewSelectionCheckBoxAutomationPeer(ReviewSelectionCheckBox owner)
    : CheckBoxAutomationPeer(owner), IToggleProvider
{
    private ReviewSelectionCheckBox ReviewOwner => (ReviewSelectionCheckBox)Owner;

    void IToggleProvider.Toggle()
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();
        ReviewOwner.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(ReviewOwner.ActivateFromAutomation));
    }

    ToggleState IToggleProvider.ToggleState => ReviewOwner.IsChecked switch
    {
        true => ToggleState.On,
        false => ToggleState.Off,
        null => ToggleState.Indeterminate,
    };
}



