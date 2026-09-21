using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoSift.App;
using PhotoSift.App.Shell;
using PhotoSift.Application.Review;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

[Collection("WPF UI Automation")]
public sealed class WpfApprovalAccessibilityTests
{
    private const string DiagnosticPathCanary = @"C:\E5S3-PATH-CANARY\private-photo.jpg";
    private const string DiagnosticPixelCanary = "E5S3-PIXEL-CANARY-255-0-127-64";
    private const string PersistenceFailureAnnouncement =
        "Override evidence persistence failed; Selection is unchanged.";
    private const string StaleFailureAnnouncement =
        "The Review changed; the stale command made no change.";

    [Fact]
    public void ProductionDiagnosticCaptureExcludesSensitiveAnnouncementTextAndClearsOnStop()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var status = new ReviewAnnouncementTextBlock();
            var window = new Window { Content = status };
            try
            {
                window.Show();
                WpfReviewSelectionTests.Drain();
                EnableProductionDiagnostics(status);
                var announce = typeof(ReviewAnnouncementTextBlock).GetMethod(
                    "Announce", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Could not find Announce.");
                announce.Invoke(status, [1L, DiagnosticPathCanary + " " + DiagnosticPixelCanary]);

                var snapshot = StopProductionDiagnostics(status);
                var records = ReadDiagnosticRecords(snapshot);
                Assert.NotEmpty(records);
                Assert.All(records, record =>
                {
                    Assert.Null(record.GetType().GetProperty("Payload"));
                    Assert.Null(record.GetType().GetProperty("Previous"));
                    Assert.Null(record.GetType().GetProperty("Current"));
                    Assert.DoesNotContain(DiagnosticPathCanary, record.ToString(), StringComparison.Ordinal);
                    Assert.DoesNotContain(DiagnosticPixelCanary, record.ToString(), StringComparison.Ordinal);
                });
                Assert.Empty(ReadDiagnosticRecords(StopProductionDiagnostics(status)));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ApprovalPanelDisclosesEveryCountRecoveryCoverageUnresolvedFactAndBoundaryInSourceOrder()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready(selected: [1, 4]);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                WpfReviewSelectionTests.Drain();
                var panel = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ApprovalPanel"));
                Assert.Equal("approval-panel", AutomationProperties.GetAutomationId(panel));
                var sourceOrder = WpfReviewSelectionTests.Descendants<TextBlock>(panel)
                    .Select(text => text.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
                AssertTextBefore(sourceOrder, "2 selected occurrences", "2 selected paths");
                AssertTextBefore(sourceOrder, "2 selected paths", "2 distinct physical files");
                AssertTextBefore(sourceOrder, "Estimated recovery for this Selection: 1024 bytes", "Incomplete Scan coverage");
                AssertTextBefore(sourceOrder, "Incomplete Scan coverage", "Unresolved total: 12");
                Assert.Contains(sourceOrder, text => text.Contains("incomplete enumeration: 1", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(sourceOrder, text => text.Contains("failed Scan rows: 2", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(sourceOrder, text => text.Contains("canceled Scan rows: 3", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(sourceOrder, text => text.Contains("unprocessed Scan rows: 4", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(sourceOrder, text => text.Contains("pending Review members: 1", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(sourceOrder, text => text.Contains("unavailable Review members: 1", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(sourceOrder, text => text.Contains("ordinary unselected survivors: 2", StringComparison.OrdinalIgnoreCase));
                AssertTextBefore(sourceOrder, "Windows Recycle Bin", "current checks");
                Assert.DoesNotContain(sourceOrder, text => text.Contains("safe to delete", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(sourceOrder, text => text.Contains("deleted successfully", StringComparison.OrdinalIgnoreCase));
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void InitialEnterCannotApproveButExplicitlyFocusedEnterAndSpaceReuseOneApprovalIdentity()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready(selected: [1, 4]);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                window.Activate();
                WpfReviewSelectionTests.Drain();
                var panel = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("ApprovalPanel"));
                var approve = Assert.IsType<Button>(window.FindName("ApproveCleanupButton"));
                Assert.False(approve.IsDefault);
                Assert.False(approve.IsKeyboardFocused);

                panel.Focus();
                Keyboard.Focus(panel);
                WpfReviewSelectionTests.Press(panel, Key.Enter);
                WpfReviewSelectionTests.Drain();
                Assert.Empty(fixture.Workflow.Approvals);

                approve.Focus();
                Keyboard.Focus(approve);
                WpfReviewSelectionTests.Press(approve, Key.Enter);
                WpfReviewSelectionTests.Drain();
                approve.Focus();
                WpfReviewSelectionTests.Press(approve, Key.Space);
                WpfReviewSelectionTests.Drain();

                Assert.Equal(2, fixture.Workflow.Approvals.Count);
                Assert.Equal(fixture.Workflow.Approvals[0].IdempotencyKey,
                    fixture.Workflow.Approvals[1].IdempotencyKey);
                Assert.Equal(fixture.Workflow.Current.Review.Pin, fixture.Workflow.Approvals[0].Session);
                Assert.Equal(9, fixture.Workflow.Approvals[0].SelectionRevision);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ReviewAutomationNamesAndItemStatusExposePositionSelectionSafetyAndDisclosure()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready(externalSurvivor: true, selected: [1]);
            WpfReviewSelectionTests.OpenFirstGroupedComparison(fixture.ViewModel);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            try
            {
                window.Show();
                WpfReviewSelectionTests.Drain();
                window.UpdateLayout();
                var root = UIElementAutomationPeer.CreatePeerForElement(window);
                Assert.NotNull(root);
                var group = FindPeer(root, "open-review-group");
                var groupName = group.GetName();
                var groupStatus = group.GetItemStatus();
                Assert.Contains("group 1 of 1", groupName, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("4 paths", groupName, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("4 physical", groupName, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("1 selected", groupStatus, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("external hard-link", groupStatus, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("path is unknown to this Scan", groupStatus, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Estimated recovery", groupStatus, StringComparison.OrdinalIgnoreCase);

                var selection = FindPeer(root, "selection-control");
                Assert.Contains("Eligible", selection.GetItemStatus(), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("selected", selection.GetName(), StringComparison.OrdinalIgnoreCase);

                var approval = FindPeer(root, "approval-panel");
                Assert.Contains("Cleanup approval disclosure", approval.GetName(), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("1 selected", approval.GetItemStatus(), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("Incomplete Scan coverage", approval.GetItemStatus(), StringComparison.OrdinalIgnoreCase);

                var summary = Assert.IsType<TextBlock>(window.FindName("SelectionSummaryText"));
                Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(summary));
                Assert.Contains("Incomplete", AutomationProperties.GetItemStatus(summary), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("unresolved total 12", AutomationProperties.GetItemStatus(summary), StringComparison.OrdinalIgnoreCase);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task PersistenceAndStaleFailuresRemainInspectableAndEachRepeatIsAnnouncedOnce()
    {
        var fixture = ReviewShellFixture.Ready();
        var announcements = new List<string>();
        fixture.ViewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(ShellViewModel.ReviewAnnouncement))
                announcements.Add(fixture.ViewModel.ReviewAnnouncement);
        };
        var current = fixture.Workflow.Current.Review;
        fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.PersistenceFailed, current);
        fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.PersistenceFailed, current);
        await Assert.IsType<AsyncCommand>(fixture.ViewModel.ExactGroups[0].Members[0].ToggleSelectionCommand).ExecuteAsync();
        await Assert.IsType<AsyncCommand>(fixture.ViewModel.ExactGroups[0].Members[0].ToggleSelectionCommand).ExecuteAsync();
        fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.Stale, current);
        fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.Stale, current);
        await Assert.IsType<AsyncCommand>(fixture.ViewModel.ExactGroups[0].Members[0].ToggleSelectionCommand).ExecuteAsync();
        await Assert.IsType<AsyncCommand>(fixture.ViewModel.ExactGroups[0].Members[0].ToggleSelectionCommand).ExecuteAsync();

        Assert.Equal(4, announcements.Count);
        Assert.Contains("persistence", announcements[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("persistence", announcements[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stale", announcements[2], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stale", announcements[3], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public Task QueuedSelectionSummaryCannotCancelTheOnlyFailureLiveAnnouncement()
    {
        return RunIsolatedAsync("live-duplicate", nameof(QueuedSelectionSummaryCannotCancelTheOnlyFailureLiveAnnouncement), stage =>
        {
            ExceptionDispatchInfo? scenarioFailure = null;
            var cleanupSafe = false;
            WpfReviewSelectionTests.RunOnSta(() =>
            {
                var fixture = ReviewShellFixture.Ready();
                var window = new MainWindow { DataContext = fixture.ViewModel };
                var diagnostics = new UiaDiagnosticLedger();
                var status = Assert.IsType<ReviewAnnouncementTextBlock>(window.FindName("ReviewAnnouncementText"));
                var captureEnabled = false;
                object? productionDiagnostics = null;
                Exception? scenarioError = null;
                Exception? diagnosticError = null;
                UiaDiagnosticSettlement? settlement = null;
                var windowCloseSucceeded = false;
                long viewModelSequence = 0;
                PropertyChangedEventHandler viewModelObserver = (_, change) =>
                {
                    if (change.PropertyName == nameof(ShellViewModel.ReviewAnnouncement))
                        diagnostics.Record("vm-notification", ++viewModelSequence,
                            fixture.ViewModel.ReviewAnnouncement, fixture.ViewModel);
                };
                try
                {
                    EnableProductionDiagnostics(status);
                    captureEnabled = true;
                    fixture.ViewModel.PropertyChanged += viewModelObserver;
                    window.Show();
                    WpfReviewSelectionTests.Drain();
                    RecordSetupPeer(status, diagnostics);
                    var handle = new WindowInteropHelper(window).Handle;
                    Assert.NotEqual(nint.Zero, handle);
                    var current = fixture.Workflow.Current.Review;
                    settlement = VerifyFailureLiveAnnouncementOnMta(
                        handle, PersistenceFailureAnnouncement, diagnostics, () =>
                    {
                        diagnostics.Record("command-persistence-begin", payload: "PersistenceFailed");
                        fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.PersistenceFailed, current);
                        var pending = Assert.IsType<AsyncCommand>(fixture.ViewModel.ExactGroups[0].Members[0]
                            .ToggleSelectionCommand).ExecuteAsync();
                        diagnostics.Record("command-persistence-return", payload: "PersistenceFailed");
                        fixture.Workflow.Publish(ReviewData.WithSelection(current, [4], 10));
                        diagnostics.Record("selection-publication-return", payload: "revision=10");
                        return pending;
                    });
                    diagnosticError = settlement.DiagnosticFailure?.SourceException;
                    settlement.OriginalFailure?.Throw();
                }
                catch (Exception error)
                {
                    scenarioError = error;
                }
                finally
                {
                    fixture.ViewModel.PropertyChanged -= viewModelObserver;
                    try
                    {
                        if (captureEnabled && settlement?.SnapshotSafe is true)
                            productionDiagnostics = StopProductionDiagnostics(status);
                        else if (diagnosticError is null)
                            diagnosticError = new IsolatedScenarioFailureException(
                                "The UIA live diagnostic did not reach safe snapshot settlement.");
                    }
                    catch (Exception error)
                    {
                        diagnosticError ??= error;
                    }
                    finally
                    {
                        try
                        {
                            window.Close();
                            windowCloseSucceeded = true;
                        }
                        catch (Exception error)
                        {
                            diagnosticError ??= error;
                        }
                    }
                }
                cleanupSafe = settlement?.SnapshotSafe is true && windowCloseSucceeded;
                if (settlement?.SnapshotSafe is not true)
                {
                    scenarioFailure = ExceptionDispatchInfo.Capture(new IsolatedScenarioFailureException(
                        "diagnostic-settlement-unsafe: snapshot and ledgers were not read."));
                }
                else
                {
                    var invalid = diagnostics.Invalid;
                    try
                    {
                        invalid |= ProductionDiagnosticsInvalid(productionDiagnostics);
                        AssertProductAnnouncementCapture(productionDiagnostics, diagnostics,
                            [PersistenceFailureAnnouncement]);
                    }
                    catch (Exception error)
                    {
                        scenarioError ??= error;
                        invalid = true;
                    }
                    if (scenarioError is not null || diagnosticError is not null || invalid)
                    {
                        try
                        {
                            scenarioFailure = ExceptionDispatchInfo.Capture(new IsolatedScenarioFailureException(
                                FormatDiagnostics(scenarioError, diagnosticError,
                                    productionDiagnostics, diagnostics)));
                        }
                        catch (Exception error)
                        {
                            scenarioFailure = ExceptionDispatchInfo.Capture(new IsolatedScenarioFailureException(
                                "diagnostic-format-failed: " + error));
                        }
                    }
                }
            });
            if (cleanupSafe) stage.CleanupSucceeded();
            scenarioFailure?.Throw();
        });
    }

    [Fact]
    public Task QueuedFailureAnnouncementsPreserveTheirOriginatingPayloadsOnceAndInOrder()
    {
        return RunIsolatedAsync("item-status", nameof(QueuedFailureAnnouncementsPreserveTheirOriginatingPayloadsOnceAndInOrder), stage =>
        {
            ExceptionDispatchInfo? scenarioFailure = null;
            var cleanupSafe = false;
            WpfReviewSelectionTests.RunOnSta(() =>
            {
                var fixture = ReviewShellFixture.Ready();
                var window = new MainWindow { DataContext = fixture.ViewModel };
                var diagnostics = new UiaDiagnosticLedger();
                var status = Assert.IsType<ReviewAnnouncementTextBlock>(window.FindName("ReviewAnnouncementText"));
                var captureEnabled = false;
                object? productionDiagnostics = null;
                Exception? scenarioError = null;
                Exception? diagnosticError = null;
                UiaDiagnosticSettlement? settlement = null;
                var windowCloseSucceeded = false;
                long viewModelSequence = 0;
                PropertyChangedEventHandler viewModelObserver = (_, change) =>
                {
                    if (change.PropertyName == nameof(ShellViewModel.ReviewAnnouncement))
                        diagnostics.Record("vm-notification", ++viewModelSequence,
                            fixture.ViewModel.ReviewAnnouncement, fixture.ViewModel);
                };
                try
                {
                    EnableProductionDiagnostics(status);
                    captureEnabled = true;
                    fixture.ViewModel.PropertyChanged += viewModelObserver;
                    window.Show();
                    WpfReviewSelectionTests.Drain();
                    RecordSetupPeer(status, diagnostics);
                    var handle = new WindowInteropHelper(window).Handle;
                    Assert.NotEqual(nint.Zero, handle);
                    var current = fixture.Workflow.Current.Review;
                    settlement = VerifyFailurePropertyAnnouncementsOnMta(
                        handle, [PersistenceFailureAnnouncement, StaleFailureAnnouncement],
                        diagnostics, async () =>
                    {
                        diagnostics.Record("command-persistence-begin", payload: "PersistenceFailed");
                        fixture.Workflow.NextSelectionResult =
                            new(ReviewCommandOutcome.PersistenceFailed, current);
                        await Assert.IsType<AsyncCommand>(fixture.ViewModel.ExactGroups[0].Members[0]
                            .ToggleSelectionCommand).ExecuteAsync();
                        diagnostics.Record("command-persistence-return", payload: "PersistenceFailed");
                        diagnostics.Record("command-stale-begin", payload: "Stale");
                        fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.Stale, current);
                        await Assert.IsType<AsyncCommand>(fixture.ViewModel.ExactGroups[0].Members[0]
                            .ToggleSelectionCommand).ExecuteAsync();
                        diagnostics.Record("command-stale-return", payload: "Stale");
                    });
                    diagnosticError = settlement.DiagnosticFailure?.SourceException;
                    settlement.OriginalFailure?.Throw();
                }
                catch (Exception error)
                {
                    scenarioError = error;
                }
                finally
                {
                    fixture.ViewModel.PropertyChanged -= viewModelObserver;
                    try
                    {
                        if (captureEnabled && settlement?.SnapshotSafe is true)
                            productionDiagnostics = StopProductionDiagnostics(status);
                        else if (diagnosticError is null)
                            diagnosticError = new IsolatedScenarioFailureException(
                                "The UIA property diagnostic did not reach safe snapshot settlement.");
                    }
                    catch (Exception error)
                    {
                        diagnosticError ??= error;
                    }
                    finally
                    {
                        try
                        {
                            window.Close();
                            windowCloseSucceeded = true;
                        }
                        catch (Exception error)
                        {
                            diagnosticError ??= error;
                        }
                    }
                }
                cleanupSafe = settlement?.SnapshotSafe is true && windowCloseSucceeded;
                if (settlement?.SnapshotSafe is not true)
                {
                    scenarioFailure = ExceptionDispatchInfo.Capture(new IsolatedScenarioFailureException(
                        "diagnostic-settlement-unsafe: snapshot and ledgers were not read."));
                }
                else
                {
                    var invalid = diagnostics.Invalid;
                    try
                    {
                        invalid |= ProductionDiagnosticsInvalid(productionDiagnostics);
                        AssertProductAnnouncementCapture(productionDiagnostics, diagnostics,
                            [PersistenceFailureAnnouncement, StaleFailureAnnouncement]);
                    }
                    catch (Exception error)
                    {
                        scenarioError ??= error;
                        invalid = true;
                    }
                    if (scenarioError is not null || diagnosticError is not null || invalid)
                    {
                        try
                        {
                            scenarioFailure = ExceptionDispatchInfo.Capture(new IsolatedScenarioFailureException(
                                FormatDiagnostics(scenarioError, diagnosticError,
                                    productionDiagnostics, diagnostics)));
                        }
                        catch (Exception error)
                        {
                            scenarioFailure = ExceptionDispatchInfo.Capture(new IsolatedScenarioFailureException(
                                "diagnostic-format-failed: " + error));
                        }
                    }
                }
            });
            if (cleanupSafe) stage.CleanupSucceeded();
            scenarioFailure?.Throw();
        });
    }

    [Fact]
    public void NormalWindowCloseWaitsForOneAsynchronousLifetimeDrain()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new ExactShellFixture();
            fixture.Workflow.HoldDisposal = true;
            var window = new MainWindow { DataContext = fixture.ViewModel };
            window.Show();
            try
            {
                window.Close();

                Assert.True(window.IsVisible);
                Assert.True(fixture.Workflow.DisposeEntered.Task.IsCompleted);
                Assert.Equal(1, fixture.Workflow.DisposeCalls);
                fixture.Workflow.DisposeRelease.TrySetResult();
                PumpUntil(() => !window.IsVisible);
                Assert.Equal(1, fixture.Workflow.DisposeCalls);
            }
            finally
            {
                fixture.Workflow.DisposeRelease.TrySetResult();
                if (window.IsVisible)
                {
                    window.Close();
                    PumpUntil(() => !window.IsVisible);
                }
            }
        });
    }

    [Fact]
    public Task FaultedLifetimeCloseUnwindsBeforeItsFinalCloseAndObservesTheFailure()
    {
        return RunIsolatedAsync("faulted-close", nameof(FaultedLifetimeCloseUnwindsBeforeItsFinalCloseAndObservesTheFailure), stage =>
        {
            ExceptionDispatchInfo? scenarioFailure = null;
            WpfReviewSelectionTests.RunOnSta(() =>
            {
                var fixture = new ExactShellFixture();
                var marker = $"faulted-close-{Guid.NewGuid():N}";
                fixture.Workflow.DisposalFailure = new InvalidOperationException(marker);
                using var listener = new MarkerTraceListener(
                    "PHOTOSIFT_DIAGNOSTIC stage=shutdown code=lifetime_dispose_failed");
                MainWindow? window = null;
                Trace.Listeners.Add(listener);
                try
                {
                    window = new MainWindow { DataContext = fixture.ViewModel };
                    var initialCloseReturned = false;
                    var closingsBeforeReturn = 0;
                    var totalClosings = 0;
                    window.Closing += (_, _) =>
                    {
                        totalClosings++;
                        if (!initialCloseReturned) closingsBeforeReturn++;
                    };
                    try
                    {
                        window.Show();
                        window.Close();
                        initialCloseReturned = true;
                        var lifetimeFailure = ObserveLifetime(fixture.ViewModel);

                        Assert.Equal(1, closingsBeforeReturn);
                        PumpUntil(() => !window.IsVisible);
                        Assert.Equal(2, totalClosings);
                        Assert.Equal(1, fixture.Workflow.DisposeCalls);
                        var observedFailure = Assert.IsType<InvalidOperationException>(lifetimeFailure);
                        Assert.Contains(marker, observedFailure.Message, StringComparison.Ordinal);
                        Assert.True(listener.SawMarker,
                            "The asynchronous close owner did not report its stable lifetime-failure code.");
                        Assert.False(listener.Contains(marker),
                            "The asynchronous close diagnostic exposed exception content.");
                    }
                    catch (Exception error)
                    {
                        scenarioFailure = ExceptionDispatchInfo.Capture(error);
                    }
                }
                finally
                {
                    try
                    {
                        fixture.Workflow.DisposeRelease.TrySetResult();
                        _ = ObserveLifetime(fixture.ViewModel);
                    }
                    finally
                    {
                        try
                        {
                            if (window?.IsVisible is true)
                            {
                                window.DataContext = null;
                                window.Close();
                                PumpUntil(() => !window.IsVisible);
                            }
                        }
                        finally
                        {
                            Trace.Listeners.Remove(listener);
                        }
                    }
                }
            });
            stage.CleanupSucceeded();
            scenarioFailure?.Throw();
        });
    }

    [Fact]
    public void LifetimeCompletionDuringClosingStillSchedulesFinalCloseAfterUnwind()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = new ExactShellFixture();
            fixture.Workflow.HoldDisposal = true;
            var window = new MainWindow { DataContext = fixture.ViewModel };
            var initialCloseReturned = false;
            var closingsBeforeReturn = 0;
            var totalClosings = 0;
            window.Closing += (_, _) =>
            {
                totalClosings++;
                if (!initialCloseReturned)
                {
                    closingsBeforeReturn++;
                    fixture.Workflow.DisposeRelease.TrySetResult();
                }
            };
            try
            {
                window.Show();
                window.Close();
                initialCloseReturned = true;
                Assert.Null(ObserveLifetime(fixture.ViewModel));

                Assert.Equal(1, closingsBeforeReturn);
                PumpUntil(() => !window.IsVisible);
                Assert.Equal(2, totalClosings);
                Assert.Equal(1, fixture.Workflow.DisposeCalls);
            }
            finally
            {
                try
                {
                    initialCloseReturned = true;
                    fixture.Workflow.DisposeRelease.TrySetResult();
                    _ = ObserveLifetime(fixture.ViewModel);
                }
                finally
                {
                    if (window.IsVisible)
                    {
                        window.DataContext = null;
                        window.Close();
                        PumpUntil(() => !window.IsVisible);
                    }
                }
            }
        });
    }

    [Fact]
    public void ActualFocusHandlerChangesFocusWithoutApprovingSealingOrReadingEvidence()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var fixture = ReviewShellFixture.Ready(selected: [1]);
            WpfReviewSelectionTests.OpenFirstGroupedComparison(fixture.ViewModel);
            var window = new MainWindow { DataContext = fixture.ViewModel };
            window.Show();
            WpfReviewSelectionTests.Drain();
            var focusTarget = WpfReviewSelectionTests.Descendants<ToggleButton>(window)
                .First(control => AutomationProperties.GetAutomationId(control) == "selection-control");

            var heading = Assert.IsType<TextBlock>(window.FindName("ReviewHeading"));
            Assert.True(heading.Focus());
            Keyboard.Focus(heading);
            var focusBefore = Keyboard.FocusedElement;
            Assert.Same(heading, focusBefore);
            Assert.True(focusTarget.Focus());
            Keyboard.Focus(focusTarget);
            WpfReviewSelectionTests.Drain();
            Assert.True(focusTarget.IsKeyboardFocused);
            Assert.NotSame(focusBefore, Keyboard.FocusedElement);
            AssertNoApproval(fixture);

            window.Close();
        });
    }

    [Fact]
    public void ActualWindowCloseHandlerClosesPendingOverrideWithoutApproval()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var window = new MainWindow();
            var fixture = ReviewShellFixture.Ready(selected: [1],
                dialog: new WpfReviewDialogService(() => window));
            WpfReviewSelectionTests.OpenFirstGroupedComparison(fixture.ViewModel);
            window.DataContext = fixture.ViewModel;
            window.Show();
            WpfReviewSelectionTests.Drain();
            var before = fixture.Workflow.Current.Review;
            fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.OverrideRequired, before,
                ReviewData.Proposal(before));
            var selection = WpfReviewSelectionTests.Descendants<ToggleButton>(window)
                .First(control => AutomationProperties.GetAutomationId(control) == "selection-control");
            Assert.True(selection.Focus());
            var commandCompleted = false;

            var interaction = WpfSurvivorOverrideDialogTests.RunModalInteraction(window,
                _ => window.Close(),
                () =>
                {
                    Assert.IsType<AsyncCommand>(selection.Command).ExecuteAsync().GetAwaiter().GetResult();
                    commandCompleted = true;
                });

            Assert.True(commandCompleted);
            Assert.True(interaction.DialogClosed);
            Assert.Equal(SurvivorOverrideDialogResult.Cancel, interaction.DialogResult);
            Assert.False(window.IsVisible);
            Assert.Equal(before.Groups[0].SelectedOccurrenceIds,
                fixture.Workflow.Current.Review.Groups[0].SelectedOccurrenceIds);
            AssertNoApproval(fixture);
        });
    }

    [Fact]
    public void ActualApplicationExitHandlerClosesPendingOverrideWithoutApproval()
    {
        WpfReviewSelectionTests.RunOnSta(() =>
        {
            var window = new MainWindow();
            var fixture = ReviewShellFixture.Ready(selected: [1],
                dialog: new WpfReviewDialogService(() => window));
            WpfReviewSelectionTests.OpenFirstGroupedComparison(fixture.ViewModel);
            window.DataContext = fixture.ViewModel;
            window.Show();
            WpfReviewSelectionTests.Drain();
            var before = fixture.Workflow.Current.Review;
            fixture.Workflow.NextSelectionResult = new(ReviewCommandOutcome.OverrideRequired, before,
                ReviewData.Proposal(before));
            var selection = WpfReviewSelectionTests.Descendants<ToggleButton>(window)
                .First(control => AutomationProperties.GetAutomationId(control) == "selection-control");
            Assert.True(selection.Focus());
            var commandCompleted = false;
            try
            {
                var interaction = WpfSurvivorOverrideDialogTests.RunModalInteraction(window,
                    _ => window.OnApplicationExit(window, EventArgs.Empty),
                    () =>
                    {
                        Assert.IsType<AsyncCommand>(selection.Command).ExecuteAsync().GetAwaiter().GetResult();
                        commandCompleted = true;
                    });

                Assert.True(commandCompleted);
                Assert.True(interaction.DialogClosed);
                Assert.Equal(SurvivorOverrideDialogResult.Cancel, interaction.DialogResult);
                Assert.Equal(before.Groups[0].SelectedOccurrenceIds,
                    fixture.Workflow.Current.Review.Groups[0].SelectedOccurrenceIds);
                AssertNoApproval(fixture);
            }
            finally { window.Close(); }
        });
    }

    private static void AssertNoApproval(ReviewShellFixture fixture)
    {
        Assert.Empty(fixture.Workflow.Approvals);
        Assert.Empty(fixture.Workflow.Affirmations);
        Assert.Equal(0, fixture.Workflow.OverrideEvidenceReadAttempts);
        Assert.False(fixture.Workflow.Current.Review.Sealed);
        Assert.Null(fixture.Workflow.Current.Review.ApprovedRequest);
    }

    private static void AssertTextBefore(string[] lines, string earlier, string later)
    {
        var earlierIndex = Array.FindIndex(lines, line => line.Contains(earlier, StringComparison.OrdinalIgnoreCase));
        var laterIndex = Array.FindIndex(lines, line => line.Contains(later, StringComparison.OrdinalIgnoreCase));
        Assert.True(earlierIndex >= 0, $"Missing source-order text: {earlier}");
        Assert.True(laterIndex >= 0, $"Missing source-order text: {later}");
        Assert.True(earlierIndex < laterIndex, $"Expected '{earlier}' before '{later}'.");
    }

    private static AutomationPeer FindPeer(AutomationPeer root, string automationId)
    {
        return FindPeerOrNull(root, automationId)
            ?? throw new InvalidOperationException(
                $"Automation peer '{automationId}' was not present in the product tree.");
    }

    private static AutomationPeer? FindPeerOrNull(AutomationPeer root, string automationId)
    {
        if (root.GetAutomationId() == automationId) return root;
        foreach (var child in root.GetChildren() ?? [])
            if (FindPeerOrNull(child, automationId) is { } match) return match;
        return null;
    }

    private static UiaDiagnosticSettlement VerifyFailureLiveAnnouncementOnMta(
        nint handle,
        string expectedPayload,
        UiaDiagnosticLedger diagnostics,
        Func<Task> publish)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        ExceptionDispatchInfo? originalFailure = null;
        ExceptionDispatchInfo? diagnosticFailure = null;
        DispatcherOperation? stopOperation = null;
        var removalSucceeded = false;
        var callbackIdleSettled = false;
        var acceptedCallbacksZero = false;
        var client = new Thread(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(handle);
                var element = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "Review command status"));
                Assert.NotNull(element);
                var nativeRuntimeId = CaptureRuntimeId(element);
                diagnostics.Record("setup-native", sender: element, runtimeId: nativeRuntimeId);
                var statuses = new BlockingCollection<LiveNativeDelivery>(boundedCapacity: 256);
                var callbackIdle = new ManualResetEventSlim(initialState: true);
                ExceptionDispatchInfo? callbackFailure = null;
                var callbackGate = new object();
                var acceptingCallbacks = true;
                var activeCallbacks = 0;
                var handlerRegistered = false;
                AutomationEventHandler handler = (sender, eventArgs) =>
                {
                    var entryTimestamp = Stopwatch.GetTimestamp();
                    lock (callbackGate)
                    {
                        if (!acceptingCallbacks) return;
                        activeCallbacks++;
                        callbackIdle.Reset();
                    }
                    try
                    {
                        var runtimeId = CaptureCachedRuntimeId(sender, out var cacheFailure);
                        diagnostics.Record("uia-callback-live", sender: sender, runtimeId: runtimeId,
                            eventId: eventArgs.EventId.Id, timestamp: entryTimestamp);
                        if (cacheFailure is not null)
                        {
                            diagnostics.Invalidate();
                            diagnostics.Record("uia-callback-live-runtime-cache-missing",
                                payload: cacheFailure, sender: sender, eventId: eventArgs.EventId.Id);
                        }
                        if (sender is AutomationElement source)
                        {
                            diagnostics.Record("uia-live-current-read-begin", sender: source,
                                runtimeId: runtimeId, eventId: eventArgs.EventId.Id);
                            var status = source.Current.ItemStatus;
                            diagnostics.Record("uia-live-current-read-end", payload: status,
                                sender: source, runtimeId: runtimeId, eventId: eventArgs.EventId.Id);
                            if (!statuses.TryAdd(new(runtimeId, eventArgs.EventId.Id, status)))
                                diagnostics.InvalidateOverflow();
                        }
                    }
                    catch (Exception error)
                    {
                        Interlocked.CompareExchange(ref callbackFailure,
                            ExceptionDispatchInfo.Capture(error), null);
                    }
                    finally
                    {
                        lock (callbackGate)
                        {
                            activeCallbacks--;
                            if (activeCallbacks == 0) callbackIdle.Set();
                        }
                    }
                };
                ExceptionDispatchInfo? operationFailure = null;
                ExceptionDispatchInfo? removalFailure = null;
                try
                {
                    diagnostics.Record("registration-live-begin", sender: element, runtimeId: nativeRuntimeId);
                    var cacheRequest = CreateRuntimeIdCacheRequest();
                    using (cacheRequest.Activate())
                        Automation.AddAutomationEventHandler(AutomationElementIdentifiers.LiveRegionChangedEvent,
                            element, TreeScope.Element, handler);
                    handlerRegistered = true;
                    diagnostics.Record("registration-live-return", sender: element, runtimeId: nativeRuntimeId);
                    Task? published = null;
                    diagnostics.Record("publication-dispatch-begin");
                    var publication = dispatcher.BeginInvoke(new Action(() =>
                    {
                        diagnostics.Record("publication-sta-begin");
                        published = publish();
                        diagnostics.Record("publication-sta-return");
                    }));
                    diagnostics.Record("publication-dispatch-return");
                    publication.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    Assert.NotNull(published);
                    diagnostics.Record("command-task-await-begin");
                    published.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    diagnostics.Record("command-task-await-return");
                    Assert.True(statuses.TryTake(out var delivery, TimeSpan.FromSeconds(4)),
                        $"The '{expectedPayload}' failure live-region announcement was canceled or never raised.");
                    var matchingDeliveries = 0;
                    LiveNativeDelivery? previous = null;
                    var drainStarted = Stopwatch.GetTimestamp();
                    var drainLimit = TimeSpan.FromMilliseconds(250);
                    while (true)
                    {
                        if (previous is not { } prior || !SameLiveDelivery(prior, delivery))
                        {
                            if (string.Equals(delivery.Payload, expectedPayload, StringComparison.Ordinal))
                                matchingDeliveries++;
                            previous = delivery;
                        }
                        var remaining = drainLimit - Stopwatch.GetElapsedTime(drainStarted);
                        if (remaining <= TimeSpan.Zero || !statuses.TryTake(out delivery, remaining)) break;
                    }
                    Assert.True(matchingDeliveries >= 1,
                        $"No normalized native live delivery contained '{expectedPayload}'.");
                }
                catch (Exception error)
                {
                    operationFailure = ExceptionDispatchInfo.Capture(error);
                }
                finally
                {
                    lock (callbackGate) acceptingCallbacks = false;
                    try
                    {
                        diagnostics.Record("removal-live-begin", sender: element, runtimeId: nativeRuntimeId);
                        if (handlerRegistered)
                            Automation.RemoveAutomationEventHandler(
                                AutomationElementIdentifiers.LiveRegionChangedEvent, element, handler);
                        removalSucceeded = handlerRegistered;
                        diagnostics.Record("removal-live-return", sender: element, runtimeId: nativeRuntimeId);
                    }
                    catch (Exception error)
                    {
                        removalFailure = ExceptionDispatchInfo.Capture(error);
                    }
                    diagnostics.Record("quiescence-live-begin");
                    callbackIdleSettled = callbackIdle.Wait(TimeSpan.FromSeconds(2));
                    lock (callbackGate) acceptedCallbacksZero = activeCallbacks == 0;
                    diagnostics.Record("quiescence-live-return",
                        payload: $"idle={callbackIdleSettled}; acceptedCallbacksZero={acceptedCallbacksZero}");
                    if (callbackIdleSettled && acceptedCallbacksZero)
                    {
                        statuses.Dispose();
                        callbackIdle.Dispose();
                    }
                }
                originalFailure = operationFailure;
                diagnosticFailure = callbackFailure ?? removalFailure;
                if ((!callbackIdleSettled || !acceptedCallbacksZero || !removalSucceeded)
                    && diagnosticFailure is null)
                    diagnosticFailure = ExceptionDispatchInfo.Capture(new IsolatedScenarioFailureException(
                        "The UIA live-region callback did not settle safely after unregistration."));
            }
            catch (Exception error)
            {
                diagnosticFailure ??= ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                stopOperation = dispatcher.BeginInvoke(new Action(() => frame.Continue = false));
            }
        }) { IsBackground = true };
        client.SetApartmentState(ApartmentState.MTA);
        client.Start();
        Dispatcher.PushFrame(frame);
        var clientJoined = client.Join(TimeSpan.FromSeconds(2));
        var stopCompleted = false;
        if (clientJoined && stopOperation is not null)
        {
            try
            {
                stopOperation.Task.GetAwaiter().GetResult();
                stopCompleted = stopOperation.Status == DispatcherOperationStatus.Completed;
            }
            catch (Exception error)
            {
                diagnosticFailure ??= ExceptionDispatchInfo.Capture(error);
            }
        }
        if (!clientJoined && diagnosticFailure is null)
            diagnosticFailure = ExceptionDispatchInfo.Capture(new IsolatedScenarioFailureException(
                "The UIA failure-announcement client did not settle."));
        diagnostics.Record("settlement-live",
            payload: $"removalSucceeded={removalSucceeded}; callbackIdleSettled={callbackIdleSettled}; "
                + $"acceptedCallbacksZero={acceptedCallbacksZero}; "
                + $"clientJoined={clientJoined}; stopCompleted={stopCompleted}");
        return new(originalFailure, diagnosticFailure, removalSucceeded, callbackIdleSettled,
            acceptedCallbacksZero, clientJoined, stopCompleted);
    }

    private static UiaDiagnosticSettlement VerifyFailurePropertyAnnouncementsOnMta(
        nint handle,
        string[] expectedPayloads,
        UiaDiagnosticLedger diagnostics,
        Func<Task> publish)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        ExceptionDispatchInfo? originalFailure = null;
        ExceptionDispatchInfo? diagnosticFailure = null;
        DispatcherOperation? stopOperation = null;
        var removalSucceeded = false;
        var callbackIdleSettled = false;
        var acceptedCallbacksZero = false;
        var client = new Thread(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(handle);
                var element = root.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.NameProperty, "Review command status"));
                Assert.NotNull(element);
                var nativeRuntimeId = CaptureRuntimeId(element);
                diagnostics.Record("setup-native", sender: element, runtimeId: nativeRuntimeId);
                var statuses = new BlockingCollection<PropertyNativeDelivery>(boundedCapacity: 256);
                var callbackIdle = new ManualResetEventSlim(initialState: true);
                ExceptionDispatchInfo? callbackFailure = null;
                var callbackGate = new object();
                var acceptingCallbacks = true;
                var activeCallbacks = 0;
                var handlerRegistered = false;
                AutomationPropertyChangedEventHandler handler = (sender, eventArgs) =>
                {
                    var entryTimestamp = Stopwatch.GetTimestamp();
                    lock (callbackGate)
                    {
                        if (!acceptingCallbacks) return;
                        activeCallbacks++;
                        callbackIdle.Reset();
                    }
                    try
                    {
                        var runtimeId = CaptureCachedRuntimeId(sender, out var cacheFailure);
                        diagnostics.Record("uia-callback-property", sender: sender,
                            runtimeId: runtimeId, propertyId: eventArgs.Property.Id,
                            oldValue: eventArgs.OldValue, newValue: eventArgs.NewValue,
                            timestamp: entryTimestamp);
                        if (cacheFailure is not null)
                        {
                            diagnostics.Invalidate();
                            diagnostics.Record("uia-callback-property-runtime-cache-missing",
                                payload: cacheFailure, sender: sender, propertyId: eventArgs.Property.Id,
                                oldValue: eventArgs.OldValue, newValue: eventArgs.NewValue);
                        }
                        if (!statuses.TryAdd(new(runtimeId, eventArgs.Property.Id,
                                eventArgs.OldValue, eventArgs.NewValue)))
                            diagnostics.InvalidateOverflow();
                    }
                    catch (Exception error)
                    {
                        Interlocked.CompareExchange(ref callbackFailure,
                            ExceptionDispatchInfo.Capture(error), null);
                    }
                    finally
                    {
                        lock (callbackGate)
                        {
                            activeCallbacks--;
                            if (activeCallbacks == 0) callbackIdle.Set();
                        }
                    }
                };
                ExceptionDispatchInfo? operationFailure = null;
                ExceptionDispatchInfo? removalFailure = null;
                try
                {
                    diagnostics.Record("registration-property-begin", sender: element, runtimeId: nativeRuntimeId);
                    var cacheRequest = CreateRuntimeIdCacheRequest();
                    using (cacheRequest.Activate())
                        Automation.AddAutomationPropertyChangedEventHandler(element, TreeScope.Element,
                            handler, AutomationElement.ItemStatusProperty);
                    handlerRegistered = true;
                    diagnostics.Record("registration-property-return", sender: element, runtimeId: nativeRuntimeId);
                    Task? published = null;
                    diagnostics.Record("publication-dispatch-begin");
                    var publication = dispatcher.BeginInvoke(new Action(() =>
                    {
                        diagnostics.Record("publication-sta-begin");
                        published = publish();
                        diagnostics.Record("publication-sta-return");
                    }));
                    diagnostics.Record("publication-dispatch-return");
                    publication.Task.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    Assert.NotNull(published);
                    diagnostics.Record("command-task-await-begin");
                    published.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    diagnostics.Record("command-task-await-return");
                    var diagnosticStarted = Stopwatch.GetTimestamp();
                    var diagnosticLimit = TimeSpan.FromMilliseconds(8250);
                    var capture = new ItemStatusTransitionCapture(
                        nativeRuntimeId ?? throw new InvalidOperationException("Expected native runtime ID is missing."),
                        AutomationElement.ItemStatusProperty.Id, expectedPayloads);
                    ExceptionDispatchInfo? firstAssertionFailure = null;
                    while (Stopwatch.GetElapsedTime(diagnosticStarted) < diagnosticLimit)
                    {
                        var remaining = diagnosticLimit - Stopwatch.GetElapsedTime(diagnosticStarted);
                        if (remaining <= TimeSpan.Zero || !statuses.TryTake(out var delivery, remaining)) break;
                        try
                        {
                            capture.Record(delivery.RuntimeId, delivery.PropertyId, delivery.OldValue, delivery.NewValue);
                        }
                        catch (Exception error)
                        {
                            firstAssertionFailure ??= ExceptionDispatchInfo.Capture(error);
                        }
                    }
                    try { capture.Complete(); }
                    catch (Exception error) { firstAssertionFailure ??= ExceptionDispatchInfo.Capture(error); }
                    operationFailure = firstAssertionFailure;
                }
                catch (Exception error)
                {
                    operationFailure ??= ExceptionDispatchInfo.Capture(error);
                }
                finally
                {
                    lock (callbackGate) acceptingCallbacks = false;
                    try
                    {
                        diagnostics.Record("removal-property-begin", sender: element, runtimeId: nativeRuntimeId);
                        if (handlerRegistered)
                            Automation.RemoveAutomationPropertyChangedEventHandler(element, handler);
                        removalSucceeded = handlerRegistered;
                        diagnostics.Record("removal-property-return", sender: element, runtimeId: nativeRuntimeId);
                    }
                    catch (Exception error)
                    {
                        removalFailure = ExceptionDispatchInfo.Capture(error);
                    }
                    diagnostics.Record("quiescence-property-begin");
                    callbackIdleSettled = callbackIdle.Wait(TimeSpan.FromSeconds(2));
                    lock (callbackGate) acceptedCallbacksZero = activeCallbacks == 0;
                    diagnostics.Record("quiescence-property-return",
                        payload: $"idle={callbackIdleSettled}; acceptedCallbacksZero={acceptedCallbacksZero}");
                    if (callbackIdleSettled && acceptedCallbacksZero)
                    {
                        statuses.Dispose();
                        callbackIdle.Dispose();
                    }
                }
                originalFailure = operationFailure;
                diagnosticFailure = callbackFailure ?? removalFailure;
                if ((!callbackIdleSettled || !acceptedCallbacksZero || !removalSucceeded)
                    && diagnosticFailure is null)
                    diagnosticFailure = ExceptionDispatchInfo.Capture(new IsolatedScenarioFailureException(
                        "The UIA ItemStatus callback did not settle safely after unregistration."));
            }
            catch (Exception error)
            {
                diagnosticFailure ??= ExceptionDispatchInfo.Capture(error);
            }
            finally
            {
                stopOperation = dispatcher.BeginInvoke(new Action(() => frame.Continue = false));
            }
        }) { IsBackground = true };
        client.SetApartmentState(ApartmentState.MTA);
        client.Start();
        Dispatcher.PushFrame(frame);
        var clientJoined = client.Join(TimeSpan.FromSeconds(2));
        var stopCompleted = false;
        if (clientJoined && stopOperation is not null)
        {
            try
            {
                stopOperation.Task.GetAwaiter().GetResult();
                stopCompleted = stopOperation.Status == DispatcherOperationStatus.Completed;
            }
            catch (Exception error)
            {
                diagnosticFailure ??= ExceptionDispatchInfo.Capture(error);
            }
        }
        if (!clientJoined && diagnosticFailure is null)
            diagnosticFailure = ExceptionDispatchInfo.Capture(new IsolatedScenarioFailureException(
                "The UIA property-announcement client did not settle."));
        diagnostics.Record("settlement-property",
            payload: $"removalSucceeded={removalSucceeded}; callbackIdleSettled={callbackIdleSettled}; "
                + $"acceptedCallbacksZero={acceptedCallbacksZero}; "
                + $"clientJoined={clientJoined}; stopCompleted={stopCompleted}");
        return new(originalFailure, diagnosticFailure, removalSucceeded, callbackIdleSettled,
            acceptedCallbacksZero, clientJoined, stopCompleted);
    }

    private static void EnableProductionDiagnostics(ReviewAnnouncementTextBlock status) =>
        GetDiagnosticMethod(nameof(EnableProductionDiagnostics), "EnableDiagnosticCapture").Invoke(status, null);

    private static object StopProductionDiagnostics(ReviewAnnouncementTextBlock status) =>
        GetDiagnosticMethod(nameof(StopProductionDiagnostics), "StopDiagnosticCapture").Invoke(status, null)
        ?? throw new InvalidOperationException("The production diagnostic snapshot was unavailable.");

    private static object[] ReadDiagnosticRecords(object snapshot) =>
        ((snapshot.GetType().GetProperty("Records")?.GetValue(snapshot) as Array)
            ?? throw new InvalidOperationException("The production diagnostic records were unavailable."))
        .Cast<object>()
        .ToArray();

    private static MethodInfo GetDiagnosticMethod(string caller, string methodName) =>
        typeof(ReviewAnnouncementTextBlock).GetMethod(methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"{caller} could not find {methodName}.");

    private static void RecordSetupPeer(
        ReviewAnnouncementTextBlock status,
        UiaDiagnosticLedger diagnostics)
    {
        var peer = UIElementAutomationPeer.FromElement(status)
            ?? UIElementAutomationPeer.CreatePeerForElement(status);
        diagnostics.Record("setup-peer", sender: peer);
    }

    private static int[]? CaptureRuntimeId(object? sender) =>
        sender is AutomationElement element ? element.GetRuntimeId() : null;

    private static CacheRequest CreateRuntimeIdCacheRequest()
    {
        var request = new CacheRequest { AutomationElementMode = AutomationElementMode.Full };
        request.Add(AutomationElement.RuntimeIdProperty);
        return request;
    }

    private static int[]? CaptureCachedRuntimeId(object? sender, out string? failure)
    {
        try
        {
            if (sender is not AutomationElement element)
                throw new InvalidOperationException("The UIA callback sender was not an AutomationElement.");
            var cached = element.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty);
            if (cached is not int[] runtimeId)
                throw new InvalidOperationException(
                    $"The cached RuntimeId value had type {cached?.GetType().FullName ?? "<null>"}.");
            failure = null;
            return runtimeId;
        }
        catch (Exception error)
        {
            failure = error.GetType().FullName + ": " + error.Message;
            return null;
        }
    }

    private static bool SameLiveDelivery(LiveNativeDelivery left, LiveNativeDelivery right) =>
        SameRuntimeId(left.RuntimeId, right.RuntimeId)
        && left.EventId == right.EventId
        && string.Equals(left.Payload, right.Payload, StringComparison.Ordinal);

    private static bool SameRuntimeId(int[]? left, int[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private static bool ProductionDiagnosticsInvalid(object? snapshot)
    {
        if (snapshot is null) return true;
        var snapshotType = snapshot.GetType();
        var capacityOverflow = (bool)(snapshotType.GetProperty("CapacityOverflow")?.GetValue(snapshot)
            ?? throw new InvalidOperationException("The production diagnostic capacity flag was unavailable."));
        var wrongThread = (bool)(snapshotType.GetProperty("WrongThread")?.GetValue(snapshot)
            ?? throw new InvalidOperationException("The production diagnostic thread flag was unavailable."));
        return capacityOverflow || wrongThread;
    }

    [Theory]
    [InlineData("Queue", false)]
    [InlineData("Execute", false)]
    [InlineData("PropertyRaiseReturn", false)]
    [InlineData("LiveRaiseReturn", false)]
    [InlineData("Queue", true)]
    [InlineData("Execute", true)]
    [InlineData("PropertyRaiseReturn", true)]
    [InlineData("LiveRaiseReturn", true)]
    public void ProductProofRejectsDuplicatedOrMissingStages(string stage, bool remove)
    {
        var diagnostics = new UiaDiagnosticLedger();
        diagnostics.Record("vm-notification", 1, "first");
        diagnostics.Record("vm-notification", 2, "second");
        var records = new List<ProductionProofFixtureRecord>();
        foreach (var sequence in new[] { 1L, 2L })
            foreach (var kind in new[] { "Queue", "Execute", "PropertyRaiseReturn", "LiveRaiseReturn" })
                records.Add(new(kind, sequence));

        // Validate the unmodified fixture first so an unrelated fixture error cannot satisfy the negative case.
        AssertProductAnnouncementCapture(new { Records = records.ToArray() }, diagnostics, ["first", "second"]);
        var index = records.FindIndex(record => record.Stage == stage);
        if (remove) records.RemoveAt(index);
        else records.Insert(index, records[index]);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertProductAnnouncementCapture(new { Records = records.ToArray() }, diagnostics, ["first", "second"]));
    }

    private sealed record ProductionProofFixtureRecord(string Stage, long NotificationSequence);

    private static void AssertProductAnnouncementCapture(
        object? productionSnapshot,
        UiaDiagnosticLedger diagnostics,
        string[] expectedPayloads)
    {
        if (productionSnapshot is null)
            throw new InvalidOperationException("The production diagnostic snapshot was unavailable.");
        var viewModelNotifications = diagnostics.Snapshot()
            .Where(record => record.Stage == "vm-notification")
            .ToArray();
        Assert.Equal(expectedPayloads.Length, viewModelNotifications.Length);
        for (var index = 0; index < expectedPayloads.Length; index++)
            Assert.Equal(expectedPayloads[index],
                Assert.IsType<string>(viewModelNotifications[index].Payload));

        var snapshotType = productionSnapshot.GetType();
        var rawRecords = snapshotType.GetProperty("Records")?.GetValue(productionSnapshot) as Array
            ?? throw new InvalidOperationException("The production diagnostic records were unavailable.");
        var productionRecords = new ProductionCaptureRecord[rawRecords.Length];
        for (var index = 0; index < rawRecords.Length; index++)
        {
            var record = rawRecords.GetValue(index)
                ?? throw new InvalidOperationException("A production diagnostic record was unavailable.");
            var recordType = record.GetType();
            productionRecords[index] = new(
                index,
                GetDiagnosticValue(recordType, record, "Stage")?.ToString()
                    ?? throw new InvalidOperationException("A production diagnostic stage was unavailable."),
                (long)(GetDiagnosticValue(recordType, record, "NotificationSequence")
                    ?? throw new InvalidOperationException("A production notification sequence was unavailable.")));
        }

        var queued = productionRecords.Where(record => record.Stage == "Queue").ToArray();
        var executed = productionRecords.Where(record => record.Stage == "Execute").ToArray();
        var propertyReturned = productionRecords
            .Where(record => record.Stage == "PropertyRaiseReturn").ToArray();
        var liveReturned = productionRecords.Where(record => record.Stage == "LiveRaiseReturn").ToArray();
        Assert.DoesNotContain(productionRecords, record => record.Stage == "Skip");
        Assert.Equal(viewModelNotifications.Length, queued.Length);
        Assert.Equal(viewModelNotifications.Length, executed.Length);
        Assert.Equal(viewModelNotifications.Length, propertyReturned.Length);
        Assert.Equal(viewModelNotifications.Length, liveReturned.Length);

        long previousSequence = 0;
        for (var index = 0; index < viewModelNotifications.Length; index++)
        {
            var notification = viewModelNotifications[index];
            var sequence = notification.NotificationSequence;
            Assert.True(sequence > previousSequence, "View-model notification sequence was not FIFO.");
            Assert.Equal(sequence, queued[index].NotificationSequence);
            Assert.Equal(sequence, executed[index].NotificationSequence);
            Assert.Equal(sequence, propertyReturned[index].NotificationSequence);
            Assert.Equal(sequence, liveReturned[index].NotificationSequence);
            Assert.True(queued[index].Index < executed[index].Index
                && executed[index].Index < propertyReturned[index].Index
                && propertyReturned[index].Index < liveReturned[index].Index,
                $"Production announcement {sequence} did not complete in queue/property/live order.");
            previousSequence = sequence;
        }
    }

    private static string FormatDiagnostics(
        Exception? error,
        Exception? diagnosticError,
        object? productionSnapshot,
        UiaDiagnosticLedger diagnostics)
    {
        var text = new StringBuilder();
        text.AppendLine(error is null
            ? "diagnostic-inconclusive: original assertion did not reproduce"
            : "original assertion failure: " + error);
        text.Append("diagnosticFailure=").AppendLine(
            diagnosticError is null ? "<null>" : diagnosticError.ToString());
        text.Append("diagnosticInvalid=")
            .AppendLine((diagnostics.Invalid || ProductionDiagnosticsInvalid(productionSnapshot)).ToString());
        text.AppendLine("PRODUCTION DIAGNOSTICS:");
        if (productionSnapshot is null)
        {
            text.AppendLine("<snapshot unavailable>");
        }
        else
        {
            var snapshotType = productionSnapshot.GetType();
            text.Append("capacityOverflow=")
                .Append(snapshotType.GetProperty("CapacityOverflow")?.GetValue(productionSnapshot))
                .Append(" wrongThread=")
                .Append(snapshotType.GetProperty("WrongThread")?.GetValue(productionSnapshot))
                .Append(" ownerThread=").AppendLine(
                    snapshotType.GetProperty("OwnerThreadId")?.GetValue(productionSnapshot)?.ToString());
            var records = snapshotType.GetProperty("Records")?.GetValue(productionSnapshot) as Array
                ?? throw new InvalidOperationException("The production diagnostic records were unavailable.");
            foreach (var record in records)
            {
                Assert.NotNull(record);
                var recordType = record.GetType();
                text.Append("stage=").Append(GetDiagnosticValue(recordType, record, "Stage"))
                    .Append(" notification=").Append(GetDiagnosticValue(recordType, record, "NotificationSequence"))
                    .Append(" ticks=").Append(GetDiagnosticValue(recordType, record, "Timestamp"))
                    .Append(" thread=").Append(GetDiagnosticValue(recordType, record, "ThreadId"))
                    .Append(" dataContextGeneration=").Append(GetDiagnosticValue(recordType, record, "DataContextGeneration"))
                    .Append(" skipReason=").Append(FormatDiagnosticValue(GetDiagnosticValue(recordType, record, "SkipReason")))
                    .Append(" peerId=").Append(GetDiagnosticValue(recordType, record, "PeerIdentity"))
                    .Append(" expectedPeerId=").AppendLine(
                        GetDiagnosticValue(recordType, record, "ExpectedPeerIdentity")?.ToString());
            }
        }
        text.AppendLine("TEST DIAGNOSTICS:");
        diagnostics.FormatInto(text);
        return text.ToString();
    }

    private static object? GetDiagnosticValue(Type type, object instance, string propertyName) =>
        type.GetProperty(propertyName)?.GetValue(instance)
        ?? (type.GetProperty(propertyName) is null
            ? throw new InvalidOperationException($"The production diagnostic {propertyName} value was unavailable.")
            : null);

    private sealed class UiaDiagnosticLedger
    {
        private const int Capacity = 256;
        private readonly UiaDiagnosticRecord[] records = new UiaDiagnosticRecord[Capacity];
        private int writeIndex;
        private int overflow;
        private int invalid;

        internal bool Overflow => Volatile.Read(ref overflow) != 0;
        internal bool Invalid => Overflow || Volatile.Read(ref invalid) != 0;

        internal void InvalidateOverflow() => Volatile.Write(ref overflow, 1);
        internal void Invalidate() => Volatile.Write(ref invalid, 1);

        internal UiaDiagnosticRecord[] Snapshot()
        {
            var count = Math.Min(Volatile.Read(ref writeIndex), records.Length);
            var captured = new UiaDiagnosticRecord[count];
            Array.Copy(records, captured, count);
            return captured;
        }

        internal void Record(
            string stage,
            long notificationSequence = 0,
            string? payload = null,
            object? sender = null,
            int[]? runtimeId = null,
            int eventId = 0,
            int propertyId = 0,
            object? oldValue = null,
            object? newValue = null,
            long? timestamp = null)
        {
            var slot = Interlocked.Increment(ref writeIndex) - 1;
            if (slot >= records.Length)
            {
                Volatile.Write(ref overflow, 1);
                return;
            }
            records[slot] = new(
                slot + 1L,
                timestamp ?? Stopwatch.GetTimestamp(),
                stage,
                Environment.CurrentManagedThreadId,
                notificationSequence,
                payload,
                sender?.GetType(),
                sender is null ? 0 : RuntimeHelpers.GetHashCode(sender),
                runtimeId,
                eventId,
                propertyId,
                oldValue,
                newValue);
        }

        internal void FormatInto(StringBuilder text)
        {
            var count = Math.Min(Volatile.Read(ref writeIndex), records.Length);
            text.Append("overflow=").Append(Overflow).Append(" invalid=").Append(Invalid)
                .Append(" count=").AppendLine(count.ToString(CultureInfo.InvariantCulture));
            for (var index = 0; index < count; index++)
            {
                var record = records[index];
                text.Append("seq=").Append(record.Sequence.ToString(CultureInfo.InvariantCulture))
                    .Append(" ticks=").Append(record.Timestamp.ToString(CultureInfo.InvariantCulture))
                    .Append(" stage=").Append(record.Stage)
                    .Append(" thread=").Append(record.ThreadId.ToString(CultureInfo.InvariantCulture))
                    .Append(" notification=").Append(record.NotificationSequence.ToString(CultureInfo.InvariantCulture))
                    .Append(" payload=").Append(FormatDiagnosticValue(record.Payload))
                    .Append(" runtime=").Append(record.RuntimeType?.FullName ?? "<null>")
                    .Append('#').Append(record.RuntimeIdentity.ToString(CultureInfo.InvariantCulture))
                    .Append(" runtimeId=").Append(record.RuntimeId is null
                        ? "<null>" : "[" + string.Join(",", record.RuntimeId) + "]")
                    .Append(" eventId=").Append(record.EventId.ToString(CultureInfo.InvariantCulture))
                    .Append(" propertyId=").Append(record.PropertyId.ToString(CultureInfo.InvariantCulture))
                    .Append(" old=").Append(FormatDiagnosticValue(record.OldValue))
                    .Append(" new=").AppendLine(FormatDiagnosticValue(record.NewValue));
            }
        }
    }

    private readonly record struct UiaDiagnosticRecord(
        long Sequence,
        long Timestamp,
        string Stage,
        int ThreadId,
        long NotificationSequence,
        string? Payload,
        Type? RuntimeType,
        int RuntimeIdentity,
        int[]? RuntimeId,
        int EventId,
        int PropertyId,
        object? OldValue,
        object? NewValue);

    private readonly record struct ProductionCaptureRecord(
        int Index,
        string Stage,
        long NotificationSequence);

    private readonly record struct LiveNativeDelivery(
        int[]? RuntimeId,
        int EventId,
        string Payload);

    private readonly record struct PropertyNativeDelivery(
        int[]? RuntimeId,
        int PropertyId,
        object? OldValue,
        object? NewValue);

    private sealed record UiaDiagnosticSettlement(
        ExceptionDispatchInfo? OriginalFailure,
        ExceptionDispatchInfo? DiagnosticFailure,
        bool RemovalSucceeded,
        bool CallbackIdleSettled,
        bool AcceptedCallbacksZero,
        bool ClientJoined,
        bool StopOperationCompleted)
    {
        internal bool SnapshotSafe =>
            RemovalSucceeded && CallbackIdleSettled && AcceptedCallbacksZero && ClientJoined;
    }

    private static string FormatDiagnosticValue(object? value) => value switch
    {
        null => "<null>",
        string { Length: 0 } => "<empty>",
        string text => '"' + text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal) + '"',
        _ => value.ToString() ?? "<null-string>",
    };

    private static void PumpUntil(Func<bool> condition)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition() && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
            WpfReviewSelectionTests.Drain();
        Assert.True(condition(), "The window lifetime did not settle before the two-second deadline.");
    }

    private static Exception? ObserveLifetime(ShellViewModel viewModel)
    {
        var lifetime = viewModel.DisposeAsync().AsTask();
        PumpUntil(() => lifetime.IsCompleted);
        try
        {
            lifetime.GetAwaiter().GetResult();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static Task RunIsolatedAsync(string kind, string methodName,
        Action<WpfScenarioIsolation.IsolationStageWriter> childBody)
    {
        var expectedKind = methodName switch
        {
            nameof(QueuedSelectionSummaryCannotCancelTheOnlyFailureLiveAnnouncement) => "live-duplicate",
            nameof(QueuedFailureAnnouncementsPreserveTheirOriginatingPayloadsOnceAndInOrder) => "item-status",
            nameof(FaultedLifetimeCloseUnwindsBeforeItsFinalCloseAndObservesTheFailure) => "faulted-close",
            _ => throw new InvalidOperationException($"Unknown isolated scenario method: {methodName}"),
        };
        Assert.Equal(expectedKind, kind);
        return WpfScenarioIsolation.RunAsync(typeof(WpfApprovalAccessibilityTests), kind, methodName, childBody);
    }
    private sealed class IsolatedScenarioFailureException(string message) : Exception(message);

    private sealed class MarkerTraceListener(string marker) : TraceListener
    {
        private readonly ConcurrentQueue<string> messages = new();
        public bool SawMarker => messages.Any(message => message.Contains(marker, StringComparison.Ordinal));
        public bool Contains(string value) =>
            messages.Any(message => message.Contains(value, StringComparison.Ordinal));
        public override void Write(string? message)
        {
            if (message is not null) messages.Enqueue(message);
        }
        public override void WriteLine(string? message) => Write(message);
    }
}
