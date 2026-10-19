using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using Albumen.App;
using Albumen.App.Composition;
using Albumen.App.Shell;
using Albumen.Application.Action;
using Albumen.Application.Review;
using Albumen.Application.Shell;
using Albumen.Domain.Action;
using Albumen.Domain.Workflow;
using Albumen.Infrastructure.Windows.Action;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfActionIdempotencyTests
{
    [Fact]
    public Task ProductionCompositionConnectsOneActionToTheReviewAuthorityValidatorAndSharedStores() =>
        WpfScenarioIsolation.RunAsync(typeof(WpfActionIdempotencyTests), "action-composition",
            nameof(ProductionCompositionConnectsOneActionToTheReviewAuthorityValidatorAndSharedStores), stage =>
        {
            WpfReviewSelectionTests.RunOnSta(() =>
            {
                using var database = new TemporaryDatabase();
                var viewModel = CompositionRoot.CreateShellViewModel(
                    database.Path, new WindowsRecycleBinExecutor());
                try
                {
                    var owned = Assert.Single(FieldValues(viewModel).OfType<IAlbumenWorkflow>());
                    var values = FieldValues(owned).ToArray();
                    var action = Assert.Single(values.OfType<ActionCoordinator>());
                    var review = Assert.Single(values.OfType<ReviewCoordinator>());
                    Assert.Single(FieldValues(review).OfType<ProtectedActionSafetyValidator>());
                    Assert.Same(review, Assert.Single(FieldValues(action).OfType<IApprovedActionAuthority>()));
                    var stores = Assert.Single(values, value => value?.GetType().Name == "SqliteAlbumenStores");
                    Assert.NotNull(stores);
                    var evidence = stores.GetType().GetProperty("OverrideEvidence")!.GetValue(stores);
                    var journal = stores.GetType().GetProperty("ActionJournal")!.GetValue(stores);
                    Assert.Same(evidence, Assert.Single(FieldValues(action).OfType<IOverrideEvidenceStore>()));
                    Assert.Same(evidence, Assert.Single(FieldValues(review).OfType<IOverrideEvidenceStore>()));
                    Assert.Same(journal, Assert.Single(FieldValues(action).OfType<IActionJournalStore>()));
                    Assert.Equal("WindowsRecycleBinExecutor", Assert.Single(FieldValues(action).OfType<IRecycleBinExecutor>()).GetType().Name);
                    Assert.Equal(ActionStatus.Idle, action.Current.Status);
                    Assert.Equal(ReviewStatus.Closed, review.Current.Status);
                }
                finally
                {
                    var disposal = viewModel.DisposeAsync().AsTask();
                    WpfActionAccessibilityTests.PumpUntil(() => disposal.IsCompleted);
                    disposal.GetAwaiter().GetResult();
                }
                stage.CleanupSucceeded();
            });
        });

    private static IEnumerable<object?> FieldValues(object instance) => instance.GetType()
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        .Select(field => field.GetValue(instance));

    [Fact]
    public Task OnlyExplicitNonDefaultApprovalActivatesAndSealedProgressCannotApproveAgain() =>
        WpfScenarioIsolation.RunAsync(typeof(WpfActionIdempotencyTests), "action-activation",
            nameof(OnlyExplicitNonDefaultApprovalActivatesAndSealedProgressCannotApproveAgain), stage =>
        {
            WpfReviewSelectionTests.RunOnSta(() =>
            {
                var fixture = new ActionShellFixture();
                var window = new MainWindow { DataContext = fixture.ViewModel };
                try
                {
                    window.Show();
                    window.Activate();
                    WpfReviewSelectionTests.Drain();
                    var approve = Assert.IsType<Button>(window.FindName("ApproveCleanupButton"));
                    Assert.Single(WpfReviewSelectionTests.Descendants<Button>(window),
                        button => ReferenceEquals(button.Command, fixture.ViewModel.ApproveReviewCommand));
                    Assert.False(approve.IsDefault);
                    Assert.False(approve.IsKeyboardFocused);
                    var heading = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ReviewHeading"));
                    heading.Focus();
                    Keyboard.Focus(heading);
                    WpfReviewSelectionTests.Press(heading, Key.Enter);
                    WpfReviewSelectionTests.Drain();
                    Assert.Empty(fixture.Approvals);
                    approve.Focus();
                    Keyboard.Focus(approve);
                    WpfReviewSelectionTests.Press(approve, Key.Enter);
                    WpfReviewSelectionTests.Drain();
                    var command = Assert.Single(fixture.Approvals);
                    Assert.Equal(fixture.Current.Review.Pin, command.Session);
                    Assert.Equal(fixture.Current.Review.SelectionRevision, command.SelectionRevision);
                    var action = ActionShellFixture.Snapshot(new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(), ActionItemState.Executing, ActionSafetyReason.None));
                    fixture.Publish(action);
                    WpfReviewSelectionTests.Drain();
                    fixture.Publish(action, WorkflowPhase.Review);
                    WpfReviewSelectionTests.Drain();
                    Assert.False(approve.IsEnabled);
                    WpfReviewSelectionTests.Press(heading, Key.Enter);
                    WpfReviewSelectionTests.Press(heading, Key.Space);
                    WpfReviewSelectionTests.Drain();
                    Assert.Single(fixture.Approvals);
                    Assert.Single(fixture.Rows);
                }
                finally { window.Close(); WpfReviewSelectionTests.Drain(); }
                stage.CleanupSucceeded();
            });
        });

    [Fact]
    public Task CloseSettlesActionReviewAndScanBeforeSharedStoresWhileDispatcherCanStillAcceptWork() =>
        WpfScenarioIsolation.RunAsync(typeof(WpfActionIdempotencyTests), "action-close",
            nameof(CloseSettlesActionReviewAndScanBeforeSharedStoresWhileDispatcherCanStillAcceptWork), stage =>
        {
            WpfReviewSelectionTests.RunOnSta(() =>
            {
                var fixture = new ActionShellFixture();
                var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var releaseAction = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var actionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var action = new Lifetime(async () =>
                {
                    calls.Enqueue("action-enter");
                    actionEntered.TrySetResult();
                    await releaseAction.Task;
                    await dispatcher.InvokeAsync(() => { Assert.False(dispatcher.HasShutdownStarted); calls.Enqueue("action-settled"); });
                });
                var review = new Lifetime(async () =>
                    await dispatcher.InvokeAsync(() => { Assert.False(dispatcher.HasShutdownStarted); calls.Enqueue("review"); }));
                var scan = new Lifetime(async () =>
                    await dispatcher.InvokeAsync(() => { Assert.False(dispatcher.HasShutdownStarted); calls.Enqueue("scan"); }));
                var stores = new Lifetime(async () =>
                    await dispatcher.InvokeAsync(() => { Assert.False(dispatcher.HasShutdownStarted); calls.Enqueue("stores"); }));
                var ownedType = typeof(CompositionRoot).GetNestedType("OwnedAlbumenWorkflow", BindingFlags.NonPublic);
                Assert.NotNull(ownedType);
                var constructor = ownedType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SingleOrDefault(candidate => candidate.GetParameters().Length == 5);
                Assert.NotNull(constructor);
                var owned = Assert.IsAssignableFrom<IAlbumenWorkflow>(
                    constructor.Invoke([fixture, scan, action, review, stores]));
                var viewModel = new ShellViewModel(fixture.Scope, owned);
                var window = new MainWindow { DataContext = viewModel };
                var closed = false;
                window.Closed += (_, _) => closed = true;
                try
                {
                    window.Show();
                    window.Close();
                    WpfActionAccessibilityTests.PumpUntil(() => actionEntered.Task.IsCompleted);
                    Assert.False(closed);
                    Assert.Equal(["action-enter"], calls);
                    releaseAction.TrySetResult();
                    WpfActionAccessibilityTests.PumpUntil(() => closed);
                    Assert.Equal(["action-enter", "action-settled", "review", "scan", "stores"], calls);
                    Assert.Empty(fixture.Approvals);
                }
                finally
                {
                    releaseAction.TrySetResult();
                    var disposal = viewModel.DisposeAsync().AsTask();
                    WpfActionAccessibilityTests.PumpUntil(() => disposal.IsCompleted);
                    disposal.GetAwaiter().GetResult();
                    if (!closed) window.Close();
                    WpfReviewSelectionTests.Drain();
                }
                stage.CleanupSucceeded();
            });
        });

    private sealed class Lifetime(Func<Task> dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(dispose());
    }
}
