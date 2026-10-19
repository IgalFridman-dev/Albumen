using System.Collections;
using System.Collections.Immutable;
using Albumen.App.Shell;
using Albumen.Application.Action;
using Albumen.Application.Review;
using Albumen.Application.Shell;
using Albumen.Domain.Action;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Domain.Workflow;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

public sealed class ActionProjectionTests
{
    private static readonly long[] mixedSourceOrder = [3, 1, 2];
    [Theory]
    [InlineData(ActionItemState.Waiting, "Waiting", "not requested")]
    [InlineData(ActionItemState.NotRequested, "Not requested", "not requested")]
    [InlineData(ActionItemState.Requested, "Requested", "durable")]
    [InlineData(ActionItemState.InterruptedBeforeExecution, "Interrupted before execution", "Requested")]
    [InlineData(ActionItemState.Executing, "Executing", "uncertain")]
    [InlineData(ActionItemState.Recycled, "Recycled", "original path")]
    [InlineData(ActionItemState.NotRemoved, "Not removed", "unchanged")]
    [InlineData(ActionItemState.UnknownPendingRevalidation, "Unknown pending revalidation", "uncertain")]
    public void EachStateRetainsItsMeaningAndNeverPromisesRecoveredBytes(ActionItemState state, string status, string certainty)
    {
        var fixture = new ActionShellFixture();
        var item = ActionShellFixture.Item(1);
        fixture.Publish(ActionShellFixture.Snapshot(new ActionItemSnapshot(item, Guid.NewGuid(), state, ActionSafetyReason.None)));
        var row = Assert.Single(fixture.Rows);
        Assert.Equal(item.DiscoveredPath, ActionShellFixture.Read<string>(row, "OriginalPath"));
        Assert.Contains(status, ActionShellFixture.Read<string>(row, "StateText"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(certainty, ActionShellFixture.Read<string>(row, "CertaintyText"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows Recycle Bin", ActionShellFixture.Read<string>(row, "AutomationName"));
        Assert.Contains(item.DiscoveredPath, ActionShellFixture.Read<string>(row, "AutomationName"));
        Assert.Contains(status, ActionShellFixture.Read<string>(row, "ItemStatus"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not measured", ActionShellFixture.Read<string>(fixture.ViewModel, "ActionRecoveryNotice"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no automatic", ActionShellFixture.Read<string>(fixture.ViewModel, "ActionReplayNotice"), StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> Reasons() => Enum.GetValues<ActionSafetyReason>().Select(reason => new object[] { reason });

    [Theory]
    [MemberData(nameof(Reasons))]
    public void EveryReasonRemainsInspectableWithoutReclassifyingUnknownAsNotRemoved(ActionSafetyReason reason)
    {
        var fixture = new ActionShellFixture();
        fixture.Publish(ActionShellFixture.Snapshot(new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(),
            ActionItemState.UnknownPendingRevalidation, reason)));
        var row = Assert.Single(fixture.Rows);
        Assert.Equal(reason, ActionShellFixture.Read<ActionSafetyReason>(row, "Reason"));
        Assert.False(string.IsNullOrWhiteSpace(ActionShellFixture.Read<string>(row, "ReasonText")));
        Assert.Contains(ActionShellFixture.Read<string>(row, "ReasonText"), ActionShellFixture.Read<string>(row, "ItemStatus"));
        Assert.DoesNotContain("not removed", ActionShellFixture.Read<string>(row, "StateText"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uncertain", ActionShellFixture.Read<string>(row, "CertaintyText"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MixedOutcomesPreserveSourceOrderDurableCountsAndEarlierProjection()
    {
        var fixture = new ActionShellFixture();
        var first = ActionShellFixture.Snapshot(
            new ActionItemSnapshot(ActionShellFixture.Item(3), Guid.NewGuid(), ActionItemState.Recycled, ActionSafetyReason.None),
            new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(), ActionItemState.UnknownPendingRevalidation, ActionSafetyReason.JournalUnavailable),
            new ActionItemSnapshot(ActionShellFixture.Item(2), null, ActionItemState.NotRequested, ActionSafetyReason.DependentOutcomeUnknown))
            with { Status = ActionStatus.Faulted, DurableRequestedCount = 2, DurableExecutingCount = 2,
                RecycledCount = 1, UnknownCount = 1, FaultReason = ActionSafetyReason.JournalUnavailable };
        fixture.Publish(first);
        var previousRows = fixture.Rows;
        Assert.Equal(mixedSourceOrder, previousRows.Select(row => ActionShellFixture.Read<long>(row, "OccurrenceId")));
        var counts = ActionShellFixture.Read<string>(fixture.ViewModel, "ActionCountsText");
        foreach (var expected in new[] { "3 approved", "2 requested", "2 executing", "1 recycled", "0 not removed", "1 unknown" })
            Assert.Contains(expected, counts, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Journal", ActionShellFixture.Read<string>(fixture.ViewModel, "ActionStatusText"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("uncertain", ActionShellFixture.Read<string>(fixture.ViewModel, "ActionCertaintyText"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dependent", ActionShellFixture.Read<string>(previousRows[2], "ReasonText"), StringComparison.OrdinalIgnoreCase);
        fixture.Publish(first with { Items = first.Items.SetItem(0, first.Items[0] with { State = ActionItemState.NotRemoved }) });
        Assert.Contains("Recycled", ActionShellFixture.Read<string>(previousRows[0], "StateText"));
        Assert.Equal(ActionItemState.Recycled, first.Items[0].State);
    }

    [Fact]
    public void SourceNoticeChangesAtAdmissionAndStaysTruthfulAcrossNavigationAndScopeRefresh()
    {
        var fixture = new ActionShellFixture();
        Assert.Contains("No source change", ActionShellFixture.Read<string>(fixture.ViewModel, "SourceMutationNotice"), StringComparison.OrdinalIgnoreCase);
        var action = ActionShellFixture.Snapshot(new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(),
            ActionItemState.Executing, ActionSafetyReason.None));
        fixture.Publish(action);
        foreach (var phase in new[] { WorkflowPhase.ApproveCleanup, WorkflowPhase.Review, WorkflowPhase.ChooseScope })
        {
            fixture.Publish(action, phase, freshScope: phase == WorkflowPhase.ChooseScope);
            var notice = ActionShellFixture.Read<string>(fixture.ViewModel, "SourceMutationNotice");
            foreach (var expected in new[] { "explicitly approved", "one at a time", "Windows Recycle Bin", "current checks", "durable" })
                Assert.Contains(expected, notice, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("remain unchanged", notice, StringComparison.OrdinalIgnoreCase);
            Assert.Single(fixture.Rows);
        }
    }

    [Fact]
    public void RepeatedSnapshotDoesNotReannounceOrReplaceImmutableRows()
    {
        var fixture = new ActionShellFixture();
        var snapshot = ActionShellFixture.Snapshot(new ActionItemSnapshot(ActionShellFixture.Item(1), Guid.NewGuid(), ActionItemState.Requested, ActionSafetyReason.None));
        fixture.Publish(snapshot);
        var rows = fixture.Rows;
        var notifications = new List<string>();
        fixture.ViewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName ?? "");
        fixture.Publish(snapshot);
        Assert.DoesNotContain("ActionAnnouncement", notifications);
        Assert.DoesNotContain("ActionItems", notifications);
        Assert.Same(rows[0], fixture.Rows[0]);
    }
}

internal sealed class ActionShellFixture : IAlbumenWorkflow
{
    internal ActionShellFixture()
    {
        var review = ReviewData.CreateSnapshot(selected: [1]);
        var existing = new RecordingReviewWorkflow(Scope.Current, review);
        Current = existing.Current;
        ViewModel = new(Scope, this);
    }
    internal ReviewStaticScope Scope { get; } = new();
    internal ShellViewModel ViewModel { get; }
    internal object[] Rows => Read<IEnumerable>(ViewModel, "ActionItems").Cast<object>().ToArray();
    public AlbumenSnapshot Current { get; private set; }
    public event EventHandler<AlbumenSnapshot>? SnapshotChanged;
    internal List<ApproveSelectionCommand> Approvals { get; } = [];
    internal List<Guid> Revalidations { get; } = [];
    internal int Continuations { get; private set; }
    internal static Guid RequestId { get; } = Guid.Parse("95000000-0000-0000-0000-000000000005");
    // approved: the live review is the sealed one for this cleanup, as after a real approval.
    internal void Publish(ActionSnapshot action, WorkflowPhase phase = WorkflowPhase.ApproveCleanup, bool freshScope = false,
        bool approved = false)
    {
        var property = typeof(AlbumenSnapshot).GetProperty("Action");
        Assert.NotNull(property);
        Current = Current with { CurrentPhase = phase,
            Review = Current.Review with { Sealed = true, ApprovedRequest = approved ? Approved(Current.Review) : null },
            Scope = freshScope ? Current.Scope with { Revision = 8 } : Current.Scope };
        property.SetValue(Current, action);
        SnapshotChanged?.Invoke(this, Current);
    }
    internal void PublishScope(ShellSnapshot scope, bool rechecking)
    {
        Current = Current with
        {
            Scope = scope, CurrentPhase = WorkflowPhase.ChooseScope, Rules = null,
            Review = ReviewSnapshot.Closed, IsRecheckingScopeAfterCleanup = rechecking,
        };
        SnapshotChanged?.Invoke(this, Current);
    }
    internal void PublishRecovery(ActionRecoverySnapshot recovery,
        WorkflowPhase phase = WorkflowPhase.ApproveCleanup)
    {
        Current = Current with { CurrentPhase = phase, ActionRecovery = recovery };
        SnapshotChanged?.Invoke(this, Current);
    }
    internal static T Read<T>(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<T>(property.GetValue(instance));
    }
    internal static ApprovedActionItem Item(long id)
    {
        var group = ReviewData.CreateSnapshot().Groups[0];
        var member = group.Members[0].Occurrence;
        var path = @"C:\Photos\" + new string('長', 160) + $"-תצלום-📷-{id}.jpg";
        return new(group.Candidate.GroupId, group.Candidate.GroupRevision, member.Token with { OccurrenceId = id },
            path, @"\\?\" + path, member.PhysicalFileKey!.Value, member.Content!, member.Allocation, null);
    }
    private static ApprovedActionRequest Approved(ReviewSnapshot review) => new(RequestId, DateTimeOffset.UnixEpoch,
        review.Pin!.ReviewSessionId, review.SelectionRevision, review.Pin.Scan,
        new ResolvedScope(new(@"C:\Photos", ScopeRecursion.IncludeSubfolders), @"\\?\C:\Photos", new DirectoryIdentity(7, 0, 70)),
        [Item(1)], [1], new(1, 1, review.Coverage, review.Unresolved, new(512, SelectionRecoveryUnavailableReason.None),
            "Windows Recycle Bin", "Revalidated again before any file operation."));
    internal static ActionSnapshot Snapshot(params ActionItemSnapshot[] rows) =>
        new(RequestId, ActionStatus.Running, [.. rows], 0, 0, 0, 0, 0, ActionSafetyReason.None);
    public Task ContinueAsync() => Task.CompletedTask;
    public Task SetScanOptionsAsync(ScanOptions options) => Task.CompletedTask;
    public Task ConfirmExactRulesAsync() => Task.CompletedTask;
    public Task StartScanAsync() => Task.CompletedTask;
    public Task CancelScanAsync() => Task.CompletedTask;
    public Task OpenReviewAsync() => Task.CompletedTask;
    public Task RevalidateUnknownAsync(Guid requestId, CancellationToken token)
    { Revalidations.Add(requestId); return Task.CompletedTask; }
    public Task NavigateAsync(WorkflowPhase phase) => Task.CompletedTask;
    public Task ContinueReviewAsync() { Continuations++; return Task.CompletedTask; }
    public Task<ReviewCommandResult> ChangeReviewSelectionAsync(SelectionCommand command, CancellationToken token) => throw new NotSupportedException();
    public Task<ReviewCommandResult> AffirmReviewOverrideAsync(OverrideAffirmationCommand command, CancellationToken token) => throw new NotSupportedException();
    public Task<ReviewCommandResult> ApproveReviewAsync(ApproveSelectionCommand command, CancellationToken token)
    { Approvals.Add(command); return Task.FromResult(new ReviewCommandResult(ReviewCommandOutcome.Approved, Current.Review)); }
}
