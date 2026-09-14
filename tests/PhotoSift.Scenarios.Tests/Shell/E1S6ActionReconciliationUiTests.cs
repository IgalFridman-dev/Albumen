using System.Collections;
using System.Collections.Immutable;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoSift.App;
using PhotoSift.App.Shell;
using PhotoSift.Application.Action;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

public sealed class E1S6ActionReconciliationUiTests
{
    private const string Disclosure = "PhotoSift found an unfinished cleanup from a previous session. The outcome of one or more items is unknown. Revalidate their current file state. Revalidation will not remove files.";
    private static readonly string[] ExpectedResults = ["moved to Recycle Bin", "outcome unknown pending revalidation", "not removed"];
    private static readonly DateTimeOffset RequestedUtc = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnfinishedRecordShowsExactDisclosureAllRowsAndIndependentSummary()
    {
        var fixture = new ActionShellFixture();
        var record = Record(
            Row(1, ActionRecordEvidenceState.Recycled, allocation: 8192),
            Row(2, ActionRecordEvidenceState.Unknown, allocation: 4096),
            Row(3, ActionRecordEvidenceState.NotRemoved, allocation: 2048));

        fixture.PublishRecovery(new(ActionRecoveryStatus.Ready, [record], null, false));

        Assert.True(ActionShellFixture.Read<bool>(fixture.ViewModel, "HasActionRecoveryDisclosure"));
        Assert.Equal(Disclosure, ActionShellFixture.Read<string>(fixture.ViewModel, "ActionRecoveryDisclosure"));
        var records = ActionShellFixture.Read<IEnumerable>(fixture.ViewModel, "ActionRecoveryRecords").Cast<object>().ToArray();
        var projected = Assert.Single(records);
        Assert.Equal("Has unknown", ActionShellFixture.Read<string>(projected, "CertaintyText"));
        Assert.Equal("Partial", ActionShellFixture.Read<string>(projected, "OutcomeMixText"));
        Assert.Equal("Recovered bytes: 8192", ActionShellFixture.Read<string>(projected, "RecoveredBytesText"));
        Assert.Equal(1, ActionShellFixture.Read<int>(projected, "UnknownCount"));
        var rows = ActionShellFixture.Read<IEnumerable>(projected, "Items").Cast<object>().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(ExpectedResults,
            rows.Select(row => ActionShellFixture.Read<string>(row, "ResultText")));
    }

    [Fact]
    public async Task ExplicitCommandTargetsRecordAndIsGatedByOutstandingProbe()
    {
        var fixture = new ActionShellFixture();
        var record = Record(Row(1, ActionRecordEvidenceState.Unknown));
        fixture.PublishRecovery(new(ActionRecoveryStatus.Ready, [record], null, false));
        var command = ActionShellFixture.Read<AsyncCommand>(fixture.ViewModel, "RevalidateUnknownCommand");
        Assert.True(command.CanExecute(record.RequestId));

        await command.ExecuteAsync(record.RequestId);

        Assert.Equal([record.RequestId], fixture.Revalidations);
        fixture.PublishRecovery(new(ActionRecoveryStatus.Ready, [record], record.Items[0].Requested.ActionItemId, true));
        Assert.False(command.CanExecute(record.RequestId));
        Assert.Equal("Verifying current file state…", ActiveRow(fixture).StatusText);
        Assert.True(ActiveRow(fixture).IsVerifying);
    }

    [Fact]
    public void RecoveryPublicationRetainsUnchangedRowsAndAnnouncesOnlyChangedState()
    {
        var fixture = new ActionShellFixture();
        var record = Record(Row(1, ActionRecordEvidenceState.Unknown), Row(2, ActionRecordEvidenceState.Recycled));
        fixture.PublishRecovery(new(ActionRecoveryStatus.Ready, [record], null, false));
        var before = RecoveryRows(fixture);

        fixture.PublishRecovery(new(ActionRecoveryStatus.Ready, [record], record.Items[0].Requested.ActionItemId, true));

        var during = RecoveryRows(fixture);
        Assert.Same(before[0], during[0]);
        Assert.Same(before[1], during[1]);
        Assert.Contains("Verifying current file state…", ActionShellFixture.Read<string>(fixture.ViewModel, "ActionRecoveryAnnouncement"));

        var resolved = Record(Row(1, ActionRecordEvidenceState.ConfirmedPresent), record.Items[1]);
        fixture.PublishRecovery(new(ActionRecoveryStatus.Ready, [resolved], null, false));

        var after = RecoveryRows(fixture);
        Assert.Same(before[0], after[0]);
        Assert.Equal("not removed", after[0].ResultText);
        Assert.False(after[0].IsVerifying);
        Assert.Equal(0, ActionShellFixture.Read<int>(
            Assert.Single(ActionShellFixture.Read<IEnumerable>(fixture.ViewModel, "ActionRecoveryRecords").Cast<object>()),
            "UnknownCount"));
    }

    private static dynamic ActiveRow(ActionShellFixture fixture) => RecoveryRows(fixture)[0];

    private static dynamic[] RecoveryRows(ActionShellFixture fixture)
    {
        var record = Assert.Single(ActionShellFixture.Read<IEnumerable>(fixture.ViewModel, "ActionRecoveryRecords").Cast<object>());
        return ActionShellFixture.Read<IEnumerable>(record, "Items").Cast<dynamic>().ToArray();
    }

    internal static ActionRecordSnapshot Record(params ActionRecordItemSnapshot[] rows)
    {
        var requestId = Guid.Parse("86000000-0000-0000-0000-000000000006");
        var evidence = rows.Select(row => new ActionRecordEvidence(row.Requested.Safety.Item.PhysicalFileKey,
            row.Requested.Safety.Item.Allocation.AllocatedBytes, row.State)).ToImmutableArray();
        return new(requestId, RequestedUtc, [.. rows], ActionRecordPolicy.Derive(evidence));
    }

    internal static ActionRecordItemSnapshot Row(long occurrenceId, ActionRecordEvidenceState state, long allocation = 4096)
    {
        var source = ActionShellFixture.Item(occurrenceId);
        var key = new PhysicalFileKey((ulong)occurrenceId, new((ulong)occurrenceId, (ulong)occurrenceId));
        var item = source with
        {
            PhysicalFileKey = key,
            Content = new ContentRevision(key, source.Content.ByteLength, source.Content.Sha256),
            Allocation = new(allocation, 1, true),
        };
        var envelope = new ActionSafetyEnvelope(null!, item, null!, null!, [], []);
        var requested = new ActionRequestedFact(Guid.NewGuid(), RequestedUtc, envelope, new string('A', 64), null);
        return new(requested, state, ActionSafetyReason.None, null);
    }
}

[Collection("WPF UI Automation")]
public sealed class E1S6ActionReconciliationAccessibilityTests
{
    [Fact]
    public Task ActiveProbeIsIndeterminateAnnouncedAndDoesNotStealInspectionFocus() =>
        WpfScenarioIsolation.RunAsync(typeof(E1S6ActionReconciliationAccessibilityTests), "e1-s6-revalidation-ui",
            nameof(ActiveProbeIsIndeterminateAnnouncedAndDoesNotStealInspectionFocus), stage =>
        {
            WpfReviewSelectionTests.RunOnSta(() =>
            {
                var fixture = new ActionShellFixture();
                var record = E1S6ActionReconciliationUiTests.Record(
                    E1S6ActionReconciliationUiTests.Row(1, ActionRecordEvidenceState.Recycled),
                    E1S6ActionReconciliationUiTests.Row(2, ActionRecordEvidenceState.Unknown));
                fixture.PublishRecovery(new(ActionRecoveryStatus.Ready, [record], null, false));
                var window = new MainWindow { DataContext = fixture.ViewModel, Width = 1100, Height = 900 };
                try
                {
                    window.Show();
                    window.Activate();
                    WpfReviewSelectionTests.Drain();
                    var activePath = WpfReviewSelectionTests.Descendants<TextBox>(window)
                        .Single(control => control.Text == record.Items[1].Requested.Safety.Item.DiscoveredPath);
                    activePath.Focus();
                    Keyboard.Focus(activePath);
                    activePath.Select(3, 8);
                    var selected = activePath.SelectedText;

                    fixture.PublishRecovery(new(ActionRecoveryStatus.Ready, [record],
                        record.Items[1].Requested.ActionItemId, true));
                    window.UpdateLayout();
                    WpfReviewSelectionTests.Drain();

                    Assert.Same(activePath, Keyboard.FocusedElement);
                    Assert.Equal(selected, activePath.SelectedText);
                    var progress = Assert.Single(WpfReviewSelectionTests.Descendants<ProgressBar>(window),
                        control => control.IsVisible);
                    Assert.True(progress.IsIndeterminate);
                    Assert.Equal("Current file state verification progress", AutomationProperties.GetName(progress));
                    Assert.Equal("Verifying current file state…", AutomationProperties.GetItemStatus(progress));
                    var button = Assert.IsType<Button>(window.FindName("RevalidateUnknownItemsButton"));
                    Assert.False(button.IsEnabled);
                    var announcement = Assert.IsType<ReviewAnnouncementTextBlock>(window.FindName("ActionRecoveryAnnouncementText"));
                    var peer = UIElementAutomationPeer.CreatePeerForElement(announcement);
                    Assert.NotNull(peer);
                    Assert.Contains("Verifying current file state…", peer.GetItemStatus());
                }
                finally { window.Close(); WpfReviewSelectionTests.Drain(); }
                stage.CleanupSucceeded();
            });
        });
}
