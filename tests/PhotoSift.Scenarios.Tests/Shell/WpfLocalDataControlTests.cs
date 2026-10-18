using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PhotoSift.App;
using PhotoSift.App.Shell;
using PhotoSift.Application.LocalData;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfLocalDataControlTests
{
    [Fact]
    public void ConfirmationIsExplicitNonDefaultKeyboardFocusedAndReportsAResultThroughOneLiveStatus()
    {
        RunOnSta(async () =>
        {
            var fixture = new ExactShellFixture();
            var control = new RecordingScanDataControl(new(
                ScanDataClearOutcome.Cleared,
                ClearedScanCount: 2,
                RetainedScanCount: 0,
                Message: "Local Scan data was cleared. Source photos and Action records were unchanged."));
            var viewModel = new ShellViewModel(
                fixture.Scope, fixture.Workflow, localScanDataControl: control);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                window.Show();
                ShowHistory(window);
                var request = Assert.IsType<Button>(window.FindName("ClearScanDataButton"));
                var confirm = Assert.IsType<Button>(window.FindName("ConfirmClearScanDataButton"));
                var consequence = Assert.IsType<TextBlock>(window.FindName("ClearScanDataConsequenceText"));
                var status = Assert.IsType<TextBlock>(window.FindName("LocalDataClearStatusText"));
                Assert.Equal("Clear local Scan data", AutomationProperties.GetName(request));
                Assert.Equal("Confirm clear local Scan data", AutomationProperties.GetName(confirm));
                Assert.False(confirm.IsDefault);
                Assert.False(confirm.IsCancel);
                Assert.False(confirm.IsVisible);
                Assert.Contains("Source photos are unchanged", consequence.Text, StringComparison.Ordinal);
                Assert.Contains("Action records are retained", consequence.Text, StringComparison.Ordinal);
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));

                window.Activate();
                Assert.True(request.Focus());
                Keyboard.Focus(request);
                Assert.True(request.IsKeyboardFocusWithin);
                var requestPeer = UIElementAutomationPeer.CreatePeerForElement(request);
                Assert.NotNull(requestPeer);
                var requestProvider = Assert.IsAssignableFrom<IInvokeProvider>(
                    requestPeer.GetPattern(PatternInterface.Invoke));
                requestProvider.Invoke();
                var waitingSince = Stopwatch.GetTimestamp();
                while (!(viewModel.IsClearScanDataConfirmationPending
                         && confirm.IsVisible
                         && confirm.IsEnabled)
                       && Stopwatch.GetElapsedTime(waitingSince) < TimeSpan.FromSeconds(2))
                {
                    WpfReviewSelectionTests.Drain();
                }

                Assert.True(viewModel.IsClearScanDataConfirmationPending);
                Assert.True(confirm.IsVisible && confirm.IsEnabled && confirm.IsTabStop && confirm.Focusable);
                Assert.True(confirm.IsKeyboardFocusWithin);
                InvokeButton(confirm);
                PumpUntil(() => control.Calls == 1 && status.Text ==
                    "Local Scan data was cleared. Source photos and Action records were unchanged.");

                Assert.Equal(1, control.Calls);
                Assert.False(confirm.IsVisible);
                Assert.Equal(
                    "Local Scan data was cleared. Source photos and Action records were unchanged.",
                    status.Text);
                Assert.Equal(status.Text, AutomationProperties.GetItemStatus(status));
                Assert.True(status.Focusable);
                Assert.True(status.IsKeyboardFocusWithin);
            }
            finally
            {
                window.Close();
                await viewModel.DisposeAsync();
            }
        });
    }

    [Fact]
    public void CancelConfirmationRestoresKeyboardFocusToClearRequest()
    {
        RunOnSta(async () =>
        {
            var fixture = new ExactShellFixture();
            var viewModel = new ShellViewModel(
                fixture.Scope, fixture.Workflow, localScanDataControl: new RecordingScanDataControl());
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                window.Show();
                window.Activate();
                ShowHistory(window);
                var request = Assert.IsType<Button>(window.FindName("ClearScanDataButton"));
                var confirm = Assert.IsType<Button>(window.FindName("ConfirmClearScanDataButton"));
                Assert.True(request.Focus());
                Keyboard.Focus(request);
                Assert.True(request.IsKeyboardFocusWithin);
                InvokeButton(request);
                PumpUntil(() => viewModel.IsClearScanDataConfirmationPending
                    && confirm.IsVisible && confirm.IsEnabled);
                var cancel = WpfReviewSelectionTests.Descendants<Button>(window).Single(button =>
                    AutomationProperties.GetName(button) == "Cancel clearing local Scan data");

                InvokeButton(cancel);
                PumpUntil(() => !viewModel.IsClearScanDataConfirmationPending
                    && request.IsVisible && request.IsEnabled);

                Assert.True(request.IsKeyboardFocusWithin);
            }
            finally
            {
                window.Close();
                await viewModel.DisposeAsync();
            }
        });
    }

    [Fact]
    public void LeavingHistoryCancelsAPendingClearSoReturningDoesNotFocusTheConfirmation()
    {
        RunOnSta(async () =>
        {
            var fixture = new ExactShellFixture();
            var control = new RecordingScanDataControl();
            var viewModel = new ShellViewModel(
                fixture.Scope, fixture.Workflow, localScanDataControl: control);
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                window.Show();
                window.Activate();
                ShowHistory(window);
                var request = Assert.IsType<Button>(window.FindName("ClearScanDataButton"));
                var confirm = Assert.IsType<Button>(window.FindName("ConfirmClearScanDataButton"));
                InvokeButton(request);
                PumpUntil(() => viewModel.IsClearScanDataConfirmationPending
                    && confirm.IsVisible && confirm.IsKeyboardFocusWithin);

                var history = Assert.IsType<TabItem>(window.FindName("ActionRecordsTab"));
                var photos = Assert.IsType<TabControl>(history.Parent).Items.OfType<TabItem>()
                    .Single(tab => !ReferenceEquals(tab, history));
                photos.IsSelected = true;
                PumpUntil(() => !viewModel.IsClearScanDataConfirmationPending);
                ShowHistory(window);
                PumpUntil(() => request.IsVisible);

                Assert.False(confirm.IsVisible);
                Assert.False(confirm.IsKeyboardFocusWithin);
                Assert.Equal(0, control.Calls);
            }
            finally
            {
                window.Close();
                await viewModel.DisposeAsync();
            }
        });
    }

    [Fact]
    public async Task ActiveScanDisablesClearAndClosesEarlierConfirmationWithWaitOrCancelResult()
    {
        var fixture = new ExactShellFixture();
        var control = new RecordingScanDataControl(new(
            ScanDataClearOutcome.Cleared, 1, 0, "must not clear"));
        var viewModel = new ShellViewModel(
            fixture.Scope, fixture.Workflow, localScanDataControl: control);
        var requestAvailabilityChanges = 0;
        var confirmAvailabilityChanges = 0;
        viewModel.RequestClearScanDataCommand.CanExecuteChanged += (_, _) => requestAvailabilityChanges++;
        viewModel.ConfirmClearScanDataCommand.CanExecuteChanged += (_, _) => confirmAvailabilityChanges++;
        await viewModel.RequestClearScanDataCommand.ExecuteAsync();
        Assert.True(viewModel.IsClearScanDataConfirmationPending);
        var requestChangesBeforeBusy = requestAvailabilityChanges;
        var confirmChangesBeforeBusy = confirmAvailabilityChanges;

        fixture.PublishPhase(WorkflowPhase.Scan, ScanStatus.Running);

        Assert.False(viewModel.RequestClearScanDataCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmClearScanDataCommand.CanExecute(null));
        Assert.False(viewModel.IsClearScanDataConfirmationPending);
        Assert.True(requestAvailabilityChanges > requestChangesBeforeBusy);
        Assert.True(confirmAvailabilityChanges > confirmChangesBeforeBusy);
        Assert.Equal(
            "Local Scan data was retained because a Scan is active. Wait for it to finish, or cancel it, then try again.",
            viewModel.LocalDataClearStatus);
        await viewModel.ConfirmClearScanDataCommand.ExecuteAsync();
        Assert.Equal(0, control.Calls);
    }

    [Theory]
    [InlineData(ScanStatus.Idle)]
    [InlineData(ScanStatus.Faulted)]
    public async Task UnsettledScanTruthBlocksClearForIdleOrFaultedDisplayStatus(
        ScanStatus displayStatus)
    {
        var fixture = new ExactShellFixture();
        var control = new RecordingScanDataControl(new(
            ScanDataClearOutcome.Cleared, 1, 0, "must not clear"));
        var viewModel = new ShellViewModel(
            fixture.Scope, fixture.Workflow, localScanDataControl: control);
        var requestAvailabilityChanges = 0;
        var confirmAvailabilityChanges = 0;
        viewModel.RequestClearScanDataCommand.CanExecuteChanged += (_, _) => requestAvailabilityChanges++;
        viewModel.ConfirmClearScanDataCommand.CanExecuteChanged += (_, _) => confirmAvailabilityChanges++;
        await viewModel.RequestClearScanDataCommand.ExecuteAsync();
        Assert.True(viewModel.IsClearScanDataConfirmationPending);
        var requestChangesBeforeUnsettled = requestAvailabilityChanges;
        var confirmChangesBeforeUnsettled = confirmAvailabilityChanges;

        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = fixture.Workflow.Current.Scan with
            {
                Status = displayStatus,
                HasUnsettledWork = true,
            },
        });

        Assert.False(viewModel.RequestClearScanDataCommand.CanExecute(null));
        Assert.False(viewModel.ConfirmClearScanDataCommand.CanExecute(null));
        Assert.False(viewModel.IsClearScanDataConfirmationPending);
        Assert.True(requestAvailabilityChanges > requestChangesBeforeUnsettled);
        Assert.True(confirmAvailabilityChanges > confirmChangesBeforeUnsettled);
        Assert.Equal(
            "Local Scan data was retained because a Scan is active. Wait for it to finish, or cancel it, then try again.",
            viewModel.LocalDataClearStatus);
        await viewModel.ConfirmClearScanDataCommand.ExecuteAsync();
        Assert.Equal(0, control.Calls);
    }

    [Fact]
    public async Task RepeatedConfirmationWhileClearIsRunningInvokesTheControlOnce()
    {
        var fixture = new ExactShellFixture();
        var control = new RecordingScanDataControl();
        var viewModel = new ShellViewModel(
            fixture.Scope, fixture.Workflow, localScanDataControl: control);
        await viewModel.RequestClearScanDataCommand.ExecuteAsync();

        var first = viewModel.ConfirmClearScanDataCommand.ExecuteAsync();
        await control.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var repeated = viewModel.ConfirmClearScanDataCommand.ExecuteAsync();

        Assert.False(viewModel.ConfirmClearScanDataCommand.CanExecute(null));
        await repeated;
        Assert.Equal(1, control.Calls);
        control.Complete(new(ScanDataClearOutcome.BlockedByActionReconciliation, 0, 1,
            "Local Scan data was retained because an Action is unfinished or unknown. Reconcile the Action record, then try again."));
        await first;
        Assert.Equal(
            "Local Scan data was retained because an Action is unfinished or unknown. Reconcile the Action record, then try again.",
            viewModel.LocalDataClearStatus);
        Assert.False(viewModel.IsClearScanDataConfirmationPending);
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    // Clearing scan data lives on the History view.
    private static void ShowHistory(MainWindow window)
    {
        Assert.IsType<TabItem>(window.FindName("ActionRecordsTab")).IsSelected = true;
        window.UpdateLayout();
        Drain();
    }

    private static void InvokeButton(Button button)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button);
        Assert.NotNull(peer);
        Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var waitingSince = Stopwatch.GetTimestamp();
        while (!condition() && Stopwatch.GetElapsedTime(waitingSince) < TimeSpan.FromSeconds(2))
            WpfReviewSelectionTests.Drain();
        Assert.True(condition(), "The WPF postcondition did not settle before the two-second deadline.");
    }

    private static void RunOnSta(Func<Task> action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { action().GetAwaiter().GetResult(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Local-data WPF scenario exceeded ten seconds.");
        failure?.Throw();
    }

    private sealed class RecordingScanDataControl
        : ILocalScanDataControl
    {
        private readonly TaskCompletionSource<ScanDataClearResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal RecordingScanDataControl() { }

        internal RecordingScanDataControl(ScanDataClearResult result) =>
            completion.TrySetResult(result);

        internal int Calls { get; private set; }

        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ScanDataClearResult> ClearAsync(CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult();
            return await completion.Task.WaitAsync(cancellationToken);
        }

        internal void Complete(ScanDataClearResult result) => completion.TrySetResult(result);
    }
}
