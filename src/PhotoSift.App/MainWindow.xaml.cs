using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using PhotoSift.App.Shell;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.App;

public partial class MainWindow : Window
{
    private ShellViewModel? shellViewModel;
    private int dataContextGeneration;
    private bool isClosed;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
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

        if (eventArgs.PropertyName == nameof(ShellViewModel.IsBlocked) &&
            shellViewModel?.IsBlocked is true)
        {
            FocusBlockingNotice();
        }
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

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        isClosed = true;
        dataContextGeneration++;
        if (shellViewModel is not null)
        {
            shellViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }
}



