using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoSift.App;
using PhotoSift.App.Shell;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scope;
using PhotoSift.Domain.Workflow;
using Xunit;
using WpfPath = System.Windows.Shapes.Path;

namespace PhotoSift.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfScopeAccessibilityTests
{
    [Theory]
    [InlineData(ScopeVolumeKind.Fixed, "Fixed")]
    [InlineData(ScopeVolumeKind.Removable, "Removable")]
    public void ReadySurfaceUsesAccessibleBuiltInControlsAndSystemPresentation(
        ScopeVolumeKind volume,
        string expectedVolumeValue)
    {
        RunOnSta(() =>
        {
            var workflow = new PublishingChooseScopeWorkflow(ReadySnapshot(volume));
            var viewModel = new ShellViewModel(workflow);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                ShowAndDrain(window);

                var selectedPath = Assert.IsType<TextBox>(
                    window.FindName("SelectedPathTextBox"));
                var resolvedPath = Assert.IsType<TextBox>(
                    window.FindName("ResolvedPathTextBox"));
                var status = Assert.IsType<TextBlock>(
                    window.FindName("ScopeStatusText"));
                var ruleSet = Assert.IsType<TextBlock>(
                    window.FindName("RuleSetStatusText"));
                var blocking = Assert.IsType<Border>(
                    window.FindName("BlockingNotice"));
                var recursion = Assert.IsType<CommandSwitchCheckBox>(
                    window.FindName("IncludeSubfoldersCheckBox"));
                var summary = Assert.IsType<ItemsControl>(
                    window.FindName("ScopeSummaryList"));

                Assert.Equal(@"C:\Photos", selectedPath.Text);
                Assert.Equal("Selected folder", AutomationProperties.GetName(selectedPath));
                Assert.True(selectedPath.IsReadOnly);
                Assert.Equal(TextWrapping.Wrap, selectedPath.TextWrapping);
                Assert.Equal(@"\\?\C:\Photos", resolvedPath.Text);
                Assert.Equal("Resolved folder path", AutomationProperties.GetName(resolvedPath));
                Assert.True(resolvedPath.IsReadOnly);
                Assert.Equal(TextWrapping.Wrap, resolvedPath.TextWrapping);
                Assert.Equal("Ready", status.Text);
                Assert.Equal(
                    AutomationLiveSetting.Polite,
                    AutomationProperties.GetLiveSetting(status));
                Assert.Equal("Scan options: Not configured", ruleSet.Text);
                Assert.Equal("Include subfolders", AutomationProperties.GetName(recursion));
                Assert.True(recursion.IsChecked);
                Assert.Equal(7, summary.Items.Count);
                var volumeSummary = Assert.IsType<ScopeSummaryItemViewModel>(
                    summary.Items[0]);
                Assert.Equal("Local drive type", volumeSummary.Label);
                Assert.Equal(expectedVolumeValue, volumeSummary.Value);
                Assert.Equal(Visibility.Collapsed, blocking.Visibility);

                AssertSystemBrushesRemainDynamic(window);
                AssertLaterPhaseButtonsStayUnavailable(window);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RecursionControlStaysDisabledUntilASelectionIsPublished()
    {
        RunOnSta(() =>
        {
            var workflow = new PublishingChooseScopeWorkflow(ColdSnapshot());
            var viewModel = new ShellViewModel(workflow);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                ShowAndDrain(window);

                var recursion = Assert.IsType<CommandSwitchCheckBox>(
                    window.FindName("IncludeSubfoldersCheckBox"));
                Assert.Equal(string.Empty, viewModel.SelectedPath);
                Assert.False(recursion.IsChecked);
                Assert.False(recursion.IsEnabled);

                workflow.Publish(CheckingSnapshot(ScopeRecursion.IncludeSubfolders));
                DrainDispatcher();

                Assert.Equal(@"C:\Photos", viewModel.SelectedPath);
                Assert.True(recursion.IsEnabled);
                Assert.True(recursion.IsChecked);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ChooseButtonNameMatchesItsVisibleActionAsSelectionStateChanges()
    {
        RunOnSta(() =>
        {
            var workflow = new PublishingChooseScopeWorkflow(ColdSnapshot());
            var viewModel = new ShellViewModel(workflow);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                ShowAndDrain(window);

                var choose = Assert.IsType<Button>(
                    window.FindName("ChooseAnotherScopeButton"));
                Assert.Equal("Choose folder", choose.Content);
                Assert.Equal("Choose folder", AutomationProperties.GetName(choose));

                workflow.Publish(CheckingSnapshot(ScopeRecursion.TopLevelOnly));
                DrainDispatcher();

                Assert.Equal("Choose another folder", choose.Content);
                Assert.Equal(
                    "Choose another folder",
                    AutomationProperties.GetName(choose));
                Assert.True(choose.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void StatusTransitionsRaiseOneLiveRegionEventForCheckingAndCompletion()
    {
        RunOnSta(() =>
        {
            var workflow = new PublishingChooseScopeWorkflow(ColdSnapshot());
            var viewModel = new ShellViewModel(workflow);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                ShowAndDrain(window);

                var status = Assert.IsType<TextBlock>(
                    window.FindName("ScopeStatusText"));
                var statusElement = FindStatusAutomationElement(window, status);
                using var liveEvents = new LiveRegionEventRecorder(statusElement);

                workflow.Publish(CheckingSnapshot(ScopeRecursion.TopLevelOnly));
                AssertLiveRegionCount(liveEvents, expectedCount: 1);

                Assert.Equal("Checking location and access", status.Text);
                Assert.Collection(
                    liveEvents.ItemStatuses,
                    itemStatus => Assert.Equal(
                        "Checking location and access",
                        itemStatus));

                workflow.Publish(ReadySnapshot());
                AssertLiveRegionCount(liveEvents, expectedCount: 2);

                Assert.Equal("Ready", status.Text);
                Assert.Collection(
                    liveEvents.ItemStatuses,
                    itemStatus => Assert.Equal(
                        "Checking location and access",
                        itemStatus),
                    itemStatus => Assert.Equal("Ready", itemStatus));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void StatusIconChangesGeometryAndAutomationMeaningWithoutChangingColor()
    {
        RunOnSta(() =>
        {
            var workflow = new PublishingChooseScopeWorkflow(ReadySnapshot());
            var viewModel = new ShellViewModel(workflow);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                ShowAndDrain(window);

                var icon = Assert.IsType<WpfPath>(window.FindName("ScopeStatusIcon"));
                var readyGeometry = Assert.IsAssignableFrom<Geometry>(icon.Data)
                    .ToString(CultureInfo.InvariantCulture);
                Assert.Equal(Visibility.Visible, icon.Visibility);
                Assert.Equal("Folder status: Ready", AutomationProperties.GetName(icon));
                Assert.Equal("Ready", AutomationProperties.GetItemStatus(icon));

                var systemTextBrush = new SolidColorBrush(
                    Color.FromRgb(34, 68, 102));
                window.Resources[SystemColors.WindowTextBrushKey] = systemTextBrush;
                DrainDispatcher();
                Assert.Same(systemTextBrush, icon.Fill);

                workflow.Publish(BlockedSnapshot());
                DrainDispatcher();

                var blockedGeometry = Assert.IsAssignableFrom<Geometry>(icon.Data)
                    .ToString(CultureInfo.InvariantCulture);
                Assert.NotEqual(readyGeometry, blockedGeometry);
                Assert.Equal("Folder status: Blocked", AutomationProperties.GetName(icon));
                Assert.Equal("Blocked", AutomationProperties.GetItemStatus(icon));
                Assert.Same(systemTextBrush, icon.Fill);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void BlockedPublicationFocusesNoticeAndKeepsRecoveryActionsKeyboardReachable()
    {
        RunOnSta(() =>
        {
            var workflow = new PublishingChooseScopeWorkflow(ReadySnapshot());
            var viewModel = new ShellViewModel(workflow);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                ShowAndDrain(window);

                workflow.Publish(BlockedSnapshot());
                DrainDispatcher();

                var status = Assert.IsType<TextBlock>(
                    window.FindName("ScopeStatusText"));
                var blocking = Assert.IsType<Border>(
                    window.FindName("BlockingNotice"));
                var retry = Assert.IsType<Button>(
                    window.FindName("RetryScopeButton"));
                var chooseAnother = Assert.IsType<Button>(
                    window.FindName("ChooseAnotherScopeButton"));

                Assert.Equal("Blocked", status.Text);
                Assert.Equal(Visibility.Visible, blocking.Visibility);
                Assert.Equal(
                    "Choose a folder on a local drive.",
                    AutomationProperties.GetName(blocking));
                Assert.True(blocking.IsKeyboardFocusWithin);
                Assert.True(retry.IsEnabled);
                Assert.True(retry.Focusable);
                Assert.True(retry.IsTabStop);
                Assert.Equal("Check the folder again", AutomationProperties.GetName(retry));
                Assert.Same(viewModel.RetryScopeCommand, retry.Command);
                Assert.True(chooseAnother.IsEnabled);
                Assert.True(chooseAnother.Focusable);
                Assert.True(chooseAnother.IsTabStop);
                Assert.Equal("Choose another folder", AutomationProperties.GetName(chooseAnother));
                Assert.Same(viewModel.ChooseScopeCommand, chooseAnother.Command);
                AssertLaterPhaseButtonsStayUnavailable(window);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task FolderPickerHonorsPreCancellationBeforeAccessingItsOwner()
    {
        var ownerWasAccessed = false;
        var picker = new WpfLocalFolderPicker(
            () =>
            {
                ownerWasAccessed = true;
                return null;
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => picker.PickSingleAsync(cancellation.Token));

        Assert.False(ownerWasAccessed);
    }

    [Fact]
    public void AutomationToggleOnRecursionSwitchRequestsANewPreflight()
    {
        RunOnSta(() =>
        {
            var workflow = new PublishingChooseScopeWorkflow(
                CheckingSnapshot(ScopeRecursion.TopLevelOnly));
            var viewModel = new ShellViewModel(workflow);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                ShowAndDrain(window);
                var recursion = Assert.IsType<CommandSwitchCheckBox>(
                    window.FindName("IncludeSubfoldersCheckBox"));
                var peer = UIElementAutomationPeer.CreatePeerForElement(recursion);
                Assert.NotNull(peer);

                Assert.IsAssignableFrom<IToggleProvider>(
                    peer.GetPattern(PatternInterface.Toggle)).Toggle();
                DrainDispatcher();

                // Narrator and UI Automation must change the Scope, not only the switch.
                Assert.True(Assert.Single(workflow.RecursionRequests));
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void AssertSystemBrushesRemainDynamic(Window window)
    {
        var windowBrush = new SolidColorBrush(Color.FromRgb(17, 34, 51));
        var textBrush = new SolidColorBrush(Color.FromRgb(238, 221, 204));

        window.Resources[SystemColors.WindowBrushKey] = windowBrush;
        window.Resources[SystemColors.WindowTextBrushKey] = textBrush;
        DrainDispatcher();

        Assert.Same(windowBrush, window.Background);
        Assert.Same(textBrush, window.Foreground);
    }

    private static void AssertLaterPhaseButtonsStayUnavailable(MainWindow window)
    {
        var phaseRail = Assert.IsType<ItemsControl>(window.FindName("PhaseRail"));
        phaseRail.UpdateLayout();
        var phaseButtons = VisualDescendants<Button>(phaseRail)
            .Where(button => button.DataContext is PhaseItemViewModel)
            .ToArray();

        Assert.Equal(4, phaseButtons.Length);
        Assert.True(phaseButtons[0].IsEnabled);
        Assert.All(phaseButtons.Skip(1), button => Assert.False(button.IsEnabled));
    }

    private static AutomationElement FindStatusAutomationElement(
        Window window,
        TextBlock status)
    {
        var root = AutomationElement.FromHandle(new WindowInteropHelper(window).Handle);
        var statusElement = root.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(
                    AutomationElement.NameProperty,
                    AutomationProperties.GetName(status)),
                new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    ControlType.Text)));
        return statusElement ?? throw new InvalidOperationException(
            "ScopeStatusText was not exposed in the UI Automation tree.");
    }

    private static void AssertLiveRegionCount(
        LiveRegionEventRecorder recorder,
        int expectedCount)
    {
        var timeout = Stopwatch.StartNew();
        while (recorder.Count < expectedCount &&
               timeout.Elapsed < TimeSpan.FromSeconds(2))
        {
            DrainDispatcher();
            recorder.WaitForSignal(TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(expectedCount, recorder.Count);

        var settling = Stopwatch.StartNew();
        while (settling.Elapsed < TimeSpan.FromMilliseconds(100))
        {
            DrainDispatcher();
            recorder.WaitForSignal(TimeSpan.FromMilliseconds(10));
        }

        Assert.Equal(expectedCount, recorder.Count);
    }

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in VisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void ShowAndDrain(Window window)
    {
        window.Show();
        window.Activate();
        window.UpdateLayout();
        DrainDispatcher();
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new DispatcherOperationCallback(
                _ =>
                {
                    frame.Continue = false;
                    return null;
                }),
            null);
        Dispatcher.PushFrame(frame);
    }

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
        {
            throw new TimeoutException("The WPF STA test did not complete within 20 seconds.");
        }

        failure?.Throw();
    }

    private static ShellSnapshot ColdSnapshot() =>
        new(
            InitialWorkflow.Create(),
            "Choose one local folder to scan.",
            ScopeSessionState.Cold);

    private static ShellSnapshot CheckingSnapshot(ScopeRecursion recursion) =>
        new(
            InitialWorkflow.Create(),
            "Checking location and access.",
            ScopeSessionState.Checking,
            new ScopeSelection(@"C:\Photos", recursion),
            Revision: 1);

    private static ShellSnapshot ReadySnapshot(
        ScopeVolumeKind volume = ScopeVolumeKind.Fixed)
    {
        var selection = new ScopeSelection(
            @"C:\Photos",
            ScopeRecursion.IncludeSubfolders);
        var result = new ScopePreflightResult(
            Decision: ScopePreflightDecision.Ready,
            Scope: new ResolvedScope(
                selection,
                @"\\?\C:\Photos",
                new DirectoryIdentity(1, 2, 3)),
            Volume: volume,
            CompleteCounts: new ScopeOutcomeCounts(12, 3, 4, 5),
            Restrictions: new ScopeRestrictionCounts(6, 7),
            BlockingReasons: ImmutableArray<ScopeBlockingReason>.Empty);
        return new ShellSnapshot(
            InitialWorkflow.Create(),
            "Scope is ready.",
            ScopeSessionState.Ready,
            selection,
            result,
            "Not configured",
            Revision: 1);
    }

    private static ShellSnapshot BlockedSnapshot()
    {
        var selection = new ScopeSelection(
            @"C:\Photos",
            ScopeRecursion.IncludeSubfolders);
        var result = new ScopePreflightResult(
            Decision: ScopePreflightDecision.Blocked,
            Scope: new ResolvedScope(
                selection,
                @"\\?\C:\Photos",
                new DirectoryIdentity(1, 2, 3)),
            Volume: ScopeVolumeKind.Remote,
            CompleteCounts: null,
            Restrictions: new ScopeRestrictionCounts(0, 0),
            BlockingReasons: ImmutableArray.Create(ScopeBlockingReason.NonLocalRoot));
        return new ShellSnapshot(
            InitialWorkflow.Create(),
            "Scope is blocked.",
            ScopeSessionState.Blocked,
            selection,
            result,
            "Not configured",
            Revision: 2);
    }

    private sealed class PublishingChooseScopeWorkflow(ShellSnapshot current)
        : IChooseScopeWorkflow
    {
        public event EventHandler<ShellSnapshot>? SnapshotChanged;

        public ShellSnapshot Current { get; private set; } = current;

        public Task HandleAsync(
            ChooseScopeCommand command,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public List<bool> RecursionRequests { get; } = [];

        public Task HandleAsync(
            ChangeScopeRecursionCommand command,
            CancellationToken cancellationToken)
        {
            RecursionRequests.Add(command.IncludeSubfolders);
            return Task.CompletedTask;
        }

        public Task HandleAsync(
            RetryScopePreflightCommand command,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Publish(ShellSnapshot snapshot)
        {
            Current = snapshot;
            SnapshotChanged?.Invoke(this, snapshot);
        }
    }

    private sealed class LiveRegionEventRecorder : IDisposable
    {
        private readonly AutomationElement element;
        private readonly AutomationEventHandler handler;
        private readonly AutoResetEvent signal = new(initialState: false);
        private readonly Lock itemStatusesLock = new();
        private readonly List<string> itemStatuses = [];
        private int count;

        public LiveRegionEventRecorder(AutomationElement element)
        {
            this.element = element;
            handler = OnAutomationEvent;
            Automation.AddAutomationEventHandler(
                AutomationElementIdentifiers.LiveRegionChangedEvent,
                element,
                TreeScope.Element,
                handler);
        }

        public int Count => Volatile.Read(ref count);

        public string[] ItemStatuses
        {
            get
            {
                lock (itemStatusesLock)
                {
                    return [.. itemStatuses];
                }
            }
        }

        public void WaitForSignal(TimeSpan timeout) => signal.WaitOne(timeout);

        public void Dispose()
        {
            Automation.RemoveAutomationEventHandler(
                AutomationElementIdentifiers.LiveRegionChangedEvent,
                element,
                handler);
            signal.Dispose();
            GC.SuppressFinalize(this);
        }

        private void OnAutomationEvent(object sender, AutomationEventArgs eventArgs)
        {
            if (sender is not AutomationElement source ||
                eventArgs.EventId !=
                    AutomationElementIdentifiers.LiveRegionChangedEvent)
            {
                return;
            }

            lock (itemStatusesLock)
            {
                itemStatuses.Add(source.Current.ItemStatus);
            }

            Interlocked.Increment(ref count);
            signal.Set();
        }
    }
}

