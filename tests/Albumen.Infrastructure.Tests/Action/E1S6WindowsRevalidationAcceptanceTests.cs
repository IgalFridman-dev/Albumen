using System.Text.Json;
using Albumen.Application.Action;
using Albumen.Domain.Action;
using Albumen.Infrastructure.Sqlite;
using Albumen.Infrastructure.Windows.Action;
using Albumen.Infrastructure.Tests.Exact;
using Xunit;

namespace Albumen.Infrastructure.Tests.ActionExecution;

[Collection(WindowsRecycleBinIsolationDefinition.Name)]
public sealed class E1S6WindowsRevalidationAcceptanceTests
{
    private const string ModeVariable = "ALBUMEN_E1S6_HARNESS_MODE";
    private const string PointVariable = "ALBUMEN_E1S6_CRASH_POINT";
    private const string ManifestVariable = "ALBUMEN_E1S6_MANIFEST_PATH";

    [Fact]
    public async Task ProcessHarnessChildUsesRealActionJournalAndRecovery()
    {
        var mode = Environment.GetEnvironmentVariable(ModeVariable);
        if (mode is null) return;
        var point = Assert.IsType<string>(Environment.GetEnvironmentVariable(PointVariable));
        var manifestPath = Assert.IsType<string>(Environment.GetEnvironmentVariable(ManifestVariable));
        if (mode == "crash")
        {
            var boundary = new CrashBoundary(point);
            await using var fixture = new RealActionAcceptanceFixture(hardLink: false,
                boundaryObserver: boundary, callbackObserver: boundary);
            var request = await fixture.ApproveReviewAsync(1, 2);
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new Manifest(fixture.DatabasePath,
                fixture.Files.Root, request.RequestId, request.Items[0].Allocation.AllocatedBytes,
                request.Items.Sum(item => item.Allocation.AllocatedBytes))));
            _ = await fixture.Action.ApproveAsync(request, CancellationToken.None);
            throw new InvalidOperationException("The requested real boundary was not reached.");
        }

        Assert.Equal("restart", mode);
        var manifest = Assert.IsType<Manifest>(JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath)));
        var first = await LoadAsync(manifest.DatabasePath);
        var second = await LoadAsync(manifest.DatabasePath);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        if (point == "before-requested-commit")
        {
            Assert.DoesNotContain(first.Records, value => value.RequestId == manifest.RequestId);
            File.WriteAllText(manifestPath + ".verified", "stable-real-sqlite-no-replay-no-probe");
            Directory.Delete(manifest.Root, true);
            return;
        }
        var record = Assert.Single(first.Records, value => value.RequestId == manifest.RequestId);
        Assert.Equal(ExpectedRows(point), record.Items.Length);
        Assert.Equal(ExpectedStates(point), record.Items.Select(item => item.State).ToArray());
        Assert.Equal(ExpectedResults(point), record.Items.Select(item => Result(item.State)).ToArray());
        Assert.Equal(point == "after-terminal-commit-before-ui-publication"
            ? ActionRecordCertainty.Resolved : ActionRecordCertainty.HasUnknown, record.Summary.Certainty);
        Assert.Equal(point == "after-terminal-commit-before-ui-publication"
            ? ActionRecordOutcomeMix.AllMoved : ActionRecordOutcomeMix.NoneMoved, record.Summary.OutcomeMix);
        Assert.Equal(point == "after-terminal-commit-before-ui-publication"
            ? manifest.FirstAllocatedBytes : 0, record.Summary.RecoveredBytes);
        Assert.Equal(point == "after-terminal-commit-before-ui-publication" ? 0 : 1,
            record.Items.Count(item => item.State is ActionRecordEvidenceState.Requested
                or ActionRecordEvidenceState.Executing or ActionRecordEvidenceState.Unknown));
        if (point == "after-executing-before-adapter-entry")
            await RevalidateThroughRealWindowsProbeAsync(manifest.DatabasePath, manifest.RequestId);
        File.WriteAllText(manifestPath + ".verified", "stable-real-sqlite-no-replay-no-probe");
        Directory.Delete(manifest.Root, true);
    }

    private static async Task<ActionRecoverySnapshot> LoadAsync(string databasePath)
    {
        await using var scheduler = new SerialTestScheduler();
        await using var stores = new SqliteAlbumenStores(databasePath);
        var probe = new CountingProbe();
        await using var recovery = new ActionRecoveryCoordinator(scheduler, stores.ActionRecords, probe);
        await recovery.LoadAsync(CancellationToken.None);
        Assert.Equal(ActionRecoveryStatus.Ready, recovery.Current.Status);
        Assert.Equal(0, probe.Calls);
        return recovery.Current;
    }

    private static async Task RevalidateThroughRealWindowsProbeAsync(string databasePath, Guid requestId)
    {
        await using var scheduler = new SerialTestScheduler();
        await using var stores = new SqliteAlbumenStores(databasePath);
        await using var recovery = new ActionRecoveryCoordinator(scheduler, stores.ActionRecords,
            new WindowsActionRevalidationProbe());
        await recovery.LoadAsync(CancellationToken.None);
        await recovery.RevalidateUnknownAsync(requestId, CancellationToken.None);
        var record = Assert.Single(recovery.Current.Records, value => value.RequestId == requestId);
        Assert.Equal(ActionRecordEvidenceState.ConfirmedPresent, record.Items[0].State);
        Assert.Equal("not removed", Result(record.Items[0].State));
        Assert.Equal(ActionRecordCertainty.Resolved, record.Summary.Certainty);
    }

    private static int ExpectedRows(string point) => 1;
    private static ActionRecordEvidenceState[] ExpectedStates(string point) => point switch
    {
        "after-requested-before-executing" => [ActionRecordEvidenceState.Requested],
        "after-terminal-commit-before-ui-publication" => [ActionRecordEvidenceState.Recycled],
        _ => [ActionRecordEvidenceState.Executing],
    };
    private static string[] ExpectedResults(string point) => ExpectedStates(point).Select(Result).ToArray();
    private static string Result(ActionRecordEvidenceState state) => state switch
    {
        ActionRecordEvidenceState.Recycled => "moved to Recycle Bin",
        ActionRecordEvidenceState.NotRemoved or ActionRecordEvidenceState.ConfirmedPresent => "not removed",
        _ => "outcome unknown pending revalidation",
    };

    private sealed record Manifest(string DatabasePath, string Root, Guid RequestId,
        long? FirstAllocatedBytes, long? TotalAllocatedBytes);
    private sealed class CountingProbe : IActionRevalidationProbe
    {
        internal int Calls { get; private set; }
        public ValueTask<ActionRevalidationProbeResult> ProbeAsync(ActionRevalidationRequest request, CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Startup must not probe."); }
    }
    private sealed class CrashBoundary(string point) : IActionExecutionBoundaryObserver, IFileOperationCallbackObserver
    {
        private int callbacks;
        public void Observe(ActionExecutionBoundary boundary, int itemIndex)
        {
            if (itemIndex == 0 && point == Name(boundary)) Environment.FailFast("E1-S6 real boundary: " + point);
        }
        public void Observe(FileOperationCallbackBoundary boundary)
        {
            if (++callbacks == 1 && point == "during-shell-callbacks")
                Environment.FailFast("E1-S6 real shell callback boundary");
        }
        private static string Name(ActionExecutionBoundary boundary) => boundary switch
        {
            ActionExecutionBoundary.BeforeRequestedCommit => "before-requested-commit",
            ActionExecutionBoundary.AfterRequestedCommitBeforeExecuting => "after-requested-before-executing",
            ActionExecutionBoundary.AfterExecutingCommitBeforeAdapter => "after-executing-before-adapter-entry",
            ActionExecutionBoundary.AfterAdapterReturnBeforeTerminalCommit => "after-shell-return-before-terminal-commit",
            ActionExecutionBoundary.AfterTerminalCommitBeforePublication => "after-terminal-commit-before-ui-publication",
            _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
        };
    }
}
