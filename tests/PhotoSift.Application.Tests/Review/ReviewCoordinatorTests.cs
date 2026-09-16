using System.Collections.Concurrent;
using System.Collections.Immutable;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Application.Tests.Scan;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using Xunit;

namespace PhotoSift.Application.Tests.Review;

public sealed class ReviewCoordinatorTests
{
    [Fact]
    public async Task DataSealPreventsMutationAndPreservesTheImmutableHandoff()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        var session = f.Owner.Current.Pin!;
        var store = new ReviewStore(session, f.Request);
        Assert.True(store.Accept(f.Evidence(1)));
        Assert.True(store.Accept(f.Evidence(2)));
        Assert.True(store.Accept(f.Evidence(3)));
        var selected = store.ChangeSelection(f.Command(SelectionCommandKind.Toggle, 1));
        Assert.Equal(ReviewCommandOutcome.Committed, selected.Outcome);
        var member = f.Request.Scan.Groups[0].Members[0];
        var item = new ApprovedActionItem(f.Request.Scan.Groups[0].GroupId, 1, member.Token,
            member.DiscoveredPath, member.ResolvedPath, member.PhysicalFileKey!.Value, member.Content!, member.Allocation, null);
        var request = new ApprovedActionRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch, session.ReviewSessionId,
            3, session.Scan, f.Request.Scope.Preflight!.Scope!, [item], [1],
            new(1, 1, ScanStatus.Complete, selected.Snapshot.Unresolved, new(4096, SelectionRecoveryUnavailableReason.None),
                "Windows Recycle Bin", "Revalidate again"));
        store.Seal(request);
        var command = f.Command(SelectionCommandKind.Toggle, 2) with
        { Pin = f.Command(SelectionCommandKind.Toggle, 2).Pin with { SelectionRevision = 3 } };
        var result = store.ChangeSelection(command);
        Assert.Equal(ReviewCommandOutcome.Sealed, result.Outcome);
        Assert.Same(request, result.Snapshot.ApprovedRequest);
        Assert.Equal(3, result.Snapshot.SelectionRevision);
        Assert.Equal([1L], result.Snapshot.Groups[0].SelectedOccurrenceIds);
        Assert.True(result.Snapshot.Sealed);
        Assert.True(result.Snapshot.Groups[0].Sealed);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("rules")]
    [InlineData("not-ready")]
    [InlineData("selection")]
    [InlineData("group-pin")]
    [InlineData("row-token")]
    public async Task OpenRejectsInconsistentScopeRulesAndCandidatePinsBeforeExecutorStart(string mismatch)
    {
        await using var f = new ReviewFixture();
        var r = f.Request;
        var group = r.Scan.Groups[0];
        r = mismatch switch
        {
            "scope" => r with { Scope = r.Scope with { Revision = 2 } },
            "rules" => r with { Rules = new(1, 2) },
            "not-ready" => r with { Scope = r.Scope with { ScopeState = ScopeSessionState.Checking } },
            "selection" => r with { Scope = r.Scope with { Selection = new(@"C:\Other", ScopeRecursion.TopLevelOnly) } },
            "group-pin" => r with { Scan = r.Scan with { Groups = [group with { Pin = group.Pin with { AnalysisRevision = 2 } }] } },
            _ => r with { Scan = r.Scan with { Groups = [group with { Members = group.Members.SetItem(0,
                ReviewFixture.Copy(group.Members[0], token: group.Members[0].Token with { OccurrenceRevision = 2 })) }] } },
        };
        Assert.Equal(ReviewCommandOutcome.Invalid, (await f.Open(r)).Outcome);
        Assert.Empty(f.Executor.Runs);
        Assert.Null(f.Owner.Current.Pin);
    }

    [Theory]
    [InlineData("opened-key")]
    [InlineData("final-length")]
    [InlineData("final-path")]
    [InlineData("digest")]
    public async Task MalformedProtectedEvidenceFailsClosedForThePhysicalKey(string mismatch)
    {
        await using var f = new ReviewFixture(aliases: true);
        await f.Open();
        var e = f.Evidence(1);
        e = mismatch switch
        {
            "opened-key" => e with { Opened = e.Opened with { PhysicalFileKey = new(8, new(8, 8)) } },
            "final-length" => e with { Final = e.Final with { ByteLength = 2 } },
            "final-path" => e with { Final = e.Final with { ResolvedPath = @"C:\Other\1.jpg" } },
            _ => e with { Digest = new(1, new string('B', 64)) },
        };
        var ack = f.Owner.CommitRevalidationAsync(e).AsTask();
        await f.Pump(ack);
        Assert.True(await ack);
        Assert.All(f.Owner.Current.Groups[0].Members.Take(2), m => Assert.Equal(ReviewEligibilityState.Unavailable, m.Eligibility));
        Assert.Equal(ReviewEligibilityState.PendingRevalidation, f.Owner.Current.Groups[0].Members[2].Eligibility);
    }

    [Theory]
    [InlineData(ScanStatus.Complete)]
    [InlineData(ScanStatus.Incomplete)]
    public async Task TerminalReviewStartsPendingAndEmptyWithoutCreatingAuthority(ScanStatus coverage)
    {
        await using var f = new ReviewFixture();
        var request = f.Request with { Scan = f.Request.Scan with { Status = coverage } };
        var result = await f.Open(request);
        Assert.Equal(ReviewCommandOutcome.Committed, result.Outcome);
        Assert.Equal(coverage, result.Snapshot.Coverage);
        Assert.Equal(1, result.Snapshot.SelectionRevision);
        Assert.Equal(3, result.Snapshot.Unresolved.PendingReviewMembers);
        Assert.Null(result.Snapshot.ApprovedRequest);
        Assert.False(result.Snapshot.Sealed);
        Assert.All(result.Snapshot.Groups, g =>
        {
            Assert.Empty(g.SelectedOccurrenceIds);
            Assert.All(g.Members, m => Assert.Equal(ReviewEligibilityState.PendingRevalidation, m.Eligibility));
        });
        await f.Run();
    }

    [Theory]
    [InlineData(ScanStatus.Idle)]
    [InlineData(ScanStatus.Checking)]
    [InlineData(ScanStatus.Running)]
    [InlineData(ScanStatus.Faulted)]
    public async Task NonterminalOrFaultedScanCannotStartRevalidation(ScanStatus status)
    {
        await using var f = new ReviewFixture();
        var result = await f.Open(f.Request with { Scan = f.Request.Scan with { Status = status } });
        Assert.Equal(ReviewCommandOutcome.Invalid, result.Outcome);
        Assert.Null(f.Owner.Current.Pin);
        Assert.Empty(f.Executor.Runs);
    }

    [Fact]
    public async Task PendingBulkIsAtomicAndEligibleSelectionAdvancesOnceWhileNeutralRetainsSnapshot()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        var pending = await f.Change(f.Command(SelectionCommandKind.ReplaceFocusedGroup, 1, 2));
        Assert.Equal(ReviewCommandOutcome.Invalid, pending.Outcome);
        Assert.Equal(1, f.Owner.Current.SelectionRevision);
        await f.Eligible(1, 2, 3);
        var prior = f.Owner.Current;
        var selected = await f.Change(f.Command(SelectionCommandKind.ReplaceFocusedGroup, 1, 2));
        Assert.Equal(ReviewCommandOutcome.Committed, selected.Outcome);
        Assert.Equal(2, selected.Snapshot.SelectionRevision);
        Assert.Equal([1L, 2L], Assert.Single(selected.Snapshot.Groups).SelectedOccurrenceIds.Order());
        Assert.Equal(8192, Assert.Single(selected.Snapshot.Groups).Recovery.Bytes);
        Assert.Empty(Assert.Single(prior.Groups).SelectedOccurrenceIds);
        var neutral = await f.Change(f.Command(SelectionCommandKind.ReplaceFocusedGroup, 2, 1));
        Assert.Equal(ReviewCommandOutcome.Neutral, neutral.Outcome);
        Assert.Same(selected.Snapshot, neutral.Snapshot);
        var toggle = await f.Change(f.Command(SelectionCommandKind.Toggle, 1));
        Assert.Equal(3, toggle.Snapshot.SelectionRevision);
        Assert.Equal([2L], Assert.Single(toggle.Snapshot.Groups).SelectedOccurrenceIds);
    }

    [Fact]
    public async Task LastSurvivorReturnsCanonicalProposalWithoutAnyMutationOrPersistence()
    {
        await using var f = new ReviewFixture();
        await f.Open();
        await f.Eligible(1, 2, 3);
        var before = f.Owner.Current;
        var result = await f.Change(f.Command(SelectionCommandKind.ReplaceFocusedGroup, 3, 1, 2));
        Assert.Equal(ReviewCommandOutcome.OverrideRequired, result.Outcome);
        Assert.Same(before, result.Snapshot);
        Assert.Equal([1L, 2L, 3L], result.OverrideProposal!.ProposedSelectedOccurrenceIds);
        Assert.Equal(before.SelectionRevision, result.OverrideProposal.Pin.SelectionRevision);
        Assert.Equal(SurvivorPolicy.Version, result.OverrideProposal.SurvivorPolicyVersion);
        Assert.Null(result.ApprovedRequest);
    }

    [Theory]
    [InlineData("review")]
    [InlineData("scan")]
    [InlineData("scope")]
    [InlineData("rules")]
    [InlineData("analysis")]
    [InlineData("group")]
    [InlineData("group-revision")]
    [InlineData("selection")]
    [InlineData("occurrence")]
    public async Task EntireStaleSelectionRejectsWithoutPartialCommit(string mismatch)
    {
        await using var f = new ReviewFixture();
        await f.Open();
        await f.Eligible(1, 2, 3);
        var command = f.Command(SelectionCommandKind.ReplaceFocusedGroup, 1, 2);
        var pin = command.Pin;
        command = mismatch switch
        {
            "review" => command with { Pin = pin with { Session = pin.Session with { ReviewSessionId = Guid.NewGuid() } } },
            "scan" => command with { Pin = pin with { Session = pin.Session with { Scan = pin.Session.Scan with { ScanId = Guid.NewGuid() } } } },
            "scope" => command with { Pin = pin with { Session = pin.Session with { Scan = pin.Session.Scan with { ScopeRevision = 2 } } } },
            "rules" => command with { Pin = pin with { Session = pin.Session with { Scan = pin.Session.Scan with { RuleSetRevision = 2 } } } },
            "analysis" => command with { Pin = pin with { Session = pin.Session with { Scan = pin.Session.Scan with { AnalysisRevision = 2 } } } },
            "group" => command with { Pin = pin with { GroupId = Guid.NewGuid() } },
            "group-revision" => command with { Pin = pin with { GroupRevision = 99 } },
            "selection" => command with { Pin = pin with { SelectionRevision = 99 } },
            _ => command with { OccurrenceIds = [1, 999] },
        };
        var before = f.Owner.Current;
        var result = await f.Change(command);
        Assert.Equal(ReviewCommandOutcome.Stale, result.Outcome);
        Assert.Same(before, result.Snapshot);
    }

    [Theory]
    [InlineData("attempt")]
    [InlineData("occurrence-revision")]
    [InlineData("occurrence-attempt")]
    [InlineData("group-revision")]
    [InlineData("context")]
    [InlineData("content")]
    public async Task StaleEvidenceCannotChangeEligibility(string mismatch)
    {
        await using var f = new ReviewFixture();
        await f.Open();
        var evidence = f.Evidence(1);
        var r = evidence.Request;
        r = mismatch switch
        {
            "attempt" => r with { Attempt = 2 },
            "occurrence-revision" => r with { Occurrence = ReviewFixture.Copy(r.Occurrence, token: r.Occurrence.Token with { OccurrenceRevision = 2 }) },
            "occurrence-attempt" => r with { Occurrence = ReviewFixture.Copy(r.Occurrence, token: r.Occurrence.Token with { Attempt = 2 }) },
            "group-revision" => r with { GroupRevision = 2 },
            "context" => r with { Context = r.Context with { Scope = r.Context.Scope with { RootIdentity = new(9, 9, 9) } } },
            _ => r with { Occurrence = ReviewFixture.Copy(r.Occurrence, content: new(r.Occurrence.PhysicalFileKey!.Value, 2, ScanTestData.Digest)) },
        };
        var before = f.Owner.Current;
        var ack = f.Owner.CommitRevalidationAsync(evidence with { Request = r }).AsTask();
        await f.Pump(ack);
        Assert.False(await ack);
        Assert.Same(before, f.Owner.Current);
    }

    [Fact]
    public async Task PhysicalFailureAtomicallyUnselectsAllAliasesAndPreservesOtherMembers()
    {
        await using var f = new ReviewFixture(aliases: true);
        await f.Open();
        await f.Eligible(1, 3);
        await f.Change(f.Command(SelectionCommandKind.Toggle, 1));
        var before = f.Owner.Current;
        var ack = f.Owner.FailRevalidationAsync(f.Evidence(2).Request, ReviewUnavailableReason.Changed).AsTask();
        await f.Pump(ack);
        Assert.True(await ack);
        var group = Assert.Single(f.Owner.Current.Groups);
        Assert.All(group.Members.Take(2), m =>
        {
            Assert.Equal(ReviewEligibilityState.Unavailable, m.Eligibility);
            Assert.Equal(ReviewUnavailableReason.Changed, m.Reason);
            Assert.False(m.Selected);
        });
        Assert.Equal(ReviewEligibilityState.Eligible, group.Members[2].Eligibility);
        Assert.Empty(group.SelectedOccurrenceIds);
        Assert.Equal(before.SelectionRevision + 1, f.Owner.Current.SelectionRevision);
        Assert.Equal(2, f.Owner.Current.Unresolved.UnavailableReviewMembers);
        Assert.Equal(1, f.Owner.Current.Unresolved.UnselectedSurvivorMembers);
        Assert.Equal(2, f.Owner.Current.Unresolved.Total);
        var late = f.Owner.CommitRevalidationAsync(f.Evidence(2)).AsTask();
        await f.Pump(late);
        Assert.False(await late);
    }
}

internal sealed class ReviewFixture : IAsyncDisposable
{
    internal ManualScanScheduler Scheduler { get; } = new();
    internal ReviewExecutor Executor { get; } = new();
    internal ReviewCoordinator Owner { get; }
    internal ReviewOpenRequest Request { get; }
    internal ReviewFixture(bool aliases = false)
    {
        var context = ScanTestData.Context();
        var members = Enumerable.Range(1, 3).Select(id => ReviewData.Request(id, context).Occurrence).ToImmutableArray();
        if (aliases)
        {
            members = members.SetItem(0, Copy(members[0], allocation: new(4096, 2, true)));
            members = members.SetItem(1, Copy(members[1], key: members[0].PhysicalFileKey,
                content: members[0].Content, allocation: members[0].Allocation));
        }
        var group = new CandidateGroup(Guid.NewGuid(), 1, context.Pin, CandidateCategory.Exact, members, [], false, null, new(99999, RecoveryUnavailableReason.None));
        var evidence = new ScopePreflightEvidence(context.Scope, ScopeVolumeKind.Fixed, ScopeCloudState.Local,
            ScopeTraversalState.Complete, new(1, 0, 0, 0), new(0, 0));
        var scope = new ShellSnapshot([], "Ready", ScopeSessionState.Ready, context.Scope.Selection,
            ScopePreflightPolicy.Evaluate(evidence), Revision: 1);
        Request = new(scope, new(1, 1), new(context.Pin, ScanStatus.Complete, ScanTestData.Success, members, [group], new(3, 3, 0, 0, 0, 0)));
        Owner = new(Scheduler, Executor, new RejectEvidenceStore(), resultCapacity: 1);
    }
    internal Task Pump(Task task) => ScanTestData.Bound(Scheduler.PumpUntilAsync(task));
    internal static ScanFileOccurrence Copy(ScanFileOccurrence member, OccurrenceToken? token = null,
        PhysicalFileKey? key = null, ContentRevision? content = null, AllocationEvidence? allocation = null) =>
        new(token ?? member.Token, member.DiscoveredPath, member.ResolvedPath, key ?? member.PhysicalFileKey,
            member.Enumerate, member.Exact, content ?? member.Content, allocation ?? member.Allocation);
    internal async Task<ReviewCommandResult> Open(ReviewOpenRequest? request = null)
    {
        var task = Owner.OpenAsync(request ?? Request, TestContext.Current.CancellationToken);
        await Pump(task);
        return await task;
    }
    internal async Task<ReviewTestRun> Run()
    {
        Assert.NotNull(Owner.Current.Pin);
        await Pump(Executor.Arrived.Task);
        return await Executor.Arrived.Task;
    }
    internal SelectionCommand Command(SelectionCommandKind kind, params long[] ids)
    {
        var snapshot = Owner.Current;
        var group = snapshot.Groups[0].Candidate;
        return new(new(snapshot.Pin!, group.GroupId, group.GroupRevision, snapshot.SelectionRevision), kind, [.. ids], Guid.NewGuid());
    }
    internal ReviewRevalidationEvidence Evidence(long id)
    {
        var group = Request.Scan.Groups[0];
        var member = group.Members.Single(m => m.Token.OccurrenceId == id);
        var request = new ReviewRevalidationRequest(Owner.Current.Pin!, new(Request.Scan.Pin!, Request.Scope.Preflight!.Scope!), group.GroupId, group.GroupRevision, member, 1);
        var facts = new ProtectedFileFacts(member.PhysicalFileKey!.Value, 1, member.ResolvedPath, null, member.Allocation);
        return new(request, facts, facts, new(1, ScanTestData.Digest));
    }
    internal async Task Eligible(params long[] ids)
    {
        await Run();
        foreach (var id in ids)
        {
            var task = Owner.CommitRevalidationAsync(Evidence(id)).AsTask();
            await Pump(task);
            Assert.True(await task);
        }
    }
    internal async Task<ReviewCommandResult> Change(SelectionCommand command)
    {
        var task = Owner.ChangeSelectionAsync(command, TestContext.Current.CancellationToken);
        await Pump(task);
        return await task;
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var run in Executor.Runs) run.Done.TrySetResult();
        await Pump(Owner.DisposeAsync().AsTask());
    }
    private sealed class RejectEvidenceStore : IOverrideEvidenceStore
    {
        public ValueTask<OverridePersistResult> PersistAsync(DeliberateOverrideEvidence evidence, CancellationToken cancellationToken) => throw new InvalidOperationException("Task 4 must not persist");
        public ValueTask<OverrideReadResult> ReadAsync(OverrideEvidenceId evidenceId, CancellationToken cancellationToken) => throw new InvalidOperationException("Task 4 must not read override authority");
    }
}

internal sealed class ReviewExecutor : IReviewRevalidationExecutor
{
    internal ConcurrentQueue<ReviewTestRun> Runs { get; } = new();
    internal TaskCompletionSource<ReviewTestRun> Arrived { get; set; } = ScanTestData.Signal<ReviewTestRun>();
    public IReviewRun Start(ReviewSessionPin session, ScanRunContext context, ImmutableArray<CandidateGroup> groups, IReviewResultSink sink)
    {
        var run = new ReviewTestRun(session, sink);
        Runs.Enqueue(run);
        Arrived.TrySetResult(run);
        return run;
    }
}

internal sealed class ReviewTestRun(ReviewSessionPin pin, IReviewResultSink sink) : IReviewRun
{
    internal ReviewSessionPin Pin { get; } = pin;
    internal IReviewResultSink Sink { get; } = sink;
    internal TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Func<Task>? OnCancel { get; set; }
    internal Exception? DisposalFailure { get; set; }
    public Task Completion => Done.Task;
    public Task CancelAsync() { Done.TrySetResult(); return OnCancel?.Invoke() ?? Task.CompletedTask; }
    public ValueTask DisposeAsync()
    {
        Disposed.TrySetResult();
        return DisposalFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DisposalFailure);
    }
}
