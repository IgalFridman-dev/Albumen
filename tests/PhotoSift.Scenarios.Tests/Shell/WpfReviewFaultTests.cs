using System.Collections.Immutable;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoSift.App;
using PhotoSift.App.Shell;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfReviewFaultTests
{
    [Theory]
    [InlineData("status")]
    [InlineData("announcement")]
    [InlineData("selection-controls")]
    public void WorkflowObserverFaultAfterEligibilityIsVisibleWithoutAnotherUiCommand(string projection)
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var scheduler = new ManualWorkflowScheduler();
            var scope = new ReviewStaticScope();
            var scan = new CompletedScanOwner();
            var executor = new ControlledEligibilityExecutor();
            var owner = new ReviewCoordinator(scheduler, executor, new UnusedEvidenceStore());
            var workflow = new PhotoSiftWorkflow(scope, scan, scheduler, owner);
            var viewModel = new ShellViewModel(scope, workflow);
            var window = new MainWindow { DataContext = viewModel };
            var faultPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var injected = false;
            var readySelectionEnabled = false;
            var announcements = new List<string>();
            void ObserveWorkflow(object? sender, PhotoSiftSnapshot snapshot)
            {
                // Throw at the real asynchronous projection boundary only after
                // the owner accepted both members' protected evidence.
                if (!injected && snapshot.Review.Status == ReviewStatus.Ready)
                {
                    readySelectionEnabled = viewModel.ExactGroups[0].Members.All(member => member.CanSelect)
                        && viewModel.ExactGroups[0].SelectAllCommand.CanExecute(null);
                    injected = true;
                    throw new InvalidOperationException("Injected current-session workflow observer fault.");
                }
                if (snapshot.Review.Status == ReviewStatus.Faulted)
                    faultPublished.TrySetResult();
            }

            try
            {
                Finish(workflow.ContinueAsync(), scheduler);
                Finish(workflow.ConfirmExactRulesAsync(), scheduler);
                scan.Publish(CompletedScan());
                scheduler.Drain();
                Finish(workflow.OpenReviewAsync(), scheduler);
                Finish(executor.Started.Task, scheduler);
                var run = executor.Started.Task.GetAwaiter().GetResult();
                var first = run.AcceptAsync(1);
                Finish(first, scheduler);
                Assert.True(first.GetAwaiter().GetResult());
                Assert.False(viewModel.ExactGroups[0].Members[0].CanSelect);
                Assert.Equal(ReviewStatus.Revalidating, owner.Current.Status);

                window.Show();
                WpfReviewSelectionTests.Drain();
                viewModel.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(ShellViewModel.ReviewAnnouncement))
                        announcements.Add(viewModel.ReviewAnnouncement);
                };
                workflow.SnapshotChanged += ObserveWorkflow;
                var second = run.AcceptAsync(2);
                Finish(second, scheduler);
                Assert.True(second.GetAwaiter().GetResult());
                Finish(faultPublished.Task, scheduler);

                // The explicit fault-publication signal, followed by the existing
                // bounded scheduler/Dispatcher drain, is the ordering barrier.
                // No selection, navigation, approval, or other UI command follows.
                Assert.True(injected);
                Assert.True(readySelectionEnabled);
                Assert.Equal(ReviewStatus.Faulted, owner.Current.Status);
                Assert.Equal(ReviewUnavailableReason.ProgrammingFault, owner.Current.FaultReason);
                Assert.Same(owner.Current, workflow.Current.Review);
                Assert.All(owner.Current.Groups[0].Members,
                    member => Assert.Equal(ReviewEligibilityState.Eligible, member.Eligibility));
                Assert.Equal(ScanStatus.Complete, workflow.Current.Scan.Status);
                Assert.Null(owner.Current.ApprovedRequest);

                switch (projection)
                {
                    case "status":
                        var status = Assert.IsType<TextBlock>(window.FindName("ReviewStatusText"));
                        Assert.True(status.IsVisible);
                        Assert.Contains("fault", status.Text, StringComparison.OrdinalIgnoreCase);
                        Assert.Contains("ProgrammingFault", status.Text, StringComparison.Ordinal);
                        Assert.Contains("ProgrammingFault", AutomationProperties.GetItemStatus(status), StringComparison.Ordinal);
                        break;
                    case "announcement":
                        Assert.Contains("fault", viewModel.ReviewAnnouncement, StringComparison.OrdinalIgnoreCase);
                        Assert.Contains("ProgrammingFault", viewModel.ReviewAnnouncement, StringComparison.Ordinal);
                        Assert.Single(announcements);
                        var announcement = Assert.IsType<ReviewAnnouncementTextBlock>(
                            window.FindName("ReviewAnnouncementText"));
                        Assert.True(announcement.IsVisible);
                        Assert.Equal(viewModel.ReviewAnnouncement, announcement.Text);
                        var peer = UIElementAutomationPeer.CreatePeerForElement(announcement);
                        Assert.NotNull(peer);
                        // The peer's status changes only when MainWindow dispatches
                        // the product announcement, not from the Text binding alone.
                        Assert.Equal(viewModel.ReviewAnnouncement, peer.GetItemStatus());
                        break;
                    case "selection-controls":
                        WpfReviewSelectionTests.OpenFirstGroupedComparison(viewModel);
                        window.UpdateLayout();
                        WpfReviewSelectionTests.Drain();
                        var group = Assert.Single(viewModel.ExactGroups);
                        Assert.All(group.Members, member => Assert.False(member.CanSelect));
                        Assert.False(group.SelectAllCommand.CanExecute(null));
                        Assert.False(group.ReplaceSelectionCommand.CanExecute(new long[] { 1 }));
                        Assert.All(group.Members, member =>
                            Assert.False(Assert.IsType<AsyncCommand>(member.ToggleSelectionCommand).CanExecute(null)));
                        var controls = WpfReviewSelectionTests.Descendants<ReviewSelectionCheckBox>(window).ToArray();
                        Assert.NotEmpty(controls);
                        Assert.All(controls, control => Assert.False(control.IsEnabled));
                        var members = Assert.IsType<ListBox>(window.FindName("ReviewComparisonsList"));
                        Assert.False(ApplicationCommands.SelectAll.CanExecute(null, members));
                        Assert.False(viewModel.CanApproveReview);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(projection));
                }
            }
            finally
            {
                workflow.SnapshotChanged -= ObserveWorkflow;
                try { window.Close(); }
                finally
                {
                    try { Finish(viewModel.DisposeAsync().AsTask(), scheduler); }
                    finally { Finish(owner.DisposeAsync().AsTask(), scheduler); }
                }
            }
        });
    }

    private static void Finish(Task task, ManualWorkflowScheduler scheduler) =>
        WpfReviewSelectionTests.FinishOnSta(task, scheduler);

    private static ScanSnapshot CompletedScan()
    {
        var pin = new ScanPin(Guid.Parse("81000000-0000-0000-0000-000000000001"), 7, 1, 1);
        var members = Enumerable.Range(1, 2).Select(id =>
        {
            var key = new PhysicalFileKey(7, new(0, (ulong)id));
            var path = $@"C:\Photos\{id}.jpg";
            return new ScanFileOccurrence(new(pin, id, 1, 1), path, path, key,
                new(ScanStageState.Succeeded, ScanReason.None), new(ScanStageState.Succeeded, ScanReason.None),
                new(key, 17, new string('A', 64)), new(512, 1, true));
        }).ToImmutableArray();
        var group = new CandidateGroup(Guid.Parse("82000000-0000-0000-0000-000000000002"), 1, pin,
            CandidateCategory.Exact, members,
            [new(members[0].Token, members[1].Token, members[0].Content!, members[1].Content!)],
            false, null, new(512, RecoveryUnavailableReason.None));
        return new(pin, ScanStatus.Complete, new(ScanStageState.Succeeded, ScanReason.None),
            members, [group], new(2, 2, 0, 0, 0, 0));
    }

    private sealed class ControlledEligibilityExecutor : IReviewRevalidationExecutor
    {
        internal TaskCompletionSource<ControlledEligibilityRun> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReviewRun Start(ReviewSessionPin session, ScanRunContext context,
            ImmutableArray<CandidateGroup> groups, IReviewResultSink sink)
        {
            var run = new ControlledEligibilityRun(session, context, groups.Single(), sink);
            Started.TrySetResult(run);
            return run;
        }
    }

    private sealed class ControlledEligibilityRun(ReviewSessionPin session, ScanRunContext context,
        CandidateGroup group, IReviewResultSink sink) : IReviewRun
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion => completion.Task;
        internal Task<bool> AcceptAsync(long occurrenceId)
        {
            var member = group.Members.Single(row => row.Token.OccurrenceId == occurrenceId);
            var request = new ReviewRevalidationRequest(session, context, group.GroupId, group.GroupRevision, member, 1);
            var facts = new ProtectedFileFacts(member.PhysicalFileKey!.Value, member.Content!.ByteLength,
                member.ResolvedPath, null, member.Allocation);
            return sink.CommitRevalidationAsync(new(request, facts, facts,
                new(member.Content.ByteLength, member.Content.Sha256))).AsTask();
        }
        public Task CancelAsync() { completion.TrySetResult(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnusedEvidenceStore : IOverrideEvidenceStore
    {
        public ValueTask<OverridePersistResult> PersistAsync(DeliberateOverrideEvidence evidence,
            CancellationToken cancellationToken) => throw new InvalidOperationException("The fault scenario must not persist evidence.");
        public ValueTask<OverrideReadResult> ReadAsync(OverrideEvidenceId evidenceId,
            CancellationToken cancellationToken) => throw new InvalidOperationException("The fault scenario must not read approval authority.");
    }
}
