using System.Runtime.ExceptionServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoSift.App;
using PhotoSift.App.Shell;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfExactAccessibilityTests
{
    [Fact]
    public void PhaseCommandsAreNamedKeyboardReachableAndFocusFollowsPhase()
    {
        RunOnSta(() =>
        {
            var fixture = new ExactShellFixture();
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                fixture.PublishPhase(WorkflowPhase.ConfirmRules);
                Drain();
                var confirm = Assert.IsType<Button>(window.FindName("ConfirmExactRulesButton"));
                var start = Assert.IsType<Button>(window.FindName("StartScanButton"));
                var exact = Assert.IsType<CheckBox>(window.FindName("ExactDuplicatesCheckBox"));
                var near = Assert.IsType<CheckBox>(window.FindName("NearDuplicatesCheckBox"));
                var guidance = Assert.IsType<TextBlock>(window.FindName("ScanOptionsGuidanceText"));
                Assert.True(exact.IsChecked);
                Assert.False(exact.IsEnabled);
                Assert.Equal("Exact duplicates are always enabled", AutomationProperties.GetName(exact));
                Assert.Equal(
                    "Exact requires identical bytes and cannot be disabled.",
                    AutomationProperties.GetHelpText(exact));
                Assert.True(near.IsChecked);
                Assert.True(near.IsTabStop && near.Focusable && near.IsEnabled);
                Assert.Equal("Conservative Near duplicates", AutomationProperties.GetName(near));
                Assert.Equal(
                    "Uses the fixed precision-first Near profile. Ambiguous matches remain Uncertain.",
                    AutomationProperties.GetHelpText(near));
                Assert.Contains("Exact is always enabled", guidance.Text, StringComparison.Ordinal);
                Assert.Contains("Near", guidance.Text, StringComparison.Ordinal);
                Assert.Equal("Confirm Scan options", AutomationProperties.GetName(confirm));
                Assert.Equal("Start Scan", AutomationProperties.GetName(start));
                Assert.Same(fixture.ViewModel.ConfirmExactRulesCommand, confirm.Command);
                Assert.Same(fixture.ViewModel.StartScanCommand, start.Command);
                Assert.True(confirm.IsTabStop && confirm.Focusable && confirm.IsVisible);
                Assert.True(confirm.IsKeyboardFocusWithin || start.IsKeyboardFocusWithin);
                fixture.PublishPhase(WorkflowPhase.Scan, ScanStatus.Running);
                Drain();
                var cancel = Assert.IsType<Button>(window.FindName("CancelScanButton"));
                Assert.True(cancel.IsEnabled && cancel.IsTabStop && cancel.Focusable);
                Assert.Equal("Cancel Scan", AutomationProperties.GetName(cancel));
                var status = Assert.IsType<TextBlock>(window.FindName("ScanStatusText"));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
                Assert.Contains("Running", AutomationProperties.GetItemStatus(status));
                fixture.PublishPhase(WorkflowPhase.ChooseScope);
                Drain();
                Assert.True(Assert.IsType<Button>(window.FindName("ChooseAnotherScopeButton")).IsKeyboardFocusWithin);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void InvalidOptionsExposeLiveGuidanceAndBlockConfirmationAndStart()
    {
        RunOnSta(() =>
        {
            var fixture = new ExactShellFixture();
            var invalid = new ScanOptions(
                ExactDuplicateMode.Disabled,
                NearDuplicateMode.Conservative);
            fixture.PublishOptions(invalid);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                Drain();
                var guidance = Assert.IsType<TextBlock>(
                    window.FindName("ScanOptionsGuidanceText"));
                var confirm = Assert.IsType<Button>(
                    window.FindName("ConfirmExactRulesButton"));
                var start = Assert.IsType<Button>(window.FindName("StartScanButton"));

                Assert.Equal(invalid.ValidationGuidance, guidance.Text);
                Assert.Equal(
                    invalid.ValidationGuidance,
                    AutomationProperties.GetItemStatus(guidance));
                Assert.Equal(
                    AutomationLiveSetting.Polite,
                    AutomationProperties.GetLiveSetting(guidance));
                Assert.False(confirm.IsEnabled);
                Assert.False(start.IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void LargeReviewActuallyVirtualizesAndExposesSelectableWrappingReadOnlyPaths()
    {
        RunOnSta(() =>
        {
            var fixture = new ExactShellFixture();
            fixture.PublishReview(ScanStatus.Incomplete, 1000);
            var exact = fixture.ViewModel.ReviewCategories.Single(
                category => category.Category == ReviewCategoryKind.Exact);
            fixture.ViewModel.OpenReviewCategoryCommand.ExecuteAsync(exact).GetAwaiter().GetResult();
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                Drain();
                window.UpdateLayout();
                var groups = Assert.IsType<ListBox>(window.FindName("ReviewGroupsList"));
                Assert.True(VirtualizingPanel.GetIsVirtualizing(groups));
                Assert.Equal(1000, groups.Items.Count);
                Assert.Null(groups.ItemContainerGenerator.ContainerFromIndex(999));
                fixture.ViewModel.OpenReviewGroupCommand.ExecuteAsync(exact.Groups[0])
                    .GetAwaiter().GetResult();
                Drain();
                window.UpdateLayout();
                var comparisons = Assert.IsType<ListBox>(window.FindName("ReviewComparisonsList"));
                var paths = Descendants<TextBox>(comparisons).ToArray();
                Assert.NotEmpty(paths);
                Assert.All(paths, path =>
                {
                    Assert.True(path.IsReadOnly);
                    Assert.Equal(TextWrapping.Wrap, path.TextWrapping);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(path)));
                });
                var selectionControls = Descendants<ToggleButton>(comparisons)
                    .Where(control => AutomationProperties.GetAutomationId(control) == "selection-control").ToArray();
                Assert.NotEmpty(selectionControls);
                Assert.All(selectionControls, control => Assert.True(control.IsTabStop));
                Assert.DoesNotContain(Descendants<Button>(window), button => button.IsEnabled &&
                    (button.Content?.ToString()?.Contains("Remove", StringComparison.OrdinalIgnoreCase) == true ||
                     button.Content?.ToString() == "Approve Cleanup"));
                var textBrush = new SolidColorBrush(Colors.Coral);
                window.Resources[SystemColors.WindowTextBrushKey] = textBrush;
                Drain();
                Assert.Same(textBrush, window.Foreground);
                Assert.True(fixture.ViewModel.ReviewCategories.Single(category =>
                    category.Category == ReviewCategoryKind.CouldNotAnalyze).GroupCount > 0);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ReviewMetadataRemainsReadOnlyNonTabStopContentAtTwoHundredPercentTextScale()
    {
        RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready();
            WpfReviewSelectionTests.OpenFirstGroupedComparison(fixture.ViewModel);
            var window = new MainWindow { DataContext = fixture.ViewModel, FontSize = 24 };
            try
            {
                window.Show();
                Drain();
                window.UpdateLayout();
                var comparisons = Assert.IsType<ListBox>(window.FindName("ReviewComparisonsList"));
                Assert.True(VirtualizingPanel.GetIsVirtualizing(comparisons));
                Assert.All(Descendants<TextBox>(comparisons), path =>
                {
                    Assert.True(path.IsReadOnly);
                    Assert.Equal(TextWrapping.Wrap, path.TextWrapping);
                    Assert.False(path.IsTabStop);
                });
                Assert.All(Descendants<TextBlock>(comparisons), metadata => Assert.False(metadata.Focusable));
                Assert.Contains("照片-very-long-name", fixture.ViewModel.ExactGroups[0].Members[0].DiscoveredPath);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void OneLargeExactGroupKeepsItsMemberViewportVirtualized()
    {
        RunOnSta(() =>
        {
            var fixture = new ExactShellFixture();
            fixture.PublishReview(ScanStatus.Complete, memberCount: 2000);
            WpfReviewSelectionTests.OpenFirstGroupedComparison(fixture.ViewModel);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                Drain();
                window.UpdateLayout();
                var members = Assert.IsType<ListBox>(window.FindName("ReviewComparisonsList"));
                Assert.Equal(2000, members.Items.Count);
                Assert.True(VirtualizingPanel.GetIsVirtualizing(members));
                Assert.Null(members.ItemContainerGenerator.ContainerFromIndex(1999));
                members.ScrollIntoView(members.Items[1999]);
                Drain();
                window.UpdateLayout();
                Assert.NotNull(members.ItemContainerGenerator.ContainerFromIndex(1999));
                Assert.True(Descendants<ListBoxItem>(members).Count() < 100);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void RunningAndCompletionRaiseAccessibleLiveAnnouncements()
    {
        RunOnSta(() =>
        {
            var fixture = new ExactShellFixture();
            fixture.PublishPhase(WorkflowPhase.Scan);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                window.Activate();
                window.UpdateLayout();
                Drain();
                var handle = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(nint.Zero, handle);
                VerifyAnnouncementsOnMta(handle,
                    () => fixture.PublishPhase(WorkflowPhase.Scan, ScanStatus.Running),
                    () => fixture.PublishPhase(WorkflowPhase.Scan, ScanStatus.Complete));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void OpenReviewFailureRaisesLiveAnnouncementWhilePhaseRemainsScan()
    {
        RunOnSta(() =>
        {
            var fixture = new ExactShellFixture();
            var failedPin = fixture.PublishFreshScan(ScanStatus.Complete);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                window.Activate();
                window.UpdateLayout();
                Drain();
                var handle = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(nint.Zero, handle);

                VerifyReviewFailureAnnouncementOnMta(handle,
                    () => fixture.ViewModel.OpenReviewCommand.ExecuteAsync());

                Assert.True(fixture.ViewModel.IsScanPhase);
                Assert.Contains("invalid, stale, or faulted", fixture.ViewModel.ReviewAnnouncement,
                    StringComparison.Ordinal);

                fixture.PublishScan(failedPin, ScanStatus.Incomplete, includeOccurrence: true);
                Drain();

                Assert.Contains("invalid, stale, or faulted", fixture.ViewModel.ReviewAnnouncement,
                    StringComparison.Ordinal);

                fixture.PublishFreshScan();
                Drain();

                Assert.True(fixture.ViewModel.IsScanPhase);
                Assert.Empty(fixture.ViewModel.ReviewAnnouncement);
            }
            finally { window.Close(); }
        });
    }

    // UIA uses window messages, so a client inspecting its own UI must use a
    // windowless MTA while the window's STA continues pumping. Registration and
    // removal stay on that same MTA for the lifetime of both assertions.
    private static void VerifyAnnouncementsOnMta(nint handle, Action publishRunning, Action publishComplete)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var cancellationToken = TestContext.Current.CancellationToken;
        var frame = new DispatcherFrame();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ExceptionDispatchInfo? failure = null;
        var client = new Thread(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(handle);
                var element = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "Scan status"));
                Assert.NotNull(element);
                using var statuses = new BlockingCollection<string>();
                AutomationEventHandler handler = (sender, _) =>
                {
                    if (sender is AutomationElement source)
                    {
                        statuses.Add(source.Current.ItemStatus, cancellationToken);
                    }
                };
                Automation.AddAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent,
                    element, TreeScope.Element, handler);
                try
                {
                    dispatcher.BeginInvoke(publishRunning);
                    Assert.StartsWith("Running;", NextAnnouncement(statuses, "Running", cancellationToken));
                    dispatcher.BeginInvoke(publishComplete);
                    Assert.StartsWith("Complete;", NextAnnouncement(statuses, "Complete", cancellationToken));
                }
                finally
                {
                    Automation.RemoveAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent,
                        element, handler);
                }
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(new InvalidOperationException(
                    $"UIA MTA verification failed ({error.GetType().Name}, HRESULT 0x{error.HResult:X8}).", error));
            }
            finally
            {
                finished.SetResult();
                dispatcher.BeginInvoke(new Action(() => frame.Continue = false));
            }
        }) { IsBackground = true };
        client.SetApartmentState(ApartmentState.MTA);
        var timeout = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(8) };
        timeout.Tick += (_, _) => frame.Continue = false;
        client.Start();
        timeout.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        Assert.True(finished.Task.IsCompletedSuccessfully, "The UIA MTA client exceeded eight seconds.");
        failure?.Throw();
    }

    private static string NextAnnouncement(BlockingCollection<string> statuses, string requiredStatus,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        string? observed = null;
        while (true)
        {
            // A live-region callback reads the element's current ItemStatus,
            // so queued events can repeat a status. Require the actual desired
            // event, without restarting the deadline for unrelated deliveries.
            var remaining = 4000 - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.True(remaining > 0 && statuses.TryTake(out observed, remaining, cancellationToken),
                $"No {requiredStatus} live-region announcement arrived within four seconds. Last status: {observed ?? "none"}.");
            var status = Assert.IsType<string>(observed);
            if (status.StartsWith(requiredStatus + ";", StringComparison.Ordinal)) return status;
        }
    }

    private static void VerifyReviewFailureAnnouncementOnMta(nint handle, Func<Task> publish)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var cancellationToken = TestContext.Current.CancellationToken;
        var frame = new DispatcherFrame();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ExceptionDispatchInfo? failure = null;
        var client = new Thread(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(handle);
                var element = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "Review command status"));
                Assert.NotNull(element);
                using var statuses = new BlockingCollection<string>();
                AutomationEventHandler handler = (sender, _) =>
                {
                    if (sender is AutomationElement source)
                        statuses.Add(source.Current.ItemStatus, cancellationToken);
                };
                Automation.AddAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent,
                    element, TreeScope.Element, handler);
                try
                {
                    Task? published = null;
                    var operation = dispatcher.BeginInvoke(new Action(() => published = publish()));
                    operation.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                        .GetAwaiter().GetResult();
                    Assert.NotNull(published);
                    published.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                        .GetAwaiter().GetResult();
                    Assert.Contains("invalid, stale, or faulted",
                        NextReviewFailureAnnouncement(statuses, cancellationToken),
                        StringComparison.Ordinal);
                }
                finally
                {
                    Automation.RemoveAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent,
                        element, handler);
                }
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(new InvalidOperationException(
                    $"Review failure UIA MTA verification failed ({error.GetType().Name}, HRESULT 0x{error.HResult:X8}).", error));
            }
            finally
            {
                finished.SetResult();
                dispatcher.BeginInvoke(new Action(() => frame.Continue = false));
            }
        }) { IsBackground = true };
        client.SetApartmentState(ApartmentState.MTA);
        var timeout = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(8) };
        timeout.Tick += (_, _) => frame.Continue = false;
        client.Start();
        timeout.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timeout.Stop(); }
        Assert.True(finished.Task.IsCompletedSuccessfully, "The Review failure UIA client exceeded eight seconds.");
        failure?.Throw();
    }

    private static string NextReviewFailureAnnouncement(
        BlockingCollection<string> statuses,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        string? observed = null;
        while (true)
        {
            var remaining = 4000 - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.True(remaining > 0 && statuses.TryTake(out observed, remaining, cancellationToken),
                $"No Review failure live-region announcement arrived within four seconds. Last status: {observed ?? "none"}.");
            var status = Assert.IsType<string>(observed);
            if (status.StartsWith("Review could not open", StringComparison.Ordinal)) return status;
        }
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Exact WPF scenario exceeded ten seconds.");
        failure?.Throw();
    }
}







