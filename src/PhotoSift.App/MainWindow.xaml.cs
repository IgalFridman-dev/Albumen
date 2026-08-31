using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Threading;
using PhotoSift.App.Shell;

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
