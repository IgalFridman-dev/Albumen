using Microsoft.Win32.SafeHandles;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Action;
using PhotoSift.Infrastructure.Windows.Scan;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.ActionExecution;

[Trait("Category", "GoldenVm")]
public sealed class WindowsActionRaceTests
{
    [Theory]
    [InlineData("selected", "identity")]
    [InlineData("selected", "length")]
    [InlineData("selected", "content")]
    [InlineData("selected", "links")]
    [InlineData("selected", "unknownLinks")]
    [InlineData("selected", "allocation")]
    [InlineData("selected", "sync")]
    [InlineData("selected", "placeholder")]
    [InlineData("selected", "readonly")]
    [InlineData("selected", "path")]
    [InlineData("selected", "unknown")]
    [InlineData("ancestor", "identity")]
    [InlineData("ancestor", "reparse")]
    [InlineData("ancestor", "sync")]
    [InlineData("ancestor", "unknown")]
    [InlineData("witness", "identity")]
    [InlineData("witness", "content")]
    [InlineData("witness", "length")]
    [InlineData("witness", "links")]
    [InlineData("witness", "sync")]
    [InlineData("witness", "unknown")]
    [InlineData("witnessAncestor", "identity")]
    [InlineData("witnessAncestor", "reparse")]
    public async Task LatestSignaledFactChangeDeniesPermissionAndRetainsSelected(string target, string change)
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create(Path.Combine("selected-parent", "selected.jpg"));
        var witness = fixture.Create(Path.Combine("witness-parent", "witness.jpg"));
        var path = target switch { "selected" => selected, "witness" => witness,
            "ancestor" => Path.GetDirectoryName(selected)!, _ => Path.GetDirectoryName(witness)! };
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var armed = false;
        var source = new ActionObservedFacts((candidate, facts) => armed && candidate == path ? Alter(facts, change) : facts);
        using var lease = await WindowsActionLease.OpenAsync(fixture.Envelope(selected, witness), CancellationToken.None, source);
        var contender = Task.Run(async () =>
        {
            await prepared.Task;
            armed = true;
            changed.SetResult();
        }, CancellationToken.None);
        prepared.SetResult();
        await changed.Task.WaitAsync(TestContext.Current.CancellationToken);
        await contender;
        Assert.NotEqual(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
        Assert.Equal(32, fixture.Delete(selected));
        fixture.AssertWriteDenied(selected);
        Assert.True(File.Exists(selected));
        // A rejected lease is poisoned; a return to the old facts cannot grant.
        armed = false;
        Assert.NotEqual(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
    }

    [Fact]
    public async Task LiveDirectoryGuardsDenyAncestorRenameAndContentMutation()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create(Path.Combine("album", "selected.jpg"));
        var witness = fixture.Create("witness.jpg");
        using var lease = await WindowsActionLease.OpenAsync(fixture.Envelope(selected, witness), CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contender = Task.Run(async () =>
        {
            await entered.Task;
            var error = fixture.Move(Path.GetDirectoryName(selected)!, fixture.PathFor("moved"));
            Assert.True(error is 5 or 32);
            fixture.AssertWriteDenied(selected);
            Assert.Equal(32, fixture.Delete(witness));
        }, CancellationToken.None);
        entered.SetResult();
        await contender.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
        Assert.True(File.Exists(selected));
    }

    [Fact]
    public async Task EveryLatestNativeReadStillHasSelectedProtectionAndReturnReleasesIt()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var witness = fixture.Create("witness.jpg");
        var callback = false;
        var trace = new List<string>();
        var source = new ActionObservedFacts((path, facts) =>
        {
            if (callback)
            {
                Assert.Equal(32, fixture.Delete(selected));
                fixture.AssertWriteDenied(selected);
                trace.Add(path);
            }
            return facts;
        });
        using var lease = await WindowsActionLease.OpenAsync(fixture.Envelope(selected, witness), CancellationToken.None, source);
        callback = true;
        Assert.Equal(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
        Assert.Contains(selected, trace);
        Assert.Contains(witness, trace);
        Assert.Contains(fixture.Root, trace);
        fixture.AssertWritable(selected);
        fixture.AssertWriteDenied(witness);
        Assert.NotEqual(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
    }

    internal static WindowsFileObservation Alter(WindowsFileObservation facts, string change) => change switch
    {
        "identity" => facts with { PhysicalFileKey = new(123, new(456, 789)) },
        "length" => facts with { ByteLength = 10 },
        "content" => facts with { LastWriteUtc = facts.LastWriteUtc?.AddSeconds(1) },
        "links" => facts with { Allocation = facts.Allocation with { LinkCount = 3 } },
        "unknownLinks" => facts with { Allocation = facts.Allocation with { LinkCount = null } },
        "allocation" => facts with { Allocation = facts.Allocation with { Stable = false } },
        "sync" => facts with { CloudState = ScopeCloudState.Synchronized },
        "placeholder" => facts with { CloudState = ScopeCloudState.Placeholder },
        "readonly" => facts with { Attributes = facts.Attributes | 1 },
        "path" => facts with { ResolvedPath = facts.ResolvedPath + ".replaced" },
        "unknown" => facts with { PhysicalFileKey = null },
        "reparse" => facts with { Attributes = facts.Attributes | 0x400, ReparseTag = 0xA0000003 },
        _ => throw new ArgumentOutOfRangeException(nameof(change)),
    };
}

internal sealed class ActionObservedFacts(Func<string, WindowsFileObservation, WindowsFileObservation> alter) : IWindowsFileFactsSource
{
    private readonly WindowsFileNative native = new();
    public WindowsFileObservation Read(SafeFileHandle handle, string path) => alter(path, native.Read(handle, path));
}
