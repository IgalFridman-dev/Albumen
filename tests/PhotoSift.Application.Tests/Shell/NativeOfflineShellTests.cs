using PhotoSift.Application.Shell;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Application.Tests.Shell;

public sealed class NativeOfflineShellTests
{
    [Fact]
    public async Task OpenAsyncReturnsTheImmutableInitialWorkflowSnapshot()
    {
        var shell = new NativeOfflineShell();

        var snapshot = await shell.OpenAsync(
            new OpenChooseScopeCommand(),
            CancellationToken.None);

        Assert.Equal("Choose one local folder to scan.", snapshot.Instruction);
        Assert.Equal(InitialWorkflow.Create(), snapshot.Phases);
        Assert.Equal(PhaseAvailability.Current, snapshot.Phases[0].Availability);
        Assert.All(
            snapshot.Phases.Skip(1),
            phase => Assert.Equal(PhaseAvailability.Unavailable, phase.Availability));
    }

    [Fact]
    public async Task OpenAsyncHonorsPreCanceledRequests()
    {
        var shell = new NativeOfflineShell();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => shell.OpenAsync(new OpenChooseScopeCommand(), cancellation.Token));
    }
}
