using System.ComponentModel;
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
using PhotoSift.Domain.Workflow;

namespace PhotoSift.App;

public partial class MainWindow : Window
{
    private ShellViewModel? shellViewModel;
    private int dataContextGeneration;
    private bool isClosed;
    private int reviewAnnouncementGeneration;
    private IInputElement? lastReviewFocus;

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
        if (eventArgs.PropertyName is nameof(ShellViewModel.SelectionSummary)
            or nameof(ShellViewModel.ReviewAnnouncement))
        {
            QueueReviewAnnouncement(eventArgs.PropertyName);
        }

        if (eventArgs.PropertyName == nameof(ShellViewModel.IsBlocked) &&
            shellViewModel?.IsBlocked is true)
        {
            FocusBlockingNotice();
        }
    }

    private void OnReviewKeyboardFocusChanged(object sender, KeyboardFocusChangedEventArgs eventArgs)
    {
        if (shellViewModel?.IsReviewPhase is true && eventArgs.NewFocus is not null)
            lastReviewFocus = eventArgs.NewFocus;
    }

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

    private void QueueReviewAnnouncement(string propertyName)
    {
        var generation = dataContextGeneration;
        var expected = shellViewModel;
        var announcement = Interlocked.Increment(ref reviewAnnouncementGeneration);
        Dispatcher.InvokeAsync(() =>
        {
            if (isClosed || generation != dataContextGeneration || announcement != reviewAnnouncementGeneration
                || !ReferenceEquals(expected, shellViewModel) || expected?.IsReviewPhase is not true) return;
            var target = propertyName == nameof(ShellViewModel.SelectionSummary)
                ? SelectionSummaryText : ReviewAnnouncementText;
            if (!target.IsVisible) return;
            var peer = UIElementAutomationPeer.FromElement(target)
                ?? UIElementAutomationPeer.CreatePeerForElement(target);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }, DispatcherPriority.Background);
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs) => CancelBlockingReviewLayer();

    public void OnApplicationExit(object? sender, EventArgs eventArgs) => CancelBlockingReviewLayer();

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
        lastReviewFocus = null;
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



