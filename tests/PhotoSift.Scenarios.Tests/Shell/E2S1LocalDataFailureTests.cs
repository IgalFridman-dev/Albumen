using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using PhotoSift.App;
using PhotoSift.App.Shell;
using PhotoSift.Application.Action;
using PhotoSift.Application.Review;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class E2S1LocalDataFailureTests
{
    private const string BlockedMessage =
        "Local data could not be safely loaded. PhotoSift will not modify files until the database is available.";

    [Fact]
    public void FaultedLocalDataAtStartupIsVisibleAndAccessibleBeforeLeavingChooseScope()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var scope = new ReviewStaticScope();
            var workflow = new FaultedLocalDataWorkflow(scope.Current, WorkflowPhase.ChooseScope);
            var viewModel = new ShellViewModel(scope, workflow);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                window.Show();
                WpfReviewSelectionTests.Drain();

                Assert.Equal(BlockedMessage, viewModel.ActionRecoveryStatusText);
                Assert.True(viewModel.IsChooseScopePhase);

                var status = Assert.IsType<TextBlock>(window.FindName("LocalDataStatusText"));
                Assert.True(status.IsVisible);
                Assert.Equal(BlockedMessage, status.Text);
                Assert.Equal(BlockedMessage, AutomationProperties.GetItemStatus(status));
                // Narrator reads the live region's name, so the name carries the warning itself.
                Assert.Equal(BlockedMessage, AutomationProperties.GetName(status));
                Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(status));
                Assert.True(viewModel.IsLocalDataFaulted);
                Assert.True(Assert.IsType<Border>(window.FindName("LocalDataStatusBanner")).IsVisible);

                viewModel.ApproveReviewCommand.Execute(null);
                WpfReviewSelectionTests.Drain();
                Assert.Equal(0, workflow.ApprovalCalls);
            }
            finally
            {
                window.Close();
                viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public void LocalDataFailureRaisesOneLiveRegionEventPerFailure()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var scope = new ReviewStaticScope();
            var workflow = new FaultedLocalDataWorkflow(
                scope.Current, WorkflowPhase.ChooseScope, ActionRecoveryStatus.Loading);
            var viewModel = new ShellViewModel(scope, workflow);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                window.Show();
                WpfReviewSelectionTests.Drain();
                var root = AutomationElement.FromHandle(new WindowInteropHelper(window).Handle);
                var element = root.FindFirst(TreeScope.Descendants, new PropertyCondition(
                    AutomationElement.AutomationIdProperty, "local-data-status"));
                Assert.NotNull(element);
                var count = 0;
                AutomationEventHandler handler = (_, _) => Interlocked.Increment(ref count);
                Automation.AddAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent,
                    element, TreeScope.Element, handler);
                try
                {
                    workflow.Publish(ActionRecoveryStatus.Faulted);
                    PumpFor(TimeSpan.FromMilliseconds(300));
                    Assert.Equal(1, Volatile.Read(ref count));

                    workflow.Publish(ActionRecoveryStatus.Faulted);
                    PumpFor(TimeSpan.FromMilliseconds(300));
                    Assert.Equal(1, Volatile.Read(ref count));

                    workflow.Publish(ActionRecoveryStatus.Loading);
                    workflow.Publish(ActionRecoveryStatus.Faulted);
                    PumpFor(TimeSpan.FromMilliseconds(300));
                    Assert.Equal(2, Volatile.Read(ref count));
                }
                finally
                {
                    Automation.RemoveAutomationEventHandler(
                        AutomationElementIdentifiers.LiveRegionChangedEvent, element, handler);
                }
            }
            finally
            {
                window.Close();
                viewModel.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    private static void PumpFor(TimeSpan duration)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (started.Elapsed < duration)
        {
            WpfReviewSelectionTests.Drain();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void FaultedLocalDataBlocksApprovalWithoutHidingReviewEvidence()
    {
        var scope = new ReviewStaticScope();
        var workflow = new FaultedLocalDataWorkflow(scope.Current, WorkflowPhase.Review);
        var viewModel = new ShellViewModel(scope, workflow);

        Assert.Equal(BlockedMessage, viewModel.ActionRecoveryStatusText);
        Assert.False(viewModel.CanApproveReview);
        Assert.NotEmpty(viewModel.ExactGroups);
        Assert.True(viewModel.IsReviewPhase);

        viewModel.ApproveReviewCommand.Execute(null);
        Assert.Equal(0, workflow.ApprovalCalls);
    }

    private sealed class FaultedLocalDataWorkflow : IPhotoSiftWorkflow
    {
        internal FaultedLocalDataWorkflow(
            ShellSnapshot scope,
            WorkflowPhase phase,
            ActionRecoveryStatus status = ActionRecoveryStatus.Faulted)
        {
            var review = ReviewData.CreateSnapshot(selected: [1]);
            var baseline = new RecordingReviewWorkflow(scope, review).Current;
            Current = baseline with
            {
                CurrentPhase = phase,
                ActionRecovery = new(status, [], null, false),
            };
        }

        public PhotoSiftSnapshot Current { get; private set; }

        internal void Publish(ActionRecoveryStatus status)
        {
            Current = Current with { ActionRecovery = new(status, [], null, false) };
            snapshotChanged?.Invoke(this, Current);
        }

        private EventHandler<PhotoSiftSnapshot>? snapshotChanged;
        public int ApprovalCalls { get; private set; }
        public event EventHandler<PhotoSiftSnapshot>? SnapshotChanged
        {
            add => snapshotChanged += value;
            remove => snapshotChanged -= value;
        }
        public Task ContinueAsync() => Task.CompletedTask;
        public Task SetScanOptionsAsync(ScanOptions options) => Task.CompletedTask;
        public Task ConfirmExactRulesAsync() => Task.CompletedTask;
        public Task StartScanAsync() => Task.CompletedTask;
        public Task CancelScanAsync() => Task.CompletedTask;
        public Task OpenReviewAsync() => Task.CompletedTask;
        public Task RevalidateUnknownAsync(Guid requestId, CancellationToken token) => Task.CompletedTask;
        public Task NavigateAsync(WorkflowPhase phase) => Task.CompletedTask;
        public Task ContinueReviewAsync() => Task.CompletedTask;
        public Task<ReviewCommandResult> ChangeReviewSelectionAsync(SelectionCommand command, CancellationToken token) =>
            Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current.Review));
        public Task<ReviewCommandResult> AffirmReviewOverrideAsync(OverrideAffirmationCommand command, CancellationToken token) =>
            Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Neutral, Current.Review));
        public Task<ReviewCommandResult> ApproveReviewAsync(ApproveSelectionCommand command, CancellationToken token)
        {
            ApprovalCalls++;
            return Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Approved, Current.Review));
        }
    }
}
