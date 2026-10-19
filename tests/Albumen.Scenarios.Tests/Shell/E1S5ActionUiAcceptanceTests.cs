using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using Albumen.App;
using Albumen.Application.Action;
using Albumen.Domain.Action;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class E1S5ActionUiAcceptanceTests
{
    [Fact]
    public Task InspectingAnOriginalPathKeepsKeyboardFocusAndSelectionWhileAnotherItemProgresses() =>
        WpfScenarioIsolation.RunAsync(typeof(E1S5ActionUiAcceptanceTests), "action-inspect-progress",
            nameof(InspectingAnOriginalPathKeepsKeyboardFocusAndSelectionWhileAnotherItemProgresses), stage =>
        {
            WpfReviewSelectionTests.RunOnSta(() =>
            {
                var fixture = new ActionShellFixture();
                var window = new MainWindow { DataContext = fixture.ViewModel, Width = 1100, Height = 900 };
                try
                {
                    window.Show();
                    window.Activate();
                    WpfReviewSelectionTests.Drain();
                    var approve = Assert.IsType<Button>(window.FindName("ApproveCleanupButton"));
                    Assert.False(approve.IsDefault);
                    approve.Focus();
                    Keyboard.Focus(approve);
                    WpfReviewSelectionTests.Press(approve, Key.Enter);
                    WpfReviewSelectionTests.Drain();
                    var approved = Assert.Single(fixture.Approvals);
                    Assert.Equal(fixture.Current.Review.Pin, approved.Session);
                    Assert.Equal(fixture.Current.Review.SelectionRevision, approved.SelectionRevision);

                    var first = new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(), ActionItemState.Recycled, ActionSafetyReason.None);
                    var second = new ActionItemSnapshot(ActionShellFixture.Item(2), Guid.NewGuid(), ActionItemState.Requested, ActionSafetyReason.None);
                    var progress = ActionShellFixture.Snapshot(first, second) with
                        { DurableRequestedCount = 2, DurableExecutingCount = 1, RecycledCount = 1 };
                    fixture.Publish(progress);
                    WpfReviewSelectionTests.Drain();
                    var list = Assert.IsType<ListBox>(window.FindName("ActionItemsList"));
                    list.ScrollIntoView(list.Items[0]);
                    window.UpdateLayout();
                    WpfReviewSelectionTests.Drain();
                    var original = WpfReviewSelectionTests.Descendants<TextBox>(list)
                        .Single(path => path.Text == first.Item.DiscoveredPath);
                    Assert.True(original.IsReadOnly);
                    original.Focus();
                    Keyboard.Focus(original);
                    original.Select(3, 12);
                    var inspectedText = original.SelectedText;
                    Assert.Equal(12, inspectedText.Length);
                    Assert.True(original.IsKeyboardFocused);

                    fixture.Publish(progress with
                    {
                        Items = progress.Items.SetItem(1, second with { State = ActionItemState.Executing }),
                        DurableExecutingCount = 2,
                    });
                    window.UpdateLayout();
                    WpfReviewSelectionTests.Drain();
                    // Inspection is already in progress: a background row update
                    // must not move focus or discard its selected path substring.
                    var focused = Assert.IsType<TextBox>(Keyboard.FocusedElement);
                    Assert.Equal(first.Item.DiscoveredPath, focused.Text);
                    Assert.Equal(inspectedText, focused.SelectedText);
                    Assert.True(focused.IsReadOnly);
                    Assert.True(focused.IsEnabled);
                    Assert.True(focused.IsVisible);
                    Assert.True(focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));
                    Assert.NotSame(focused, Keyboard.FocusedElement);
                    list.ScrollIntoView(list.Items[1]);
                    window.UpdateLayout();
                    WpfReviewSelectionTests.Drain();
                    var container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(1));
                    var row = WpfReviewSelectionTests.Descendants<FrameworkElement>(container)
                        .Single(element => AutomationProperties.GetAutomationId(element) == "action-item");
                    var peer = UIElementAutomationPeer.CreatePeerForElement(row);
                    Assert.NotNull(peer);
                    Assert.Contains(second.Item.DiscoveredPath, peer.GetName());
                    Assert.Contains("executing", peer.GetItemStatus(), StringComparison.OrdinalIgnoreCase);
                    Assert.Contains("2 of 2", peer.GetName());
                    Assert.Equal(ActionItemState.Requested, second.State);
                    Assert.Equal(ActionItemState.Requested, progress.Items[1].State);
                    Assert.False(approve.IsEnabled);
                    Assert.Single(fixture.Approvals);
                }
                finally { window.Close(); WpfReviewSelectionTests.Drain(); }
                stage.CleanupSucceeded();
            });
        });
}
