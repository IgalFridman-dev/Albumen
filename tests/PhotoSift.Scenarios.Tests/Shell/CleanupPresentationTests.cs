using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using PhotoSift.App;
using PhotoSift.App.Shell;
using PhotoSift.Application.Action;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

// The Clean up and History screens put plain words over the recorded detail; the
// recorded detail itself stays available unchanged.
public sealed class CleanupPresentationTests
{
    [Fact]
    public void ApprovalAsksAboutTheMarkedPhotosByNameAndCountsTheRest()
    {
        var review = WithSelected(ReviewData.CreateLargeMemberSnapshot(10), 8);
        var scope = new ReviewStaticScope();
        var viewModel = new ShellViewModel(scope, new RecordingReviewWorkflow(scope.Current, review));

        Assert.Equal("Move 8 photos to the Recycle Bin?", viewModel.ApprovalTitle);
        Assert.Equal(
            Enumerable.Range(1, 6).Select(index => $@"C:\Photos\large-{index}.jpg"),
            viewModel.ApprovalMarkedPhotos.Select(photo => photo.DiscoveredPath));
        Assert.Equal("and 2 more photos", viewModel.ApprovalMoreMarkedText);
        Assert.True(viewModel.HasMoreMarkedPhotos);
        Assert.Equal("8 selected occurrences", viewModel.ApprovalSelectedOccurrenceText);
    }

    [Fact]
    public void ApprovalWithNothingMarkedSaysSoAndListsNoPhotos()
    {
        var review = ReviewData.CreateLargeMemberSnapshot(3);
        var scope = new ReviewStaticScope();
        var viewModel = new ShellViewModel(scope, new RecordingReviewWorkflow(scope.Current, review));

        Assert.Equal("No photos marked for removal yet", viewModel.ApprovalTitle);
        Assert.Empty(viewModel.ApprovalMarkedPhotos);
        Assert.False(viewModel.HasMoreMarkedPhotos);
        Assert.False(viewModel.CanApproveReview);
    }

    [Theory]
    [InlineData(ActionItemState.Waiting, "Waiting", ActionResultTone.Waiting, false)]
    [InlineData(ActionItemState.Requested, "Getting ready", ActionResultTone.Busy, false)]
    [InlineData(ActionItemState.Executing, "Moving…", ActionResultTone.Busy, false)]
    [InlineData(ActionItemState.Recycled, "In the Recycle Bin", ActionResultTone.Done, false)]
    [InlineData(ActionItemState.NotRemoved, "Skipped", ActionResultTone.Skipped, true)]
    [InlineData(ActionItemState.NotRequested, "Not moved", ActionResultTone.Skipped, true)]
    [InlineData(ActionItemState.InterruptedBeforeExecution, "Stopped before moving", ActionResultTone.Warning, true)]
    [InlineData(ActionItemState.UnknownPendingRevalidation, "Outcome unknown", ActionResultTone.Warning, true)]
    public void EachCleanupStateHasAPlainLabelOverItsRecordedDetail(
        ActionItemState state, string label, ActionResultTone tone, bool explains)
    {
        var row = new ActionItemViewModel(new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(),
            state, ActionSafetyReason.None), 1, 1);

        Assert.Equal(label, row.ResultLabel);
        Assert.Equal(tone, row.Tone);
        Assert.Equal(explains, row.HasDetail);
        Assert.EndsWith("-תצלום-📷-1.jpg", row.FileName, StringComparison.Ordinal);
        Assert.Contains(row.StateText, row.ItemStatus, StringComparison.Ordinal);
        Assert.Contains(row.CertaintyText, row.ItemStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void ASkippedPhotoSaysWhyInPlainWords()
    {
        var row = new ActionItemViewModel(new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(),
            ActionItemState.NotRemoved, ActionSafetyReason.Changed), 1, 1);

        Assert.Equal("The file changed since approval. The photo was left where it is.", row.DetailText);
    }

    [Fact]
    public void ProgressCountsSettledPhotosAndTheFinishedCleanupSaysWhatHappened()
    {
        var fixture = new ActionShellFixture();
        var running = ActionShellFixture.Snapshot(
            Item(1, ActionItemState.Recycled),
            Item(2, ActionItemState.Executing),
            Item(3, ActionItemState.Waiting));
        fixture.Publish(running);

        Assert.Equal("Moving 3 photos to the Recycle Bin", fixture.ViewModel.ActionHeadline);
        Assert.Equal(3, fixture.ViewModel.ActionTotalCount);
        Assert.Equal(1, fixture.ViewModel.ActionDoneCount);
        Assert.Equal("1 of 3 done · 1 moved · 0 skipped", fixture.ViewModel.ActionProgressText);
        Assert.False(fixture.ViewModel.HasActionOutcome);

        fixture.Publish(running with
        {
            Status = ActionStatus.Incomplete,
            Items = [Item(1, ActionItemState.Recycled), Item(2, ActionItemState.Recycled),
                Item(3, ActionItemState.NotRemoved, ActionSafetyReason.Missing)],
        });

        Assert.Equal("Cleanup finished: 2 of 3 moved", fixture.ViewModel.ActionHeadline);
        Assert.Equal("3 of 3 done · 2 moved · 1 skipped", fixture.ViewModel.ActionProgressText);
        Assert.Equal("2 photos moved to the Windows Recycle Bin and 1 photo left where it is. Each photo below says why.",
            fixture.ViewModel.ActionOutcomeText);
        Assert.Equal(ActionResultTone.Skipped, fixture.ViewModel.ActionOutcomeTone);

        fixture.Publish(running with
        {
            Status = ActionStatus.Incomplete,
            Items = [Item(1, ActionItemState.Recycled), Item(2, ActionItemState.UnknownPendingRevalidation),
                Item(3, ActionItemState.NotRequested)],
        });

        Assert.Contains("can't tell whether 1 photo moved", fixture.ViewModel.ActionOutcomeText, StringComparison.Ordinal);
        Assert.Contains("Nothing is retried automatically", fixture.ViewModel.ActionOutcomeText, StringComparison.Ordinal);
        Assert.Equal(ActionResultTone.Warning, fixture.ViewModel.ActionOutcomeTone);
    }

    [Fact]
    public void ACleanupWhereEveryPhotoMovedFinishesWithoutCaveats()
    {
        var fixture = new ActionShellFixture();
        fixture.Publish(ActionShellFixture.Snapshot(Item(1, ActionItemState.Recycled)) with { Status = ActionStatus.Complete });

        Assert.Equal("Cleanup finished", fixture.ViewModel.ActionHeadline);
        Assert.Equal("1 photo is in the Windows Recycle Bin. If you need one back, restore it from there.",
            fixture.ViewModel.ActionOutcomeText);
        Assert.Equal(ActionResultTone.Done, fixture.ViewModel.ActionOutcomeTone);
        Assert.DoesNotContain("deleted", fixture.ViewModel.ActionOutcomeText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASavedCleanupSummarizesItsPhotosInPlainWords()
    {
        var fixture = new ActionShellFixture();
        var record = E1S6ActionReconciliationUiTests.Record(
            E1S6ActionReconciliationUiTests.Row(1, ActionRecordEvidenceState.Recycled, allocation: 8192),
            E1S6ActionReconciliationUiTests.Row(2, ActionRecordEvidenceState.Unknown),
            E1S6ActionReconciliationUiTests.Row(3, ActionRecordEvidenceState.NotRemoved));

        fixture.PublishRecovery(new(ActionRecoveryStatus.Ready, [record], null, false));

        var projected = Assert.Single(fixture.ViewModel.ActionRecoveryRecords);
        Assert.Equal("Needs a check", projected.OutcomeLabel);
        Assert.Equal(ActionResultTone.Warning, projected.Tone);
        Assert.Equal("3 photos · 1 moved · 1 skipped · 1 unknown · about 8 KB sent to the Recycle Bin", projected.SummaryText);
        Assert.Equal([ActionResultTone.Done, ActionResultTone.Warning, ActionResultTone.Skipped],
            projected.Items.Select(item => item.Tone));
        Assert.Contains("can't tell whether this photo moved", projected.Items[1].DetailText, StringComparison.Ordinal);
        Assert.False(projected.Items[0].HasDetail);
        Assert.False(fixture.ViewModel.HasNoActionRecords);
    }

    [Fact]
    public void HistoryIsEmptyOnlyOnceSavedCleanupsHaveLoaded()
    {
        var fixture = new ActionShellFixture();
        fixture.PublishRecovery(new(ActionRecoveryStatus.Loading, [], null, false));
        Assert.False(fixture.ViewModel.HasNoActionRecords);

        fixture.PublishRecovery(new(ActionRecoveryStatus.Ready, [], null, false));
        Assert.True(fixture.ViewModel.HasNoActionRecords);
    }

    private static ActionItemSnapshot Item(long id, ActionItemState state,
        ActionSafetyReason reason = ActionSafetyReason.None) =>
        new(ActionShellFixture.Item(id), Guid.NewGuid(), state, reason);

    private static ReviewSnapshot WithSelected(
        ReviewSnapshot review, int count)
    {
        var group = review.Groups[0];
        var members = group.Members.Select((member, index) => member with { Selected = index < count })
            .ToImmutableArray();
        return review with
        {
            Groups = [group with
            {
                Members = members,
                SelectedOccurrenceIds = members.Where(member => member.Selected)
                    .Select(member => member.Occurrence.Token.OccurrenceId).ToImmutableHashSet(),
            }],
        };
    }
}

[Collection("WPF UI Automation")]
public sealed class CleanupScreenTests
{
    [Fact]
    public void TheCleanupPageShowsProgressAndTheFinishedSummary() =>
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new ActionShellFixture();
            fixture.Publish(ActionShellFixture.Snapshot(
                new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(), ActionItemState.Recycled, ActionSafetyReason.None),
                new ActionItemSnapshot(ActionShellFixture.Item(2), Guid.NewGuid(), ActionItemState.Waiting, ActionSafetyReason.None)));
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                window.UpdateLayout();
                WpfReviewSelectionTests.Drain();
                var progress = Assert.IsType<ProgressBar>(window.FindName("ActionProgressBar"));
                Assert.True(progress.IsVisible);
                Assert.Equal(2d, progress.Maximum);
                Assert.Equal(1d, progress.Value);
                var heading = Assert.IsType<TextBlock>(window.FindName("ActionHeading"));
                Assert.Equal("Moving 2 photos to the Recycle Bin", heading.Text);
                var banner = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ActionOutcomeBanner"));
                Assert.False(banner.IsVisible);

                fixture.Publish(ActionShellFixture.Snapshot(
                    new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(), ActionItemState.Recycled, ActionSafetyReason.None),
                    new ActionItemSnapshot(ActionShellFixture.Item(2), Guid.NewGuid(), ActionItemState.Recycled, ActionSafetyReason.None))
                    with { Status = ActionStatus.Complete });
                window.UpdateLayout();
                WpfReviewSelectionTests.Drain();

                Assert.Equal(2d, progress.Value);
                Assert.Equal("Cleanup finished", heading.Text);
                Assert.True(banner.IsVisible);
            }
            finally { window.Close(); WpfReviewSelectionTests.Drain(); }
        });

    [Fact]
    public void HistoryListsSavedCleanupsAndHoldsClearScanData() =>
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new ActionShellFixture();
            var record = E1S6ActionReconciliationUiTests.Record(
                E1S6ActionReconciliationUiTests.Row(1, ActionRecordEvidenceState.Recycled));
            fixture.PublishRecovery(new(ActionRecoveryStatus.Ready, [record], null, false), WorkflowPhase.ChooseScope);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                WpfReviewSelectionTests.Drain();
                var history = Assert.IsType<TabItem>(window.FindName("ActionRecordsTab"));
                Assert.Equal("History", history.Header);
                Assert.False(Assert.IsType<Button>(window.FindName("ClearScanDataButton")).IsVisible);

                history.IsSelected = true;
                window.UpdateLayout();
                WpfReviewSelectionTests.Drain();

                var records = Assert.IsType<ItemsControl>(window.FindName("ActionRecordsHistoryList"));
                var texts = WpfReviewSelectionTests.Descendants<TextBlock>(records).Select(text => text.Text).ToArray();
                Assert.Contains("All moved", texts);
                Assert.Contains("moved to Recycle Bin", texts);
                Assert.Contains(texts, text => text.StartsWith("Cleanup on ", StringComparison.Ordinal));
            }
            finally { window.Close(); WpfReviewSelectionTests.Drain(); }
        });
}
