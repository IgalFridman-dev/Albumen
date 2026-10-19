using Albumen.Application.Action;
using Albumen.Domain.Action;
using Albumen.Infrastructure.Windows.Action;
using Xunit;

namespace Albumen.Infrastructure.Tests.ActionExecution;

public sealed class StaFileOperationRunnerTests
{
    [Fact]
    public async Task AlreadyCancelledRequestRecordsKnownNoHandoffAndNoPerformEntry()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var driver = new RecordingFileOperationDriver();
        var result = await StaFileOperationRunner.RunAsync(@"C:\approved\one.jpg", () => driver,
            () => new RecordingRecycleLease(driver.Calls), cancellation.Token);
        Assert.False(result.Evidence.NamespaceHandoffOccurred);
        Assert.False(result.Evidence.PerformOperationsEntered);
        Assert.Equal(RecycleBinOutcomeKind.NotRemoved, result.Outcome);
        Assert.Equal(0, driver.DeleteCount);
    }
    [Theory]
    [InlineData("zeroPre", false)]
    [InlineData("zeroPre", true)]
    [InlineData("strictDenied", false)]
    [InlineData("strictDenied", true)]
    public async Task HandoffWithoutPermissionRequiresPostDriverProtectedProof(string scenario, bool unchanged)
    {
        var driver = new RecordingFileOperationDriver { Scenario = scenario };
        var lease = new RecordingRecycleLease(driver.Calls)
        { UseHandoff = true, Unchanged = unchanged, ValidationResult = scenario == "strictDenied" ? ActionSafetyReason.SharingViolation : ActionSafetyReason.None };
        var result = await StaFileOperationRunner.RunAsync(@"C:\approved\one.jpg", () => driver, () => lease, CancellationToken.None);
        Assert.True(result.Evidence.NamespaceHandoffOccurred);
        Assert.True(result.Evidence.PerformOperationsEntered);
        Assert.False(result.Evidence.PermissionGranted);
        Assert.False(result.Evidence.SelectedHandleReleased);
        Assert.Equal(unchanged ? RecycleBinOutcomeKind.NotRemoved : RecycleBinOutcomeKind.UnknownPendingRevalidation, result.Outcome);
        Assert.True(driver.Calls.IndexOf("disposeDriver") < driver.Calls.IndexOf("proveUnchanged"));
        Assert.True(driver.Calls.IndexOf("proveUnchanged") < driver.Calls.IndexOf("disposeLease"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterHandoffBeforePerformPreservesTransitionAndRunsUncancelledProof(bool unchanged)
    {
        using var cancellation = new CancellationTokenSource();
        var driver = new RecordingFileOperationDriver();
        var lease = new RecordingRecycleLease(driver.Calls)
        { UseHandoff = true, Unchanged = unchanged, AfterHandoff = cancellation.Cancel };
        var result = await StaFileOperationRunner.RunAsync(@"C:\approved\one.jpg", () => driver, () => lease, cancellation.Token);
        Assert.True(result.Evidence.NamespaceHandoffOccurred);
        Assert.False(result.Evidence.PerformOperationsEntered);
        Assert.False(result.Evidence.SelectedHandleReleased);
        Assert.False(result.Evidence.PermissionGranted);
        Assert.DoesNotContain("perform", driver.Calls);
        Assert.Contains("proveUnchanged", driver.Calls);
        Assert.Equal(unchanged ? RecycleBinOutcomeKind.NotRemoved : RecycleBinOutcomeKind.UnknownPendingRevalidation, result.Outcome);
    }

    [Fact]
    public async Task ThrowAfterActualHandoffCannotLoseTransitionOrClaimUnchangedWithoutPermission()
    {
        var driver = new RecordingFileOperationDriver();
        var lease = new RecordingRecycleLease(driver.Calls) { UseHandoff = true, ThrowAfterHandoff = true, Unchanged = true };
        var result = await StaFileOperationRunner.RunAsync(@"C:\approved\one.jpg", () => driver, () => lease, CancellationToken.None);
        Assert.True(result.Evidence.NamespaceHandoffOccurred);
        Assert.False(result.Evidence.PerformOperationsEntered);
        Assert.True(result.Evidence.RunnerInterrupted);
        Assert.False(result.Evidence.PermissionGranted);
        Assert.Equal(RecycleBinOutcomeKind.UnknownPendingRevalidation, result.Outcome);
        Assert.DoesNotContain("perform", driver.Calls);
        Assert.DoesNotContain("proveUnchanged", driver.Calls);
    }

    [Fact]
    public async Task PerformEntryIsRetainedWhenDriverThrowsBeforeAnyCallbackOrHResult()
    {
        var driver = new RecordingFileOperationDriver { ThrowStep = "perform" };
        var result = await StaFileOperationRunner.RunAsync(@"C:\approved\one.jpg", () => driver,
            () => new RecordingRecycleLease(driver.Calls) { UseHandoff = true }, CancellationToken.None);
        Assert.True(result.Evidence.NamespaceHandoffOccurred);
        Assert.True(result.Evidence.PerformOperationsEntered);
        Assert.Null(result.Evidence.PerformOperationsHResult);
        Assert.False(result.Evidence.PermissionGranted);
        Assert.Equal(RecycleBinOutcomeKind.UnknownPendingRevalidation, result.Outcome);
    }

    [Fact]
    public async Task ThrowingCreateRetainsActualPreparationExceptionHResultAfterSuccessfulComInitialization()
    {
        var driver = new RecordingFileOperationDriver { ThrowStep = "createOperation" };
        var result = await StaFileOperationRunner.RunAsync(@"C:\approved\one.jpg", () => driver,
            () => new RecordingRecycleLease(driver.Calls), CancellationToken.None);
        Assert.Equal(RecycleBinOutcomeKind.NotRemoved, result.Outcome);
        Assert.Equal(unchecked((int)0x80004002), result.Evidence.PreparationHResult);
        Assert.False(result.Evidence.PermissionGranted);
        Assert.Equal(0, driver.DeleteCount);
        Assert.Contains("disposeDriver", driver.Calls);
        Assert.Contains("uninitialize", driver.Calls);
    }

    [Fact]
    public async Task LaterExceptionCannotOverwriteCompletedPreparationAndPerformEvidence()
    {
        var driver = new RecordingFileOperationDriver { ThrowStep = "aborted" };
        var result = await StaFileOperationRunner.RunAsync(@"C:\approved\one.jpg", () => driver,
            () => new RecordingRecycleLease(driver.Calls), CancellationToken.None);
        Assert.Equal(RecycleBinOutcomeKind.UnknownPendingRevalidation, result.Outcome);
        Assert.Equal(0, result.Evidence.PreparationHResult);
        Assert.Equal(0, result.Evidence.PerformOperationsHResult);
        Assert.True(result.Evidence.RunnerInterrupted);
        Assert.False(result.Evidence.CallbackException);
    }

    [Fact]
    public async Task OneOriginalItemUsesOneDedicatedStaAndBalancesLifetime()
    {
        var caller = Environment.CurrentManagedThreadId;
        var driver = new RecordingFileOperationDriver();
        var lease = new RecordingRecycleLease(driver.Calls);
        var result = await StaFileOperationRunner.RunAsync(@"C:\approved\תמונה.jpg", () => driver, () => lease, CancellationToken.None);
        Assert.Equal(RecycleBinOutcomeKind.Recycled, result.Outcome);
        Assert.Equal(@"C:\approved\תמונה.jpg", driver.OriginalPath);
        Assert.Equal(0x00080414u, driver.Flags);
        Assert.Equal(1, driver.DeleteCount);
        Assert.Single(driver.Threads.Distinct());
        Assert.DoesNotContain(caller, driver.Threads);
        Assert.All(driver.Apartments, apartment => Assert.Equal(ApartmentState.STA, apartment));
        Assert.Equal(["initialize", "createOperation", "createItem", "advise", "flags", "deleteItem", "prepareHandoff", "perform", "releaseSelected", "aborted", "unadvise", "disposeDriver", "disposeLease", "uninitialize"], driver.Calls);
        Assert.Equal(0, result.Evidence.PreparationHResult);
        Assert.Equal(ActionSafetyReason.None, result.Evidence.ValidationReason);
        Assert.True(result.Evidence.SelectedHandleReleased);
        Assert.Equal(0, result.Evidence.GetAbortedHResult);
        Assert.False(result.Evidence.RunnerInterrupted);
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("createOperation")]
    [InlineData("createItem")]
    [InlineData("advise")]
    [InlineData("flags")]
    [InlineData("deleteItem")]
    [InlineData("perform")]
    [InlineData("aborted")]
    public async Task EveryFailedNativeStepSettlesConservativelyAndReleasesAcquiredResources(string failure)
    {
        var driver = new RecordingFileOperationDriver { Failure = failure };
        var lease = new RecordingRecycleLease(driver.Calls);
        var result = await StaFileOperationRunner.RunAsync(@"C:\approved\one.jpg", () => driver, () => lease, CancellationToken.None);
        Assert.Equal(failure is "perform" or "aborted" ? RecycleBinOutcomeKind.UnknownPendingRevalidation : RecycleBinOutcomeKind.NotRemoved, result.Outcome);
        Assert.Equal(1, driver.Calls.Count(call => call == "disposeDriver"));
        Assert.Equal(failure is "initialize" or "createOperation" or "createItem" or "advise" ? 0 : 1, driver.Calls.Count(call => call == "unadvise"));
        Assert.Equal(failure == "initialize" ? 0 : 1, driver.Calls.Count(call => call == "uninitialize"));
        Assert.True(driver.DeleteCount <= 1);
    }

    [Theory]
    [InlineData("zeroPre", RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("duplicatePre", RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("mismatchPre", RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("zeroPost", RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("duplicatePost", RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("mismatchPost", RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("postException", RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("failedDelete", RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("noNewItem", RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("abort", RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    [InlineData("interrupt", RecycleBinOutcomeKind.UnknownPendingRevalidation)]
    public async Task FakeNativeCallbackMatrixUsesRealSinkAndClassifier(string scenario, RecycleBinOutcomeKind expected)
    {
        var driver = new RecordingFileOperationDriver { Scenario = scenario };
        var result = await StaFileOperationRunner.RunAsync(@"C:\approved\one.jpg", () => driver,
            () => new RecordingRecycleLease(driver.Calls), CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(1, driver.DeleteCount);
        Assert.Contains("unadvise", driver.Calls);
        Assert.Contains("uninitialize", driver.Calls);
        if (scenario == "interrupt")
        {
            Assert.True(result.Evidence.RunnerInterrupted);
            Assert.False(result.Evidence.CallbackException);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDeleteUsesProtectedUnchangedProofWithoutRepeatingShell(bool unchanged)
    {
        var driver = new RecordingFileOperationDriver { Scenario = "failedDelete" };
        var result = await StaFileOperationRunner.RunAsync(@"C:\approved\one.jpg", () => driver,
            () => new RecordingRecycleLease(driver.Calls) { Unchanged = unchanged }, CancellationToken.None);
        Assert.Equal(unchanged ? RecycleBinOutcomeKind.NotRemoved : RecycleBinOutcomeKind.UnknownPendingRevalidation, result.Outcome);
        Assert.Equal(unchanged, result.Evidence.OriginalObjectProvenUnchanged);
        Assert.Equal(1, driver.DeleteCount);
    }

    [Fact]
    public async Task PostPermissionCancellationDoesNotCancelTerminalDeliveryOrCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var driver = new RecordingFileOperationDriver { AfterPermission = cancellation.Cancel };
        var result = await StaFileOperationRunner.RunAsync(@"C:\approved\one.jpg", () => driver,
            () => new RecordingRecycleLease(driver.Calls), cancellation.Token);
        Assert.Equal(RecycleBinOutcomeKind.Recycled, result.Outcome);
        Assert.Contains("uninitialize", driver.Calls);
    }

    [Fact]
    public async Task CancellationBeforePermissionAfterHandoffDoesNotReleaseFinalSelectedLease()
    {
        using var cancellation = new CancellationTokenSource();
        var driver = new RecordingFileOperationDriver { BeforePermission = cancellation.Cancel };
        var result = await StaFileOperationRunner.RunAsync(@"C:\approved\one.jpg", () => driver,
            () => new RecordingRecycleLease(driver.Calls), cancellation.Token);
        Assert.Equal(RecycleBinOutcomeKind.UnknownPendingRevalidation, result.Outcome);
        Assert.DoesNotContain("releaseSelected", driver.Calls);
        Assert.Contains("disposeLease", driver.Calls);
    }
}

internal sealed class RecordingRecycleLease(List<string> calls) : IRecycleBinLease
{
    internal bool Unchanged { get; init; }
    internal bool UseHandoff { get; init; } = true;
    internal bool ThrowAfterHandoff { get; init; }
    internal System.Action? AfterHandoff { get; init; }
    internal ActionSafetyReason ValidationResult { get; init; }
    public bool NamespaceHandoffOccurred { get; private set; }
    public ActionSafetyReason PrepareForPerformOperations()
    {
        calls.Add("prepareHandoff");
        NamespaceHandoffOccurred = UseHandoff;
        AfterHandoff?.Invoke();
        if (ThrowAfterHandoff) throw new InvalidOperationException("Interrupted after namespace handoff");
        return ActionSafetyReason.None;
    }
    public ActionSafetyReason ValidateAndReleaseForPreDelete() { calls.Add("releaseSelected"); return ValidationResult; }
    public bool ProveOriginalUnchanged() { calls.Add("proveUnchanged"); return Unchanged; }
    public void Dispose() => calls.Add("disposeLease");
}

internal sealed class RecordingFileOperationDriver : IFileOperationDriver
{
    internal List<string> Calls { get; } = [];
    internal List<int> Threads { get; } = [];
    internal List<ApartmentState> Apartments { get; } = [];
    internal string? Failure { get; init; }
    internal string? ThrowStep { get; init; }
    internal string? Scenario { get; init; }
    internal System.Action? BeforePermission { get; init; }
    internal System.Action? AfterPermission { get; init; }
    internal string? OriginalPath { get; private set; }
    internal uint Flags { get; private set; }
    internal int DeleteCount { get; private set; }
    private FileOperationProgressSink? sink;
    private bool identityFault;
    private int Step(string step)
    {
        Calls.Add(step); Threads.Add(Environment.CurrentManagedThreadId); Apartments.Add(Thread.CurrentThread.GetApartmentState());
        if (ThrowStep == step) throw new SimulatedFileOperationException();
        return Failure == step ? unchecked((int)0x80004005) : 0;
    }
    public int InitializeCom() => Step("initialize");
    public int CreateOperation() => Step("createOperation");
    public int CreateItem(string originalPath) { OriginalPath = originalPath; return Step("createItem"); }
    public bool IsExpectedItem(nint item) => identityFault ? throw new InvalidOperationException() : item == 11;
    public int Advise(FileOperationProgressSink progressSink) { sink = progressSink; return Step("advise"); }
    public int SetOperationFlags(uint flags) { Flags = flags; return Step("flags"); }
    public int DeleteItem() { DeleteCount++; return Step("deleteItem"); }
    public int PerformOperations()
    {
        var hr = Step("perform");
        if (hr < 0 || Scenario == "zeroPre") return hr;
        BeforePermission?.Invoke();
        var permission = sink!.PreDeleteItem(0, Scenario == "mismatchPre" ? 12 : 11);
        if (permission < 0) return permission;
        AfterPermission?.Invoke();
        if (Scenario == "interrupt") throw new InvalidOperationException("Interrupted native driver");
        if (Scenario == "duplicatePre") sink.PreDeleteItem(0, 11);
        if (Scenario == "zeroPost") return 0;
        sink.PostDeleteItem(0, Scenario == "mismatchPost" ? 12 : 11, Scenario == "failedDelete" ? -1 : 0,
            Scenario is "noNewItem" or "failedDelete" ? 0 : 22);
        if (Scenario == "duplicatePost") sink.PostDeleteItem(0, 11, 0, 22);
        if (Scenario == "postException") { identityFault = true; sink.PostDeleteItem(0, 11, 0, 22); }
        return 0;
    }
    public int GetAnyOperationsAborted(out bool aborted) { aborted = Scenario == "abort"; return Step("aborted"); }
    public int Unadvise() => Step("unadvise");
    public void Dispose() => Step("disposeDriver");
    public void UninitializeCom() => Step("uninitialize");

    private sealed class SimulatedFileOperationException : Exception
    {
        internal SimulatedFileOperationException() { HResult = unchecked((int)0x80004002); }
    }
}
