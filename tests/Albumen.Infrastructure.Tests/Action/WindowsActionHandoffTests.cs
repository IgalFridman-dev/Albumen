using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Albumen.Application.Review;
using Albumen.Application.Action;
using Albumen.Domain.Action;
using Albumen.Infrastructure.Windows.Action;
using Albumen.Infrastructure.Windows.Scope;
using Albumen.Infrastructure.Windows.Scan;
using Xunit;

namespace Albumen.Infrastructure.Tests.ActionExecution;

[Collection(WindowsRecycleBinIsolationDefinition.Name)]
[Trait("Category", "GoldenVm")]
public sealed class WindowsActionHandoffTests
{
    [Fact]
    public async Task UnexpectedLatestValidatorExceptionReachesSinkAndOverridesAvailableRealUnchangedProof()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var witness = fixture.Create("witness.jpg");
        var envelope = fixture.Envelope(selected, witness);
        var armed = false;
        var source = new ActionObservedFacts((path, facts) => armed && path == selected
            ? throw new InvalidOperationException("Unexpected latest validator failure") : facts);
        var driver = new RecordingFileOperationDriver { BeforePermission = () => armed = true };
        RealLeaseWithProof? actual = null;
        var result = await StaFileOperationRunner.RunAsync(selected, () => driver,
            () => actual = new RealLeaseWithProof(WindowsActionLease.OpenAsync(envelope, CancellationToken.None, source).GetAwaiter().GetResult(), envelope),
            CancellationToken.None);
        // The source really is unchanged, and the unchanged-envelope proof really
        // succeeds. An unexpected callback exception must nevertheless stay unknown.
        using (var proof = await WindowsActionLease.OpenAsync(envelope, CancellationToken.None))
        {
            fixture.AssertWriteDenied(selected);
        }
        Assert.NotNull(actual);
        TestContext.Current.TestOutputHelper?.WriteLine($"Unexpected validator result: outcome={result.Outcome}; callbackException={result.Evidence.CallbackException}; proofCalls={actual.ProofCalls}; independent fresh proof succeeded");
        Assert.Equal(RecycleBinOutcomeKind.UnknownPendingRevalidation, result.Outcome);
        Assert.Equal(ActionSafetyReason.CallbackContradiction, result.Reason);
        Assert.True(result.Evidence.CallbackException);
        Assert.True(result.Evidence.NamespaceHandoffOccurred);
        Assert.False(result.Evidence.PermissionGranted);
        Assert.Equal(0, actual.ProofCalls);
    }

    [Fact]
    public async Task ExplicitHandoffKeepsContentProtectedAndStrictReacquisitionPrecedesEveryLatestRead()
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
                Assert.Equal(32, DeleteAccess(fixture, selected));
                fixture.AssertWriteDenied(selected);
                fixture.AssertWriteDenied(witness);
                trace.Add(path);
            }
            return facts;
        });
        using var lease = await WindowsActionLease.OpenAsync(fixture.Envelope(selected, witness), CancellationToken.None, source);
        Assert.False(lease.NamespaceHandoffOccurred);
        Assert.Equal(32, DeleteAccess(fixture, selected));
        Assert.Equal(ActionSafetyReason.None, lease.PrepareForPerformOperations());
        Assert.True(lease.NamespaceHandoffOccurred);
        fixture.AssertWriteDenied(selected);
        Assert.Equal(0, DeleteAccess(fixture, selected));
        Assert.Equal(32, DeleteAccess(fixture, witness));
        callback = true;
        Assert.Equal(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
        Assert.Contains(selected, trace);
        Assert.Contains(witness, trace);
        Assert.Contains(fixture.Root, trace);
        fixture.AssertWritable(selected);
        Assert.Equal(0, DeleteAccess(fixture, selected));
        fixture.AssertWriteDenied(witness);
        Assert.True(lease.NamespaceHandoffOccurred);
        Assert.NotEqual(ActionSafetyReason.None, lease.PrepareForPerformOperations());
        Assert.NotEqual(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
    }

    [Fact]
    public async Task StrictReacquisitionFailureDeniesWithoutDroppingContinuousWriteProtection()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var witness = fixture.Create("witness.jpg");
        using var lease = await WindowsActionLease.OpenAsync(fixture.Envelope(selected, witness), CancellationToken.None);
        Assert.Equal(ActionSafetyReason.None, lease.PrepareForPerformOperations());
        fixture.Check(selected);
        using (var deleteAccess = NativeMethods.CreateFile(WindowsScopeNative.ToExtendedLengthPath(selected), 0x00010000,
            FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x00200000, 0))
        {
            Assert.False(deleteAccess.IsInvalid);
            Assert.Equal(ActionSafetyReason.SharingViolation, lease.ValidateAndReleaseForPreDelete());
            fixture.AssertWriteDenied(selected);
            fixture.AssertWriteDenied(witness);
            Assert.True(File.Exists(selected));
        }
        Assert.NotEqual(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
        Assert.True(lease.NamespaceHandoffOccurred);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalNameMissingOrReplacedDuringHandoffCannotReceivePermission(bool replace)
    {
        using var fixture = new ActionFileFixture();
        var control = fixture.Create("rename-control.jpg");
        var controlDestination = fixture.PathFor("renamed-control.jpg");
        Assert.Equal(0, RenameWithinFixture(fixture, control, controlDestination));
        Assert.False(File.Exists(control));
        Assert.True(File.Exists(controlDestination));
        var selected = fixture.Create("selected.jpg");
        var moved = fixture.PathFor("moved.jpg");
        var witness = fixture.Create("witness.jpg");
        var originalKey = ReadIdentity(selected);
        using var lease = await WindowsActionLease.OpenAsync(fixture.Envelope(selected, witness), CancellationToken.None);
        Assert.Equal(ActionSafetyReason.None, lease.PrepareForPerformOperations());
        Assert.Equal(0, RenameWithinFixture(fixture, selected, moved));
        Assert.False(File.Exists(selected));
        Assert.True(File.Exists(moved));
        Assert.Equal(originalKey, ReadIdentity(moved));
        if (replace) fixture.Create("selected.jpg");
        if (replace) Assert.NotEqual(originalKey, ReadIdentity(selected));
        Assert.Equal(replace ? ActionSafetyReason.Changed : ActionSafetyReason.Missing, lease.ValidateAndReleaseForPreDelete());
        fixture.AssertWriteDenied(moved);
        fixture.AssertWriteDenied(witness);
        Assert.Equal("fixture", File.ReadAllText(moved));
        Assert.Equal(replace, File.Exists(selected));
        Assert.NotEqual(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());

        Albumen.Domain.Scan.PhysicalFileKey? ReadIdentity(string path)
        {
            fixture.Check(path);
            using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var key = new WindowsFileNative().Read(read.SafeFileHandle, path).PhysicalFileKey;
            Assert.NotNull(key);
            return key;
        }
    }

    [Theory]
    [InlineData("selected", "identity")]
    [InlineData("selected", "content")]
    [InlineData("selected", "links")]
    [InlineData("selected", "unknownLinks")]
    [InlineData("selected", "allocation")]
    [InlineData("selected", "sync")]
    [InlineData("selected", "placeholder")]
    [InlineData("selected", "readonly")]
    [InlineData("selected", "path")]
    [InlineData("ancestor", "identity")]
    [InlineData("ancestor", "reparse")]
    [InlineData("witness", "identity")]
    [InlineData("witness", "content")]
    [InlineData("witness", "links")]
    [InlineData("witness", "unknown")]
    public async Task HandoffDoesNotRelaxLatestSelectedAncestorOrWitnessChecks(string target, string change)
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create(Path.Combine("album", "selected.jpg"));
        var witness = fixture.Create("witness.jpg");
        var observedPath = target switch { "selected" => selected, "witness" => witness, _ => Path.GetDirectoryName(selected)! };
        var armed = false;
        var source = new ActionObservedFacts((path, facts) => armed && path == observedPath
            ? WindowsActionRaceTests.Alter(facts, change) : facts);
        using var lease = await WindowsActionLease.OpenAsync(fixture.Envelope(selected, witness), CancellationToken.None, source);
        Assert.Equal(ActionSafetyReason.None, lease.PrepareForPerformOperations());
        armed = true;
        Assert.NotEqual(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
        // Even denied latest checks retain the reacquired strict handle until cleanup.
        Assert.Equal(32, DeleteAccess(fixture, selected));
        fixture.AssertWriteDenied(selected);
        fixture.AssertWriteDenied(witness);
        armed = false;
        Assert.NotEqual(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
    }

    private static int DeleteAccess(ActionFileFixture fixture, string path)
    {
        fixture.Check(path);
        using var handle = NativeMethods.CreateFile(WindowsScopeNative.ToExtendedLengthPath(path), 0x00010000,
            FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x00200000, 0);
        // OPEN_EXISTING access observation only, never disposition or rename.
        return handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
    }

    private sealed class RealLeaseWithProof(WindowsActionLease lease, ActionSafetyEnvelope envelope) : IRecycleBinLease
    {
        internal int ProofCalls { get; private set; }
        public bool NamespaceHandoffOccurred => lease.NamespaceHandoffOccurred;
        public ActionSafetyReason PrepareForPerformOperations() => lease.PrepareForPerformOperations();
        public ActionSafetyReason ValidateAndReleaseForPreDelete() => lease.ValidateAndReleaseForPreDelete();
        public bool ProveOriginalUnchanged()
        {
            ProofCalls++;
            using var proof = WindowsActionLease.OpenAsync(envelope, CancellationToken.None).GetAwaiter().GetResult();
            return true;
        }
        public void Dispose() => lease.Dispose();
    }

    private static int RenameWithinFixture(ActionFileFixture fixture, string source, string destination)
    {
        fixture.Check(source);
        fixture.Check(destination);
        Assert.Equal(fixture.Root, Path.GetDirectoryName(source));
        Assert.Equal(fixture.Root, Path.GetDirectoryName(destination));
        Assert.False(File.Exists(destination));
        // Open only DELETE on the exact contained source. Unlike a higher-level
        // move helper this makes requested access and destination resolution explicit.
        using var sourceHandle = NativeMethods.CreateFile(WindowsScopeNative.ToExtendedLengthPath(source), 0x00010000,
            FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, 0x00200000, 0);
        var error = sourceHandle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        TestContext.Current.TestOutputHelper?.WriteLine($"Contained rename source DELETE open: Win32={error}");
        if (error != 0) return error;
        // Win64 FILE_RENAME_INFO: flags/BOOLEAN at 0, HANDLE at 8, byte length at
        // 16, UTF-16 name at 20. MS-FSA FileRenameInformation resolves a basename
        // with NULL root against the source's existing parent, without opening a
        // destination directory for FILE_ADD_FILE. No process-current-directory
        // change or guard release is involved.
        // ReplaceIfExists remains false; no other entry can be overwritten.
        Assert.Equal(8, IntPtr.Size);
        var destinationName = Path.GetFileName(destination);
        Assert.False(Path.IsPathRooted(destinationName));
        Assert.DoesNotContain('\\', destinationName);
        Assert.DoesNotContain('/', destinationName);
        Assert.Equal(destination, Path.Combine(fixture.Root, destinationName));
        var name = Encoding.Unicode.GetBytes(destinationName);
        var bytes = new byte[24 + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), checked((uint)name.Length));
        name.CopyTo(bytes, 20);
        var information = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, information, bytes.Length);
            Assert.Equal(16, Marshal.SizeOf<IoStatusBlock>());
            // CreateFile omitted FILE_FLAG_OVERLAPPED: this is a synchronous
            // handle, so the native request completes before these buffers die.
            var status = NtSetInformationFile(sourceHandle, out var io, information, checked((uint)bytes.Length), 10);
            TestContext.Current.TestOutputHelper?.WriteLine($"Contained NT basename rename: NTSTATUS=0x{status:X8}; IO status=0x{io.Status:X}; information={io.Information}; no replacement; all guards retained");
            return status == 0 ? 0 : unchecked((int)RtlNtStatusToDosError(status));
        }
        finally { Marshal.FreeHGlobal(information); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal nint Status;
        internal nuint Information;
    }

#pragma warning disable SYSLIB1054 // Same test-only mutation import convention as ActionFileFixture; tests do not enable unsafe code.
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock ioStatus, nint information, uint size, int informationClass);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
#pragma warning restore SYSLIB1054
}
