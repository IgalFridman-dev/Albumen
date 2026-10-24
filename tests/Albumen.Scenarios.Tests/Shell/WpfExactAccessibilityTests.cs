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
using Albumen.App;
using Albumen.App.Shell;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Domain.Workflow;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

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
                var scanFolder = Assert.IsType<Button>(window.FindName("ScanFolderButton"));
                var subfolders = Assert.IsType<CommandSwitchCheckBox>(window.FindName("IncludeSubfoldersCheckBox"));
                var guidance = Assert.IsType<TextBlock>(window.FindName("ScanOptionsGuidanceText"));
                var choose = Assert.IsType<Button>(window.FindName("ChooseAnotherScopeButton"));
                // Folder and options are one screen: the folder controls stay in view with the options.
                Assert.True(choose.IsVisible && subfolders.IsVisible && scanFolder.IsVisible);
                // Identical copies and look-alikes are always found; no switch can turn either off.
                Assert.Null(window.FindName("ExactDuplicatesCheckBox"));
                Assert.Null(window.FindName("NearDuplicatesCheckBox"));
                Assert.Null(window.FindName("NearDuplicatesCaption"));
                Assert.True(subfolders.IsTabStop && subfolders.Focusable && subfolders.IsEnabled);
                Assert.True(choose.IsTabStop && choose.Focusable && choose.IsEnabled);
                // Valid options before any scan: the guidance has nothing to add, so it is hidden.
                Assert.Equal(Visibility.Collapsed, guidance.Visibility);
                Assert.Equal("Identical copies and look-alikes are always found. Nothing is marked for you.", guidance.Text);
                Assert.Equal("Scan 2 photos", scanFolder.Content);
                Assert.Equal("Scan 2 photos", AutomationProperties.GetName(scanFolder));
                Assert.Same(fixture.ViewModel.ScanFolderCommand, scanFolder.Command);
                Assert.True(scanFolder.IsEnabled && scanFolder.IsTabStop && scanFolder.Focusable);
                Assert.True(scanFolder.IsKeyboardFocusWithin);
                fixture.PublishPhase(WorkflowPhase.Scan, ScanStatus.Running);
                Drain();
                var cancel = Assert.IsType<Button>(window.FindName("CancelScanButton"));
                Assert.True(cancel.IsEnabled && cancel.IsTabStop && cancel.Focusable);
                Assert.Equal("Cancel Scan", AutomationProperties.GetName(cancel));
                var status = Assert.IsType<TextBlock>(window.FindName("ScanStatusText"));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
                Assert.StartsWith("Scanning:", AutomationProperties.GetItemStatus(status), StringComparison.Ordinal);
                fixture.PublishPhase(WorkflowPhase.ChooseScope);
                Drain();
                Assert.True(choose.IsKeyboardFocusWithin);
                Assert.True(subfolders.IsVisible && scanFolder.IsVisible);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void InvalidOptionsExposeLiveGuidanceAndBlockTheScan()
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
                var scanFolder = Assert.IsType<Button>(window.FindName("ScanFolderButton"));

                Assert.Equal(invalid.ValidationGuidance, guidance.Text);
                Assert.Equal(Visibility.Visible, guidance.Visibility);
                Assert.Equal(
                    invalid.ValidationGuidance,
                    AutomationProperties.GetItemStatus(guidance));
                Assert.Equal(
                    AutomationLiveSetting.Polite,
                    AutomationProperties.GetLiveSetting(guidance));
                Assert.False(fixture.ViewModel.CanScanFolder);
                Assert.False(scanFolder.IsEnabled);
                fixture.ViewModel.ScanFolderCommand.ExecuteAsync().GetAwaiter().GetResult();
                Assert.DoesNotContain("start", fixture.Workflow.Actions);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void LargeReviewActuallyVirtualizesItsSetsAndRemovesNothingByItself()
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
                var gallery = Assert.IsType<ListBox>(window.FindName("ReviewGalleryList"));
                Assert.True(VirtualizingPanel.GetIsVirtualizing(gallery));
                Assert.Equal(1000, gallery.Items.Count);
                Assert.Null(gallery.ItemContainerGenerator.ContainerFromIndex(999));
                var tiles = Descendants<Button>(gallery)
                    .Where(tile => AutomationProperties.GetAutomationId(tile) == "review-photo").ToArray();
                Assert.NotEmpty(tiles);
                Assert.All(tiles, tile => Assert.True(tile.IsTabStop && tile.Focusable));
                Assert.DoesNotContain(Descendants<Button>(window), button => button.IsEnabled &&
                    (button.Content?.ToString()?.Contains("Remove", StringComparison.OrdinalIgnoreCase) == true ||
                     button.Name == "ApproveCleanupButton"));
                var textBrush = new SolidColorBrush(Colors.Coral);
                window.Resources["Albumen.TextBrush"] = textBrush;
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
            WpfReviewSelectionTests.OpenIdenticalCopies(fixture.ViewModel);
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

    // A set shows all of its photos, so a 2,000-photo set realizes 2,000 tiles; only the rows in
    // view (and one either side) decode their thumbnails until the gallery scrolls.
    [Fact]
    public void OneLargeExactGroupKeepsThumbnailLoadsToTheVisibleRows()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var scope = new ReviewStaticScope();
            var workflow = new RecordingReviewWorkflow(scope.Current, ReviewData.CreateLargeMemberSnapshot(2000));
            var thumbnails = new ControllableReviewPreviewSource(autoComplete: true);
            var viewModel = new ShellViewModel(scope, workflow, reviewThumbnailSource: thumbnails);
            WpfReviewSelectionTests.OpenIdenticalCopies(viewModel);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                window.Show();
                WpfReviewSelectionTests.Drain();
                window.UpdateLayout();
                WpfReviewSelectionTests.Drain();
                var gallery = Assert.IsType<ListBox>(window.FindName("ReviewGalleryList"));
                Assert.Single(gallery.Items.Cast<object>());
                var tiles = Descendants<Button>(gallery)
                    .Where(tile => AutomationProperties.GetAutomationId(tile) == "review-photo").ToArray();
                Assert.Equal(2000, tiles.Length);
                Assert.NotEmpty(thumbnails.Requests);
                Assert.True(thumbnails.Requests.Count < 40,
                    $"The first rows asked for {thumbnails.Requests.Count} thumbnails.");
                Assert.DoesNotContain(thumbnails.Requests, request => request.Occurrence.Token.OccurrenceId == 2000);

                var scroller = Descendants<ScrollViewer>(gallery).First();
                scroller.ScrollToEnd();
                WpfReviewSelectionTests.Drain();
                window.UpdateLayout();
                WpfReviewSelectionTests.Drain();

                Assert.Contains(thumbnails.Requests, request => request.Occurrence.Token.OccurrenceId == 2000);
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
                    Assert.StartsWith("Scanning:", NextAnnouncement(statuses, "Scanning", cancellationToken));
                    dispatcher.BeginInvoke(publishComplete);
                    Assert.StartsWith("Finished:", NextAnnouncement(statuses, "Finished", cancellationToken));
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
            if (status.StartsWith(requiredStatus + ":", StringComparison.Ordinal)) return status;
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







