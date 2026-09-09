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
using PhotoSift.Application.Scan;
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
                Assert.Equal("Confirm Exact rules", AutomationProperties.GetName(confirm));
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
    public void LargeReviewActuallyVirtualizesAndExposesSelectableWrappingReadOnlyPaths()
    {
        RunOnSta(() =>
        {
            var fixture = new ExactShellFixture();
            fixture.PublishReview(ScanStatus.Incomplete, 1000);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                Drain();
                window.UpdateLayout();
                var groups = Assert.IsType<ListBox>(window.FindName("ExactGroupsList"));
                Assert.True(VirtualizingPanel.GetIsVirtualizing(groups));
                Assert.Equal(1000, groups.Items.Count);
                Assert.Null(groups.ItemContainerGenerator.ContainerFromIndex(999));
                var paths = Descendants<TextBox>(groups).ToArray();
                Assert.NotEmpty(paths);
                Assert.All(paths, path =>
                {
                    Assert.True(path.IsReadOnly);
                    Assert.Equal(TextWrapping.Wrap, path.TextWrapping);
                    Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(path)));
                });
                var selectionControls = Descendants<ToggleButton>(groups)
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
                Assert.NotEmpty(Assert.IsType<ListBox>(window.FindName("CouldNotAnalyzeList")).Items);
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
            var window = new MainWindow { DataContext = fixture.ViewModel, FontSize = 24 };
            try
            {
                window.Show();
                Drain();
                window.UpdateLayout();
                var groups = Assert.IsType<ListBox>(window.FindName("ExactGroupsList"));
                Assert.True(VirtualizingPanel.GetIsVirtualizing(groups));
                Assert.All(Descendants<TextBox>(groups), path =>
                {
                    Assert.True(path.IsReadOnly);
                    Assert.Equal(TextWrapping.Wrap, path.TextWrapping);
                    Assert.False(path.IsTabStop);
                });
                Assert.All(Descendants<TextBlock>(groups), metadata => Assert.False(metadata.Focusable));
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
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                Drain();
                window.UpdateLayout();
                var groups = Assert.IsType<ListBox>(window.FindName("ExactGroupsList"));
                var members = Assert.Single(Descendants<ListBox>(groups));
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







