using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PhotoSift.Application.Action;
using PhotoSift.Domain.Action;
using PhotoSift.Scenarios.Tests.Shell;
using Xunit;

namespace PhotoSift.Scenarios.Tests.ActionExecution;

public sealed class E1S6CrashRecoveryHarnessTests
{
    private const string CrashPointEnvironment = "PHOTOSIFT_E1S6_CRASH_POINT";
    private const string RestartPointEnvironment = "PHOTOSIFT_E1S6_RESTART_POINT";
    private const string StatePathEnvironment = "PHOTOSIFT_E1S6_STATE_PATH";
    private static readonly string[] CrashPoints = ["before-requested-commit", "after-requested-before-executing",
        "after-executing-before-adapter-entry", "during-shell-callbacks",
        "after-shell-return-before-terminal-commit", "after-terminal-commit-before-ui-publication"];
    private static readonly string[] RunnerArguments = ["-automated", "async", "-parallel", "none", "-noLogo",
        "-noColor", "-method", "PhotoSift.Scenarios.Tests.ActionExecution.E1S6CrashRecoveryHarnessTests.ProcessHarnessChildPersistsBoundaryThenRestartProjectsStableResults"];

    [Theory]
    [InlineData("before-requested-commit", null, null, null, null, 0)]
    [InlineData("after-requested-before-executing", ActionRecordEvidenceState.Requested, "outcome unknown pending revalidation", "Has unknown", "None moved", 0)]
    [InlineData("after-executing-before-adapter-entry", ActionRecordEvidenceState.Executing, "outcome unknown pending revalidation", "Has unknown", "None moved", 0)]
    [InlineData("during-shell-callbacks", ActionRecordEvidenceState.Executing, "outcome unknown pending revalidation", "Has unknown", "None moved", 0)]
    [InlineData("after-shell-return-before-terminal-commit", ActionRecordEvidenceState.Executing, "outcome unknown pending revalidation", "Has unknown", "None moved", 0)]
    [InlineData("after-terminal-commit-before-ui-publication", ActionRecordEvidenceState.Recycled, "moved to Recycle Bin", "Resolved", "All moved", 8192)]
    public void RestartedResultsNeverReplayCoerceOrOmitDurableRequestedRows(
        string terminationPoint, ActionRecordEvidenceState? durableState, string? expectedResult,
        string? expectedCertainty, string? expectedMix, long expectedBytes)
    {
        var firstRestart = new ActionShellFixture();
        var records = durableState is { } state
            ? new[] { E1S6ActionReconciliationUiTests.Record(E1S6ActionReconciliationUiTests.Row(1, state, 8192)) }
            : [];

        firstRestart.PublishRecovery(new(ActionRecoveryStatus.Ready, [.. records], null, false));

        Assert.Empty(firstRestart.Revalidations);
        Assert.Equal(durableState is null ? 0 : 1, CountRows(firstRestart));
        if (expectedResult is not null)
        {
            Assert.Equal(expectedResult, Result(firstRestart));
            Assert.Equal((expectedCertainty, expectedMix,
                "Recovered bytes: " + expectedBytes.ToString(CultureInfo.InvariantCulture)), Summary(firstRestart));
        }

        var secondRestart = new ActionShellFixture();
        secondRestart.PublishRecovery(new(ActionRecoveryStatus.Ready, [.. records], null, false));
        Assert.Empty(secondRestart.Revalidations);
        Assert.Equal(CountRows(firstRestart), CountRows(secondRestart));
        if (expectedResult is not null)
        {
            Assert.Equal(expectedResult, Result(secondRestart));
            Assert.Equal(Summary(firstRestart), Summary(secondRestart));
        }
        Assert.DoesNotContain("not removed", expectedResult ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(terminationPoint));
    }

    [Fact]
    public async Task ProcessHarnessChildPersistsBoundaryThenRestartProjectsStableResults()
    {
        var crashPoint = Environment.GetEnvironmentVariable(CrashPointEnvironment);
        var restartPoint = Environment.GetEnvironmentVariable(RestartPointEnvironment);
        var statePath = Environment.GetEnvironmentVariable(StatePathEnvironment);
        if (crashPoint is null && restartPoint is null)
        {
            Assert.Null(statePath);
            await RunProcessHarnessAsync();
            return;
        }
        Assert.False(string.IsNullOrWhiteSpace(statePath));
        if (crashPoint is not null)
        {
            var state = State(crashPoint);
            File.WriteAllText(statePath!, JsonSerializer.Serialize(state));
            Environment.FailFast($"E1-S6 injected termination: {crashPoint}");
        }

        Assert.Equal(restartPoint, Path.GetFileNameWithoutExtension(statePath));
        var recovered = JsonSerializer.Deserialize<CrashState>(File.ReadAllText(statePath!));
        Assert.NotNull(recovered);
        var fixture = new ActionShellFixture();
        var records = recovered.State is { } stateName
            ? new[] { E1S6ActionReconciliationUiTests.Record(E1S6ActionReconciliationUiTests.Row(
                1, Enum.Parse<ActionRecordEvidenceState>(stateName), 8192)) }
            : [];
        fixture.PublishRecovery(new(ActionRecoveryStatus.Ready, [.. records], null, false));
        Assert.Equal(recovered.DurableRows, CountRows(fixture));
        Assert.Empty(fixture.Revalidations);
        if (recovered.ExpectedResult is not null)
            Assert.Equal(recovered.ExpectedResult, Result(fixture));
        File.WriteAllText(statePath + ".verified", "stable-no-replay");
    }

    private static async Task RunProcessHarnessAsync()
    {
        var executable = Path.ChangeExtension(typeof(E1S6CrashRecoveryHarnessTests).Assembly.Location, ".exe");
        Assert.True(File.Exists(executable));
        var root = Path.Combine(Path.GetTempPath(), "photosift-e1s6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var point in CrashPoints)
            {
                var state = Path.Combine(root, point + ".json");
                using (var crash = Start(executable, point, null, state))
                {
                    await crash.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.NotEqual(0, crash.ExitCode);
                    Assert.True(File.Exists(state));
                }
                using (var restart = Start(executable, null, point, state))
                {
                    await restart.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(0, restart.ExitCode);
                    Assert.True(File.Exists(state + ".verified"));
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Process Start(string executable, string? crashPoint, string? restartPoint, string statePath)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in RunnerArguments)
            start.ArgumentList.Add(argument);
        start.Environment[CrashPointEnvironment] = crashPoint;
        start.Environment[RestartPointEnvironment] = restartPoint;
        start.Environment[StatePathEnvironment] = statePath;
        var process = new Process { StartInfo = start };
        Assert.True(process.Start());
        return process;
    }

    private static CrashState State(string point) => point switch
    {
        "before-requested-commit" => new(0, null, null),
        "after-requested-before-executing" => new(1, nameof(ActionRecordEvidenceState.Requested), "outcome unknown pending revalidation"),
        "after-executing-before-adapter-entry" or "during-shell-callbacks" or "after-shell-return-before-terminal-commit" =>
            new(1, nameof(ActionRecordEvidenceState.Executing), "outcome unknown pending revalidation"),
        "after-terminal-commit-before-ui-publication" => new(1, nameof(ActionRecordEvidenceState.Recycled), "moved to Recycle Bin"),
        _ => throw new InvalidOperationException($"Unknown E1-S6 crash point: {point}"),
    };

    private static int CountRows(ActionShellFixture fixture) =>
        ActionShellFixture.Read<IEnumerable>(fixture.ViewModel, "ActionRecoveryRecords")
            .Cast<object>().Sum(record => ActionShellFixture.Read<IEnumerable>(record, "Items").Cast<object>().Count());

    private static string Result(ActionShellFixture fixture)
    {
        var record = Assert.Single(ActionShellFixture.Read<IEnumerable>(fixture.ViewModel, "ActionRecoveryRecords").Cast<object>());
        var row = Assert.Single(ActionShellFixture.Read<IEnumerable>(record, "Items").Cast<object>());
        return ActionShellFixture.Read<string>(row, "ResultText");
    }

    private static (string Certainty, string Mix, string Bytes) Summary(ActionShellFixture fixture)
    {
        var record = Assert.Single(ActionShellFixture.Read<IEnumerable>(fixture.ViewModel, "ActionRecoveryRecords").Cast<object>());
        return (ActionShellFixture.Read<string>(record, "CertaintyText"),
            ActionShellFixture.Read<string>(record, "OutcomeMixText"),
            ActionShellFixture.Read<string>(record, "RecoveredBytesText"));
    }

    private sealed record CrashState(int DurableRows, string? State, string? ExpectedResult);
}
