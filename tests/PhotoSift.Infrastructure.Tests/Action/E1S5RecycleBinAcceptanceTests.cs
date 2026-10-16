using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoSift.Analysis.Exact;
using PhotoSift.Application.Action;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Sqlite;
using PhotoSift.Infrastructure.Tests.Exact;
using PhotoSift.Infrastructure.Windows.Action;
using PhotoSift.Infrastructure.Windows.Scan;
using Xunit;
using static PhotoSift.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace PhotoSift.Infrastructure.Tests.ActionExecution;

[Collection(WindowsRecycleBinIsolationDefinition.Name)]
public sealed class E1S5RecycleBinAcceptanceTests
{
    private static readonly JsonSerializerOptions journalOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public async Task ExactSealedOriginalRecyclesOnceWhileTerminalCommitCancellationPreservesItsHardLinkAndEvidence()
    {
        var terminalEntered = RealActionAcceptanceFixture.Signal();
        var releaseTerminal = RealActionAcceptanceFixture.Signal();
        var inserts = 0;
        var observer = new SqliteDatabaseObserver(AfterActionInsert: async (_, _, token) =>
        {
            Assert.False(token.CanBeCanceled);
            if (Interlocked.Increment(ref inserts) != 3) return;
            terminalEntered.TrySetResult();
            await releaseTerminal.Task;
        });
        await using var fixture = new RealActionAcceptanceFixture(observer, hardLink: true);
        var request = await fixture.ApproveReviewAsync(1);
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Action.ApproveAsync(request, cancellation.Token);
        try
        {
            await RealActionAcceptanceFixture.Bound(terminalEntered.Task);
            Assert.False(pending.IsCompleted);
            Assert.Equal(ActionItemState.Executing, fixture.Action.Current.Items[0].State);
            Assert.False(File.Exists(fixture.Selected));
            Assert.Equal("fixture", File.ReadAllText(fixture.Other));
            Assert.Equal("fixture", File.ReadAllText(fixture.Witness));
            await using (var read = await fixture.OpenRawAsync())
            {
                Assert.Equal(1L, await ScalarAsync(read, "SELECT COUNT(*) FROM action_items;"));
                Assert.Equal(1L, await ScalarAsync(read, "SELECT COUNT(*) FROM action_transitions WHERE sequence=2;"));
                Assert.Equal(0L, await ScalarAsync(read, "SELECT COUNT(*) FROM action_transitions WHERE sequence=3;"));
            }
            cancellation.Cancel();
            var repeated = await RealActionAcceptanceFixture.Bound(fixture.Action.ApproveAsync(request, CancellationToken.None));
            Assert.Equal(ActionCommandOutcome.Existing, repeated.Outcome);
            releaseTerminal.TrySetResult();
            var result = await RealActionAcceptanceFixture.Bound(pending);
            Assert.Equal(ActionItemState.Recycled, Assert.Single(result.Snapshot.Items).State);
            Assert.Equal(1L, result.Snapshot.RecycledCount);
            Assert.Equal(0L, result.Snapshot.UnknownCount);
            Assert.Same(request, fixture.Executor.LastRequest!.Safety.Claim.Request);
            Assert.Equal(fixture.Selected, fixture.Executor.LastRequest.Safety.Item.DiscoveredPath);
            Assert.Equal(1, fixture.Executor.Executions);
            var evidence = Assert.IsType<RecycleBinResult>(fixture.Executor.Result).Evidence;
            Assert.Equal(1, evidence.ValidPreDeleteCount);
            Assert.Equal(0, evidence.UnexpectedPreDeleteCount);
            Assert.True(evidence.NamespaceHandoffOccurred);
            Assert.True(evidence.PerformOperationsEntered);
            Assert.True(evidence.SelectedHandleReleased);
            Assert.True(evidence.PermissionGranted);
            Assert.True(evidence.PerformOperationsHResult >= 0);
            Assert.False(evidence.OperationsAborted);
            Assert.Equal(1, evidence.MatchingPostDeleteCount);
            Assert.Equal(0, evidence.UnexpectedPostDeleteCount);
            Assert.True(evidence.DeleteHResult >= 0);
            Assert.True(evidence.NewlyCreatedItemPresent);
            Assert.False(evidence.CallbackException);
            await using var verify = await fixture.OpenRawAsync();
            Assert.Equal("recycled", await ScalarAsync(verify, "SELECT transition_kind FROM action_transitions WHERE sequence=3;"));
            var payload = Assert.IsType<string>(await ScalarAsync(verify, "SELECT transition_payload FROM action_transitions WHERE sequence=3;"));
            var terminal = Assert.IsType<ActionTerminalFact>(JsonSerializer.Deserialize<ActionTerminalFact>(payload, journalOptions));
            Assert.Equal(fixture.Executor.LastRequest.ActionItemId, terminal.ActionItemId);
            Assert.Equal(ActionTerminalKind.Recycled, terminal.Kind);
            Assert.Equal(evidence, terminal.Evidence);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
                await ScalarAsync(verify, "SELECT transition_payload_sha256 FROM action_transitions WHERE sequence=3;"));
            Assert.Equal(3, inserts);
            fixture.Files.AssertWritable(fixture.Other);
            fixture.Files.AssertWritable(fixture.Witness);
            TestContext.Current.TestOutputHelper?.WriteLine("Real Review seal, Action owner, FULL journal and one-item shell success settled; original absent; hard-link alias and independent witness readable; terminal committed after cancellation; no replay or Recycle Bin enumeration.");
        }
        finally
        {
            releaseTerminal.TrySetResult();
            await RealActionAcceptanceFixture.Bound(pending);
        }
    }
}

// Shared only by the two Task 9 infrastructure suites. Every path is created
// by the existing architecture/full disposable-controller containment fixture.
internal sealed class RealActionAcceptanceFixture : IAsyncDisposable
{
    internal ActionFileFixture Files { get; } = new();
    internal SerialTestScheduler Scheduler { get; } = new();
    internal SqlitePhotoSiftStores Stores { get; }
    internal ReviewCoordinator Review { get; }
    internal ActionCoordinator Action { get; }
    internal AcceptanceExecutor Executor { get; }
    internal string Selected { get; }
    internal string Other { get; }
    internal string Witness { get; }
    internal string DatabasePath { get; }
    private readonly ReviewOpenRequest reviewRequest;

    internal RealActionAcceptanceFixture(SqliteDatabaseObserver? observer = null, bool hardLink = false,
        IRecycleBinExecutor? executor = null, IActionExecutionBoundaryObserver? boundaryObserver = null,
        IFileOperationCallbackObserver? callbackObserver = null)
    {
        Selected = Files.Create("selected-תצלום-" + Guid.NewGuid().ToString("N") + ".jpg");
        Other = hardLink ? Files.Link("retained-alias.jpg", Selected) : Files.Create("second.jpg");
        Witness = Files.Create("witness.jpg");
        var seed = Files.Envelope(Selected, Other, Witness);
        var group = seed.Input.ApprovedGroup;
        var scope = seed.Claim.Request.Scope;
        var preflight = new ScopePreflightEvidence(scope, ScopeVolumeKind.Fixed, ScopeCloudState.Local,
            ScopeTraversalState.Complete, new(1, 0, 0, 0), new(0, 0));
        reviewRequest = new(new ShellSnapshot([], "Ready", ScopeSessionState.Ready, scope.Selection,
            ScopePreflightPolicy.Evaluate(preflight), Revision: 1), new(1, 1),
            new(group.Pin, ScanStatus.Complete, new(ScanStageState.Succeeded, ScanReason.None),
                group.Members, [group], new(3, 3, 0, 0, 0, 0)));
        DatabasePath = Files.PathFor("acceptance.db");
        Stores = new(DatabasePath, observer);
        var reader = new WindowsProtectedFileReader();
        var analyzer = new StreamingExactContentAnalyzer();
        Review = new(Scheduler, new BoundedReviewRevalidator(reader, analyzer), Stores.OverrideEvidence,
            actionSafety: new ProtectedActionSafetyValidator(reader, analyzer));
        Executor = new(this, executor ?? (callbackObserver is null
            ? new WindowsRecycleBinExecutor() : new WindowsRecycleBinExecutor(callbackObserver)));
        Action = boundaryObserver is null
            ? new(Scheduler, Review, Stores.OverrideEvidence, Stores.ActionJournal, Executor)
            : new(Scheduler, Review, Stores.OverrideEvidence, Stores.ActionJournal, Executor, boundaryObserver);
    }

    internal async Task<ApprovedActionRequest> ApproveReviewAsync(params long[] selected)
    {
        var ready = Signal();
        void Observe(object? sender, ReviewSnapshot snapshot)
        {
            if (snapshot.Status is ReviewStatus.Ready or ReviewStatus.Faulted) ready.TrySetResult();
        }
        Review.SnapshotChanged += Observe;
        try
        {
            Assert.Equal(ReviewCommandOutcome.Committed,
                (await Bound(Review.OpenAsync(reviewRequest, TestContext.Current.CancellationToken))).Outcome);
            await Bound(ready.Task);
            Assert.Equal(ReviewStatus.Ready, Review.Current.Status);
            Assert.All(Review.Current.Groups[0].Members, member => Assert.Equal(ReviewEligibilityState.Eligible, member.Eligibility));
            var snapshot = Review.Current;
            var group = snapshot.Groups[0].Candidate;
            var changed = await Bound(Review.ChangeSelectionAsync(new(
                new(snapshot.Pin!, group.GroupId, group.GroupRevision, snapshot.SelectionRevision),
                SelectionCommandKind.ReplaceFocusedGroup, [.. selected], Guid.NewGuid()), TestContext.Current.CancellationToken));
            Assert.Equal(ReviewCommandOutcome.Committed, changed.Outcome);
            var approved = await Bound(Review.ApproveAsync(new(Review.Current.Pin!, Review.Current.SelectionRevision,
                Guid.NewGuid(), DateTimeOffset.UtcNow), TestContext.Current.CancellationToken));
            Assert.Equal(ReviewCommandOutcome.Approved, approved.Outcome);
            Assert.True(Review.Current.Sealed);
            var request = Assert.IsType<ApprovedActionRequest>(approved.ApprovedRequest);
            Assert.Same(request, Review.Current.ApprovedRequest);
            return request;
        }
        finally { Review.SnapshotChanged -= Observe; }
    }

    internal async Task<SqliteConnection> OpenRawAsync()
    {
        Files.Check(DatabasePath);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static Task Bound(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    internal static Task<T> Bound<T>(Task<T> task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    public async ValueTask DisposeAsync()
    {
        await Action.DisposeAsync();
        await Review.DisposeAsync();
        await Stores.DisposeAsync();
        await Scheduler.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Files.Dispose();
    }

    internal sealed class AcceptanceExecutor(RealActionAcceptanceFixture fixture, IRecycleBinExecutor inner) : IRecycleBinExecutor
    {
        internal int Executions { get; private set; }
        internal RecycleBinRequest? LastRequest { get; private set; }
        internal RecycleBinResult? Result { get; private set; }
        public async ValueTask<RecycleBinResult> ExecuteAsync(RecycleBinRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            Executions++;
            Assert.Same(fixture.Review.Current.ApprovedRequest, request.Safety.Claim.Request);
            await using var read = await fixture.OpenRawAsync();
            Assert.Equal(request.ActionItemId.ToString("D"), await ScalarAsync(read,
                "SELECT action_item_id FROM action_transitions WHERE sequence=2;"));
            Assert.Equal(0L, await ScalarAsync(read, "SELECT COUNT(*) FROM action_transitions WHERE sequence=3;"));
            Result = await inner.ExecuteAsync(request, cancellationToken);
            return Result;
        }
    }
}
