using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PhotoSift.App.Shell;

namespace PhotoSift.App;

public partial class MainWindow : Window
{
    private ShellViewModel? shellViewModel;

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

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (shellViewModel is not null)
        {
            shellViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }
}
