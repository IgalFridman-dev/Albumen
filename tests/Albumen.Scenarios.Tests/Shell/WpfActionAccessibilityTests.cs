using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Albumen.App;
using Albumen.Application.Action;
using Albumen.Domain.Action;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfActionAccessibilityTests
{
    private static readonly string[] prohibitedActionControls = ["retry", "reconcile", "permanent", "delete"];
    [Fact]
    public Task ActionRowsExposeOriginalPathsStatesReasonsAndCertaintyInSourceOrder() =>
        WpfScenarioIsolation.RunAsync(typeof(WpfActionAccessibilityTests), "action-rows",
            nameof(ActionRowsExposeOriginalPathsStatesReasonsAndCertaintyInSourceOrder), stage =>
        {
            WpfReviewSelectionTests.RunOnSta(() =>
            {
                var fixture = new ActionShellFixture();
                fixture.Publish(ActionShellFixture.Snapshot(
                    new ActionItemSnapshot(ActionShellFixture.Item(2), Guid.NewGuid(), ActionItemState.Recycled, ActionSafetyReason.None),
                    new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(), ActionItemState.UnknownPendingRevalidation, ActionSafetyReason.JournalUnavailable)));
                var window = new MainWindow { DataContext = fixture.ViewModel };
                try
                {
                    window.Show();
                    WpfReviewSelectionTests.Drain();
                    var panel = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ActionPanel"));
                    Assert.True(panel.IsVisible);
                    Assert.Equal("action-panel", AutomationProperties.GetAutomationId(panel));
                    var list = Assert.IsType<ListBox>(window.FindName("ActionItemsList"));
                    Assert.Equal(2, list.Items.Count);
                    foreach (var (item, position) in list.Items.Cast<object>().Select((item, index) => (item, index)))
                    {
                        list.ScrollIntoView(item);
                        window.UpdateLayout();
                        WpfReviewSelectionTests.Drain();
                        var container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(item));
                        var row = WpfReviewSelectionTests.Descendants<FrameworkElement>(container)
                            .Single(element => AutomationProperties.GetAutomationId(element) == "action-item");
                        var peer = UIElementAutomationPeer.CreatePeerForElement(row);
                        Assert.NotNull(peer);
                        var expectedPath = ActionShellFixture.Item(position == 0 ? 2 : 1).DiscoveredPath;
                        Assert.Contains(expectedPath, peer.GetName());
                        Assert.Contains("Windows Recycle Bin", peer.GetName());
                        Assert.Contains($"{position + 1} of 2", peer.GetName());
                        Assert.Contains(position == 0 ? "Recycled" : "Unknown pending revalidation", peer.GetItemStatus(), StringComparison.OrdinalIgnoreCase);
                        if (position == 1)
                        {
                            Assert.Contains("uncertain", peer.GetItemStatus(), StringComparison.OrdinalIgnoreCase);
                            Assert.Contains("journal", peer.GetItemStatus(), StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    var texts = WpfReviewSelectionTests.Descendants<TextBlock>(panel).Select(text => text.Text).ToArray();
                    Assert.Contains(texts, text => text.Contains("Selection", StringComparison.OrdinalIgnoreCase) && text.Contains("sealed", StringComparison.OrdinalIgnoreCase));
                    Assert.DoesNotContain(WpfReviewSelectionTests.Descendants<Button>(panel), button =>
                        prohibitedActionControls.Any(word =>
                            (button.Content?.ToString() ?? "").Contains(word, StringComparison.OrdinalIgnoreCase)));
                }
                finally { window.Close(); WpfReviewSelectionTests.Drain(); }
                stage.CleanupSucceeded();
            });
        });

    [Fact]
    public Task ActionSupportsKeyboardFocusContrastResourcesAndTwoHundredPercentText() =>
        WpfScenarioIsolation.RunAsync(typeof(WpfActionAccessibilityTests), "action-layout",
            nameof(ActionSupportsKeyboardFocusContrastResourcesAndTwoHundredPercentText), stage =>
        {
            WpfReviewSelectionTests.RunOnSta(() =>
            {
                var fixture = new ActionShellFixture();
                var window = new MainWindow { DataContext = fixture.ViewModel, Width = 960, Height = 720 };
                try
                {
                    window.Show();
                    WpfReviewSelectionTests.Drain();
                    fixture.Publish(ActionShellFixture.Snapshot(new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(),
                        ActionItemState.InterruptedBeforeExecution, ActionSafetyReason.Interrupted)));
                    WpfReviewSelectionTests.Drain();
                    var heading = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ActionHeading"));
                    Assert.True(heading.IsKeyboardFocused);
                    Assert.NotNull(heading.FocusVisualStyle);
                    Assert.True(heading.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                    Assert.NotSame(heading, Keyboard.FocusedElement);
                    var panel = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ActionPanel"));
                    foreach (var text in WpfReviewSelectionTests.Descendants<TextBlock>(panel)) text.FontSize *= 2;
                    foreach (var path in WpfReviewSelectionTests.Descendants<TextBox>(panel)) path.FontSize *= 2;
                    window.Resources["Albumen.WindowBrush"] = Brushes.Black;
                    window.Resources["Albumen.TextBrush"] = Brushes.White;
                    window.Resources["Albumen.SurfaceBrush"] = Brushes.Black;
                    window.Resources["Albumen.MutedTextBrush"] = Brushes.White;
                    window.UpdateLayout();
                    WpfReviewSelectionTests.Drain();
                    Assert.Same(Brushes.Black, window.Background);
                    Assert.Same(Brushes.White, window.Foreground);
                    var list = Assert.IsType<ListBox>(window.FindName("ActionItemsList"));
                    list.ScrollIntoView(list.Items[0]);
                    window.UpdateLayout();
                    var paths = WpfReviewSelectionTests.Descendants<TextBox>(list).Where(path => path.Text.Contains("תצלום", StringComparison.Ordinal)).ToArray();
                    Assert.NotEmpty(paths);
                    Assert.All(paths, path =>
                    {
                        Assert.True(path.IsReadOnly);
                        Assert.Equal(TextWrapping.Wrap, path.TextWrapping);
                        Assert.Equal(ActionShellFixture.Item(1).DiscoveredPath, path.Text);
                        Assert.True(path.ActualWidth > 0 && path.ActualWidth <= list.ActualWidth);
                        Assert.True(path.ActualHeight > path.FontSize);
                    });
                    Assert.Empty(fixture.Approvals);
                }
                finally { window.Close(); WpfReviewSelectionTests.Drain(); }
                stage.CleanupSucceeded();
            });
        });

    [Theory]
    [InlineData(960d, 720d)]
    [InlineData(680d, 520d)]
    public void ActionPageKeepsProgressAndNoticesReachableWithoutMaximizing(double width, double height) =>
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new ActionShellFixture();
            fixture.Publish(ActionShellFixture.Snapshot(
                new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(), ActionItemState.Recycled, ActionSafetyReason.None),
                new ActionItemSnapshot(ActionShellFixture.Item(2), Guid.NewGuid(), ActionItemState.UnknownPendingRevalidation, ActionSafetyReason.JournalUnavailable)));
            var window = new MainWindow { DataContext = fixture.ViewModel, Width = width, Height = height };
            try
            {
                window.Show();
                window.UpdateLayout();
                WpfReviewSelectionTests.Drain();
                var page = Assert.IsType<ScrollViewer>(window.FindName("ActionPageScrollViewer"));
                var list = Assert.IsType<ListBox>(window.FindName("ActionItemsList"));
                Assert.True(page.IsVisible);
                Assert.True(list.ActualHeight <= page.ViewportHeight,
                    $"Action list height {list.ActualHeight} exceeded page viewport {page.ViewportHeight}.");
                var notices = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ActionNoticesPanel"));
                notices.BringIntoView();
                window.UpdateLayout();
                WpfReviewSelectionTests.Drain();
                var bounds = notices.TransformToAncestor(page).TransformBounds(new Rect(notices.RenderSize));
                var fits = bounds.Height <= page.ViewportHeight;
                Assert.True(bounds.Top >= -0.5 && (fits ? bounds.Bottom <= page.ViewportHeight + 0.5 : bounds.Top <= 0.5),
                    $"Action notices bounds {bounds} were outside the page viewport height {page.ViewportHeight}.");
            }
            finally { window.Close(); WpfReviewSelectionTests.Drain(); }
        });

    [Fact]
    public Task ActionLiveAnnouncementsArePoliteCoalescedAndExposeTheLatestImmutableProgress() =>
        WpfScenarioIsolation.RunAsync(typeof(WpfActionAccessibilityTests), "action-live",
            nameof(ActionLiveAnnouncementsArePoliteCoalescedAndExposeTheLatestImmutableProgress), stage =>
        {
            WpfReviewSelectionTests.RunOnSta(() =>
            {
                var fixture = new ActionShellFixture();
                fixture.Publish(ActionShellFixture.Snapshot(new ActionItemSnapshot(ActionShellFixture.Item(1), null, ActionItemState.Waiting, ActionSafetyReason.None)));
                var window = new MainWindow { DataContext = fixture.ViewModel };
                ExceptionDispatchInfo? clientFailure = null;
                using var subscribed = new ManualResetEventSlim();
                using var delivered = new ManualResetEventSlim();
                using var stop = new ManualResetEventSlim();
                using var settled = new ManualResetEventSlim();
                var deliveries = new System.Collections.Concurrent.ConcurrentQueue<NativeActionDelivery>();
                var callbackGate = new object();
                var acceptCallbacks = true;
                Thread? client = null;
                int[]? expectedRuntimeId = null;
                TextBlock? status = null;
                var capturing = false;
                try
                {
                    window.Show();
                    WpfReviewSelectionTests.Drain();
                    status = Assert.IsAssignableFrom<TextBlock>(window.FindName("ActionAnnouncementText"));
                    Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
                    InvokeDiagnostic(status, "EnableDiagnosticCapture");
                    capturing = true;
                    var handle = new WindowInteropHelper(window).Handle;
                    client = new Thread(() =>
                    {
                        AutomationElement? element = null;
                        AutomationEventHandler handler = (sender, args) =>
                        {
                            lock (callbackGate)
                            {
                                if (!acceptCallbacks) return;
                                try
                                {
                                    var source = Assert.IsType<AutomationElement>(sender);
                                    var runtimeId = Assert.IsType<int[]>(source.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty));
                                    deliveries.Enqueue(new(runtimeId, args.EventId.Id, source.Current.ItemStatus));
                                    delivered.Set();
                                }
                                catch (Exception error) { clientFailure = ExceptionDispatchInfo.Capture(error); delivered.Set(); }
                            }
                        };
                        try
                        {
                            element = AutomationElement.FromHandle(handle).FindFirst(TreeScope.Descendants,
                                new PropertyCondition(AutomationElement.AutomationIdProperty, "action-announcement"));
                            Assert.NotNull(element);
                            expectedRuntimeId = element.GetRuntimeId();
                            var cache = new CacheRequest { TreeScope = TreeScope.Element };
                            cache.Add(AutomationElement.RuntimeIdProperty);
                            using (cache.Activate())
                                Automation.AddAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent, element, TreeScope.Element, handler);
                            subscribed.Set();
                            Assert.True(stop.Wait(TimeSpan.FromSeconds(8)));
                        }
                        catch (Exception error) { clientFailure = ExceptionDispatchInfo.Capture(error); subscribed.Set(); }
                        finally
                        {
                            // Close admission only after any accepted callback leaves the gate.
                            // Late callbacks cannot touch the disposable synchronization signals.
                            lock (callbackGate) acceptCallbacks = false;
                            try { if (element is not null) Automation.RemoveAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent, element, handler); }
                            catch (Exception error) { clientFailure ??= ExceptionDispatchInfo.Capture(error); }
                            settled.Set();
                        }
                    }) { IsBackground = false };
                    client.SetApartmentState(ApartmentState.MTA);
                    client.Start();
                    PumpUntil(() => subscribed.IsSet);
                    clientFailure?.Throw();
                    var requested = ActionShellFixture.Snapshot(new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(), ActionItemState.Requested, ActionSafetyReason.None));
                    fixture.Publish(requested);
                    fixture.Publish(requested with { Items = requested.Items.SetItem(0, requested.Items[0] with { State = ActionItemState.Executing }), DurableExecutingCount = 1 });
                    PumpUntil(() => delivered.IsSet);
                    clientFailure?.Throw();
                    // The accepted Review UIA harness observes a bounded native
                    // delivery tail: UIA may duplicate an identical transport event.
                    var tail = Stopwatch.GetTimestamp();
                    PumpUntil(() => Stopwatch.GetElapsedTime(tail) >= TimeSpan.FromMilliseconds(250));
                    stop.Set();
                    PumpUntil(() => settled.IsSet);
                    Assert.True(client.Join(TimeSpan.FromSeconds(2)), "The isolated Action UIA client did not join.");
                    clientFailure?.Throw();
                    var capture = InvokeDiagnostic(status, "StopDiagnosticCapture");
                    capturing = false;
                    Assert.NotNull(capture);
                    Assert.False(ReadDiagnostic<bool>(capture, "CapacityOverflow"));
                    Assert.False(ReadDiagnostic<bool>(capture, "WrongThread"));
                    var records = ReadDiagnostic<System.Collections.IEnumerable>(capture, "Records").Cast<object>().ToArray();
                    var raises = records.Where(record => ReadDiagnostic<object>(record, "Stage").ToString() == "LiveRaiseBegin").ToArray();
                    var returned = records.Count(record => ReadDiagnostic<object>(record, "Stage").ToString() == "LiveRaiseReturn");
                    var expectedPayload = ActionShellFixture.Read<string>(fixture.ViewModel, "ActionAnnouncement");
                    var diagnostic = $"Production live raises={raises.Length}; returns={returned}; raw native deliveries={deliveries.Count}."
                        + Environment.NewLine + string.Join(Environment.NewLine, deliveries.Select(delivery =>
                            $"runtime=[{string.Join(",", delivery.RuntimeId)}] event={delivery.EventId} payload={delivery.Payload}"));
                    // Do not normalize production announcements: two peer raises
                    // fail even if native transport repeats the same payload.
                    Assert.True(raises.Length == 1 && returned == 1, diagnostic);
                    Assert.Contains("executing", expectedPayload, StringComparison.OrdinalIgnoreCase);
                    Assert.NotNull(expectedRuntimeId);
                    Assert.NotEmpty(deliveries);
                    Assert.All(deliveries, delivery =>
                    {
                        Assert.Equal(expectedRuntimeId, delivery.RuntimeId);
                        Assert.Equal(AutomationElementIdentifiers.LiveRegionChangedEvent.Id, delivery.EventId);
                        Assert.Equal(expectedPayload, delivery.Payload);
                    });
                    var distinctDeliveries = deliveries.Select(delivery =>
                        (RuntimeId: string.Join(",", delivery.RuntimeId), delivery.EventId, delivery.Payload)).Distinct().ToArray();
                    Assert.Single(distinctDeliveries);
                }
                finally
                {
                    stop.Set();
                    if (client is not null)
                    {
                        PumpUntil(() => settled.IsSet);
                        Assert.True(client.Join(TimeSpan.FromSeconds(2)), "The isolated Action UIA client did not join.");
                    }
                    if (capturing && status is not null) InvokeDiagnostic(status, "StopDiagnosticCapture");
                    window.Close();
                    WpfReviewSelectionTests.Drain();
                }
                clientFailure?.Throw();
                stage.CleanupSucceeded();
            });
        });

    private sealed record NativeActionDelivery(int[] RuntimeId, int EventId, string Payload);

    private static object? InvokeDiagnostic(object target, string method)
    {
        var operation = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(operation);
        return operation.Invoke(target, null);
    }

    private static T ReadDiagnostic<T>(object target, string property)
    {
        var value = target.GetType().GetProperty(property);
        Assert.NotNull(value);
        return Assert.IsAssignableFrom<T>(value.GetValue(target));
    }

    internal static void PumpUntil(Func<bool> condition)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition() && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(4))
            WpfReviewSelectionTests.Drain();
        Assert.True(condition(), "The isolated Action UI scenario did not settle within four seconds.");
    }
}
