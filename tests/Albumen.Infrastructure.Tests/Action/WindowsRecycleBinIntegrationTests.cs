using Albumen.Application.Action;
using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using Albumen.Domain.Action;
using Albumen.Domain.Scan;
using Albumen.Infrastructure.Windows.Action;
using Albumen.Infrastructure.Windows.Scan;
using Albumen.Infrastructure.Windows.Scope;
using Xunit;

namespace Albumen.Infrastructure.Tests.ActionExecution;

[Collection(WindowsRecycleBinIsolationDefinition.Name)]
[Trait("Category", "GoldenVm")]
public sealed class WindowsRecycleBinIntegrationTests
{
    [Fact]
    public async Task ReadOnlyDirectoryAccessProbeDistinguishesRetainedGuardFromSelectedHandle()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("guard-probe.jpg");
        var envelope = fixture.Envelope(selected, fixture.Create("witness.jpg"));
        var volumeRoot = Path.GetPathRoot(fixture.Root)!;
        var beforeRoot = ProbeDirectoryAccess(fixture, volumeRoot, "before lease");
        var beforeFixture = ProbeDirectoryAccess(fixture, fixture.Root, "before lease");
        var lease = await WindowsActionLease.OpenAsync(envelope, CancellationToken.None);
        var releaseReason = ActionSafetyReason.UnknownSafety;
        try
        {
            ProbeDirectoryAccess(fixture, volumeRoot, "lease held");
            ProbeDirectoryAccess(fixture, fixture.Root, "lease held");
            releaseReason = lease.ValidateAndReleaseForPreDelete();
            ProbeDirectoryAccess(fixture, volumeRoot, "selected handle released");
            ProbeDirectoryAccess(fixture, fixture.Root, "selected handle released");
        }
        finally { lease.Dispose(); }
        var afterRoot = ProbeDirectoryAccess(fixture, volumeRoot, "lease disposed");
        var afterFixture = ProbeDirectoryAccess(fixture, fixture.Root, "lease disposed");
        Assert.Equal(0, beforeRoot);
        Assert.Equal(0, beforeFixture);
        Assert.Equal(ActionSafetyReason.None, releaseReason);
        Assert.Equal(0, afterRoot);
        Assert.Equal(0, afterFixture);
        Assert.True(File.Exists(selected));
    }

    [Fact]
    public async Task IndependentShellItemForExactOriginalMatchesButWitnessAndHardLinkAliasDoNot()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("identity.jpg");
        var witness = fixture.Create("different-item.jpg");
        var alias = fixture.Link("alias.jpg", selected);
        bool[]? comparisons = null;
        var output = TestContext.Current.TestOutputHelper;
        var result = await StaFileOperationRunner.RunAsync(selected,
            () => new TracedNativeDriver(message => output?.WriteLine(message), native =>
            {
                using var exactItem = new NativeFileOperationDriver();
                using var distinctItem = new NativeFileOperationDriver();
                using var aliasItem = new NativeFileOperationDriver();
                Assert.Equal(0, exactItem.CreateItem(selected));
                Assert.Equal(0, distinctItem.CreateItem(witness));
                Assert.Equal(0, aliasItem.CreateItem(alias));
                comparisons = [native.IsExpectedItem(ItemPointer(exactItem)), native.IsExpectedItem(ItemPointer(distinctItem)), native.IsExpectedItem(ItemPointer(aliasItem))];
                // End during preparation: this identity probe cannot enqueue an operation.
                return unchecked((int)0x80004004);
            }), () => new RecordingRecycleLease([]), CancellationToken.None);
        Assert.Equal(RecycleBinOutcomeKind.NotRemoved, result.Outcome);
        Assert.NotNull(comparisons);
        Assert.Equal([true, false, false], comparisons);
        Assert.True(File.Exists(selected));
        Assert.True(File.Exists(witness));
        Assert.True(File.Exists(alias));
    }

    [Fact]
    public async Task RealShellRecyclesOneProtectedOriginalAndReturnsRecycleItemWithoutEnumeratingBin()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("recycle-" + Guid.NewGuid().ToString("N") + ".jpg");
        var witness = fixture.Create("witness.jpg");
        var envelope = fixture.Envelope(selected, witness);
        var selectedBefore = ReadSourceTruth(selected);
        var witnessBefore = ReadSourceTruth(witness);
        Assert.Equal(envelope.Item.PhysicalFileKey, selectedBefore.PhysicalFileKey);
        var result = await new WindowsRecycleBinExecutor().ExecuteAsync(new(Guid.NewGuid(), envelope), CancellationToken.None);
        LogResult(result);
        Assert.Equal(RecycleBinOutcomeKind.Recycled, result.Outcome);
        Assert.Equal(1, result.Evidence.ValidPreDeleteCount);
        Assert.Equal(0, result.Evidence.UnexpectedPreDeleteCount);
        Assert.True(result.Evidence.NamespaceHandoffOccurred);
        Assert.True(result.Evidence.PerformOperationsEntered);
        Assert.True(result.Evidence.SelectedHandleReleased);
        Assert.True(result.Evidence.PermissionGranted);
        Assert.True(result.Evidence.PreparationHResult >= 0);
        Assert.True(result.Evidence.PerformOperationsHResult >= 0);
        Assert.False(result.Evidence.OperationsAborted);
        Assert.True(result.Evidence.GetAbortedHResult >= 0);
        Assert.Equal(1, result.Evidence.MatchingPostDeleteCount);
        Assert.Equal(0, result.Evidence.UnexpectedPostDeleteCount);
        Assert.True(result.Evidence.DeleteHResult >= 0);
        Assert.True(result.Evidence.FinishOperationsHResult >= 0);
        Assert.True(result.Evidence.NewlyCreatedItemPresent);
        Assert.False(result.Evidence.CallbackException);
        Assert.False(File.Exists(selected));
        Assert.Equal(witnessBefore, ReadSourceTruth(witness));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Protected sources: selectedKey={selectedBefore.PhysicalFileKey}; " +
            $"selectedSha256={selectedBefore.Sha256}; witnessKey={witnessBefore.PhysicalFileKey}; " +
            $"witnessSha256={witnessBefore.Sha256}");
        TestContext.Current.TestOutputHelper?.WriteLine("Real shell: full one-item success tuple, non-null recycled shell item, original absent, witness intact. No Recycle Bin enumeration or cleanup.");
    }

    [Fact]
    public async Task PerformOperationsCannotPassPreDeleteUntilSelectedLeaseIsReleased()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("gated.jpg");
        var witness = fixture.Create("witness.jpg");
        var envelope = fixture.Envelope(selected, witness);
        var approvedKey = envelope.Item.PhysicalFileKey;
        var entered = new TaskCompletionSource<PhysicalFileKey?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowRelease = new ManualResetEventSlim();
        var output = TestContext.Current.TestOutputHelper;
        var inPreDelete = false;
        var source = new ActionObservedFacts((path, facts) =>
        {
            if (inPreDelete && path == selected && !entered.Task.IsCompleted)
            {
                // The production lease must reacquire strict selected protection
                // before this first latest facts read, including its own identity read.
                entered.SetResult(facts.PhysicalFileKey);
                Assert.Equal(approvedKey, facts.PhysicalFileKey);
                if (!allowRelease.Wait(TimeSpan.FromSeconds(30))) throw new InvalidOperationException("PreDelete fixture gate timed out");
            }
            return facts;
        });
        var execution = StaFileOperationRunner.RunAsync(selected, () => new TracedNativeDriver(message => output?.WriteLine(message)),
            () => new GatedLease(WindowsActionLease.OpenAsync(envelope, CancellationToken.None, source).GetAwaiter().GetResult(), () => inPreDelete = true), CancellationToken.None);
        try
        {
            var observedKey = await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(approvedKey, observedKey);
            Assert.False(execution.IsCompleted);
            Assert.True(File.Exists(selected));
            fixture.AssertWriteDenied(selected);
            Assert.Equal(32, ProbeFileDeleteAccess(fixture, selected, "production strict PreDelete gate"));
        }
        finally
        {
            allowRelease.Set();
            // Even a failed assertion/entry timeout must observe terminal cleanup
            // before the enclosing fixture can remove its disposable directory.
            var settled = await execution;
            LogResult(settled);
        }
        var result = await execution;
        Assert.Equal(RecycleBinOutcomeKind.Recycled, result.Outcome);
        Assert.True(result.Evidence.NamespaceHandoffOccurred);
        Assert.True(result.Evidence.PerformOperationsEntered);
        Assert.True(result.Evidence.SelectedHandleReleased);
        Assert.False(File.Exists(selected));
        Assert.True(File.Exists(witness));
    }

    [Theory]
    [InlineData("locked", ActionSafetyReason.SharingViolation)]
    [InlineData("readonly", ActionSafetyReason.ReadOnly)]
    [InlineData("unavailable", ActionSafetyReason.Missing)]
    public async Task UnsafePreparationCannotRemoveSelectedOrWitness(string scenario, ActionSafetyReason expected)
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("retained.jpg");
        var witness = fixture.Create("witness.jpg");
        var envelope = fixture.Envelope(selected, witness);
        var selectedBefore = ReadSourceTruth(selected);
        var witnessBefore = ReadSourceTruth(witness);
        FileStream? blocker = null;
        try
        {
            if (scenario == "locked") blocker = fixture.Writer(selected);
            if (scenario == "readonly") fixture.Attributes(selected, FileAttributes.ReadOnly);
            if (scenario == "unavailable") Assert.Equal(0, fixture.Delete(selected));
            var result = await new WindowsRecycleBinExecutor().ExecuteAsync(new(Guid.NewGuid(), envelope), CancellationToken.None);
            Assert.Equal(RecycleBinOutcomeKind.NotRemoved, result.Outcome);
            Assert.Equal(expected, result.Reason);
            Assert.False(result.Evidence.PermissionGranted);
            Assert.Equal(scenario != "unavailable", File.Exists(selected));
            if (scenario == "unavailable")
            {
                Assert.False(File.Exists(selected));
            }
            else
            {
                Assert.Equal(selectedBefore, ReadSourceTruth(selected));
            }
            Assert.Equal(witnessBefore, ReadSourceTruth(witness));
        }
        finally
        {
            blocker?.Dispose();
            if (File.Exists(selected)) fixture.Attributes(selected, FileAttributes.Normal);
        }
    }

    private static SourceTruth ReadSourceTruth(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var facts = new WindowsFileNative().Read(stream.SafeFileHandle, path);
        var key = facts.PhysicalFileKey
            ?? throw new InvalidDataException($"Source identity was unavailable: {path}");
        return new SourceTruth(key, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private sealed record SourceTruth(PhysicalFileKey PhysicalFileKey, string Sha256);

    private sealed class GatedLease(WindowsActionLease lease, System.Action enterPreDelete) : IRecycleBinLease
    {
        public bool NamespaceHandoffOccurred => lease.NamespaceHandoffOccurred;
        public ActionSafetyReason PrepareForPerformOperations() => lease.PrepareForPerformOperations();
        public ActionSafetyReason ValidateAndReleaseForPreDelete()
        {
            enterPreDelete();
            return lease.ValidateAndReleaseForPreDelete();
        }
        public bool ProveOriginalUnchanged() => false;
        public void Dispose() => lease.Dispose();
    }

    private static void LogResult(RecycleBinResult result)
    {
        TestContext.Current.TestOutputHelper?.WriteLine("One-item native result: " + JsonSerializer.Serialize(result));
        TestContext.Current.TestOutputHelper?.WriteLine($"Native HRESULTs: preparation={result.Evidence.PreparationHResult:X8}; perform={result.Evidence.PerformOperationsHResult:X8}; abort-query={result.Evidence.GetAbortedHResult:X8}; delete={result.Evidence.DeleteHResult:X8}; finish={result.Evidence.FinishOperationsHResult:X8}; reason={result.Reason}");
    }

    // Read-only diagnostic on the borrowed callback item. This never substitutes
    // an item, authorizes permission, resolves a path, or modifies the real sink.
    private sealed class TracedNativeDriver(System.Action<string> log, Func<NativeFileOperationDriver, int>? afterCreate = null) : IFileOperationDriver
    {
        private readonly NativeFileOperationDriver native = new();
        public int InitializeCom() => Trace("InitializeCom", native.InitializeCom());
        public int CreateOperation() => Trace("CreateOperation", native.CreateOperation());
        public int CreateItem(string originalPath)
        {
            var result = Trace("CreateItem", native.CreateItem(originalPath));
            return result >= 0 && afterCreate is not null ? afterCreate(native) : result;
        }
        public bool IsExpectedItem(nint callbackItem)
        {
            var same = native.IsExpectedItem(callbackItem);
            log($"Callback exact Shell item match: {same}; non-null callback: {callbackItem != 0}");
            return same;
        }
        public int Advise(FileOperationProgressSink sink) => Trace("Advise", native.Advise(sink));
        public int SetOperationFlags(uint flags) => Trace("SetOperationFlags", native.SetOperationFlags(flags));
        public int DeleteItem() => Trace("DeleteItem", native.DeleteItem());
        public int PerformOperations() => Trace("PerformOperations", native.PerformOperations());
        public int GetAnyOperationsAborted(out bool aborted) => Trace("GetAnyOperationsAborted", native.GetAnyOperationsAborted(out aborted));
        public int Unadvise() => Trace("Unadvise", native.Unadvise());
        public void Dispose() { native.Dispose(); log("Native driver disposed"); }
        public void UninitializeCom() { native.UninitializeCom(); log("COM uninitialized"); }
        private int Trace(string step, int result) { log($"Native step {step}: {result:X8}"); return result; }
    }

    private static nint ItemPointer(NativeFileOperationDriver driver) =>
        (nint)typeof(NativeFileOperationDriver).GetField("itemPointer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(driver)!;

    private static int ProbeDirectoryAccess(ActionFileFixture fixture, string path, string phase)
    {
        var volumeRoot = Path.GetPathRoot(fixture.Root)!;
        Assert.True(path == fixture.Root || path == volumeRoot);
        if (path == fixture.Root) fixture.Check(path, allowRoot: true);
        Assert.Equal((FileAttributes)0, File.GetAttributes(path) & FileAttributes.ReparsePoint);
        // OPEN_EXISTING only. Request FILE_ADD_FILE to observe share compatibility;
        // never create a child, write bytes, set metadata, delete, or rename anything.
        using var handle = NativeMethods.CreateFile(WindowsScopeNative.ToExtendedLengthPath(path), 0x2,
            FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x02000000 | 0x00200000, 0);
        var error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        TestContext.Current.TestOutputHelper?.WriteLine($"Read-only directory access probe: phase={phase}; target={(path == volumeRoot ? "volume root" : "fixture root")}; Win32={error}; OPEN_EXISTING; no mutation");
        return error;
    }

    private static int ProbeFileDeleteAccess(ActionFileFixture fixture, string path, string phase)
    {
        fixture.Check(path);
        Assert.Equal((FileAttributes)0, File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint));
        // Request DELETE access only; never set disposition or rename anything.
        using var handle = NativeMethods.CreateFile(WindowsScopeNative.ToExtendedLengthPath(path), 0x00010000,
            FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x00200000, 0);
        var error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        TestContext.Current.TestOutputHelper?.WriteLine($"Read-only fixture-file DELETE access probe: phase={phase}; Win32={error}; OPEN_EXISTING; no mutation");
        return error;
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsRecycleBinIsolationDefinition
{
    public const string Name = "Windows Recycle Bin native";
}
