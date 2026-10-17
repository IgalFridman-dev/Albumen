using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Action;
using PhotoSift.Infrastructure.Windows.Scan;
using PhotoSift.Infrastructure.Windows.Scope;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.ActionExecution;

[Trait("Category", "GoldenVm")]
public sealed class WindowsActionLeaseIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SelectedIsProtectedUntilSinglePermissionAndWitnessSurvives(bool hardLink, bool longPath)
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create(longPath ? Path.Combine(new string('a', 100), new string('b', 100), "תמונה-日本語.jpg") : "selected.jpg");
        var witness = hardLink ? fixture.Link("witness.jpg", selected) : fixture.Create("witness.jpg");
        var envelope = fixture.Envelope(selected, witness);
        var lease = await WindowsActionLease.OpenAsync(envelope, CancellationToken.None);
        try
        {
            Assert.Equal(32, fixture.Delete(selected));
            fixture.AssertWriteDenied(selected);
            fixture.AssertWriteDenied(witness);
            Assert.Equal(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
            Assert.NotEqual(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
            Assert.Equal(0, fixture.Delete(selected));
            Assert.True(File.Exists(witness));
            if (!hardLink) fixture.AssertWriteDenied(witness);
        }
        finally { lease.Dispose(); lease.Dispose(); }
        fixture.AssertWritable(witness);
    }

    [Fact]
    public async Task PreparationRehashRejectsDistinctWitnessWithUnchangedApprovedMetadata()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var witness = fixture.Create("witness.jpg");
        var envelope = fixture.Envelope(selected, witness);
        var approved = envelope.Observations[1];
        Assert.NotEqual(envelope.Item.PhysicalFileKey, approved.Final.PhysicalFileKey);
        fixture.WritePreservingApprovedTimestamp(witness, "changed", approved);
        fixture.AssertOnlyDigestChanged(witness, approved);
        var failure = await Assert.ThrowsAsync<WindowsActionLeaseException>(async () =>
        {
            // Unexpected successful preparation is disposed even if the assertion
            // fails; no callback or shell permission is attempted for changed bytes.
            using var unexpected = await WindowsActionLease.OpenAsync(envelope, CancellationToken.None);
        });
        Assert.Equal(ActionSafetyReason.Changed, failure.Reason);
        Assert.True(File.Exists(selected));
        Assert.True(File.Exists(witness));
        fixture.AssertOnlyDigestChanged(witness, approved);
        fixture.AssertWritable(selected);
        fixture.AssertWritable(witness);
    }

    [Theory]
    [InlineData("missing", ActionSafetyReason.Missing)]
    [InlineData("replaced", ActionSafetyReason.Changed)]
    [InlineData("readonly", ActionSafetyReason.ReadOnly)]
    [InlineData("locked", ActionSafetyReason.SharingViolation)]
    public async Task UnsafeOriginalCannotAcquirePermission(string change, ActionSafetyReason reason)
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var envelope = fixture.Envelope(selected, fixture.Create("witness.jpg"));
        FileStream? blocker = null;
        try
        {
            if (change is "missing" or "replaced") { Assert.Equal(0, fixture.Delete(selected)); }
            if (change == "replaced") fixture.Create("selected.jpg", "different");
            if (change == "readonly") fixture.Attributes(selected, FileAttributes.ReadOnly);
            if (change == "locked") blocker = fixture.Writer(selected);
            var failure = await Assert.ThrowsAsync<WindowsActionLeaseException>(() => WindowsActionLease.OpenAsync(envelope, CancellationToken.None));
            Assert.Equal(reason, failure.Reason);
            Assert.Equal(change != "missing", File.Exists(selected));
        }
        finally { blocker?.Dispose(); if (File.Exists(selected)) fixture.Attributes(selected, FileAttributes.Normal); }
    }

    [Fact]
    public async Task RealFileReparseReplacementIsDeniedWithoutTouchingEitherEntry()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var witness = fixture.Create("witness.jpg");
        var envelope = fixture.Envelope(selected, witness);
        Assert.Equal(0, fixture.Delete(selected));
        fixture.SymbolicFile(selected, witness);
        try
        {
            var failure = await Assert.ThrowsAsync<WindowsActionLeaseException>(() => WindowsActionLease.OpenAsync(envelope, CancellationToken.None));
            Assert.Equal(ActionSafetyReason.UnsafeLocation, failure.Reason);
            Assert.True(File.Exists(selected));
            Assert.Equal("fixture", File.ReadAllText(witness));
        }
        finally { fixture.RemoveSymbolicFile(selected); }
    }
}

// Every mutation is confined to a unique child of the controller's current
// disposable guest run, never its submitted input, source, profile or libraries.
internal sealed class ActionFileFixture : IDisposable
{
    internal string Root { get; }
    private readonly WindowsFileNative native = new();
    internal ActionFileFixture()
    {
        var source = new DirectoryInfo(AppContext.BaseDirectory);
        while (source is not null && source.Name != "source") source = source.Parent;
        var run = source?.Parent ?? throw new InvalidOperationException("Not in a disposable controller run.");
        Assert.Equal(@"C:\PhotoSift\runs", run.Parent!.FullName, ignoreCase: true);
        Assert.Matches(@"^\d{8}-\d{6}-architecture-[a-f0-9]{8}$|^\d{8}-\d{6}-full-[a-f0-9]{8}$", run.Name);
        Root = Path.Combine(run.FullName, "action-fixture-" + Guid.NewGuid().ToString("N"));
        Check(Root, allowRoot: true);
        Directory.CreateDirectory(Root);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Disposable Action fixture id={Path.GetFileName(Root)}");
    }
    internal string PathFor(string relative) { var path = Path.GetFullPath(Path.Combine(Root, relative)); Check(path); return path; }
    internal void Check(string path, bool allowRoot = false)
    {
        var full = Path.GetFullPath(path);
        Assert.True(full.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase) || allowRoot && full == Root);
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
            if (File.Exists(current) || Directory.Exists(current))
                Assert.Equal((FileAttributes)0, File.GetAttributes(current) & FileAttributes.ReparsePoint);
    }
    internal string Create(string relative, string text = "fixture")
    {
        var path = PathFor(relative); Check(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); Write(path, text); return path;
    }
    internal void Write(string path, string text) { Check(path); File.WriteAllText(path, text); }
    internal void WritePreservingApprovedTimestamp(string path, string text, ActionProbeObservation approved)
    {
        Assert.NotNull(approved.Final.LastWriteUtc);
        Write(path, text);
        Check(path);
        File.SetLastWriteTimeUtc(path, approved.Final.LastWriteUtc.Value.UtcDateTime);
    }
    internal void AssertOnlyDigestChanged(string path, ActionProbeObservation approved)
    {
        Check(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var current = native.Read(stream.SafeFileHandle, path);
        Assert.Equal(approved.Final.PhysicalFileKey, current.PhysicalFileKey);
        Assert.Equal(approved.Final.ResolvedPath, current.ResolvedPath);
        Assert.Equal(approved.Final.ByteLength, current.ByteLength);
        Assert.Equal(approved.Final.Allocation, current.Allocation);
        Assert.NotNull(approved.Final.LastWriteUtc);
        Assert.Equal(approved.Final.LastWriteUtc, current.LastWriteUtc);
        Assert.NotEqual(approved.Digest.Sha256, Convert.ToHexString(SHA256.HashData(stream)));
        TestContext.Current.TestOutputHelper?.WriteLine("Hash isolation proof: approved identity/path/length/allocation/link count/last-write timestamp match; SHA-256 differs.");
    }
    internal void Attributes(string path, FileAttributes attributes) { Check(path); File.SetAttributes(path, attributes); }
    internal FileStream Writer(string path) { Check(path); return new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
    internal void AssertWriteDenied(string path) => Assert.Throws<IOException>(() => { using var writer = Writer(path); });
    internal void AssertWritable(string path) { using var writer = Writer(path); }
    internal int Delete(string path) { Check(path); return DeleteFile(@"\\?\" + path) ? 0 : Marshal.GetLastPInvokeError(); }
    internal int Move(string from, string to) { Check(from); Check(to); return MoveFile(@"\\?\" + from, @"\\?\" + to) ? 0 : Marshal.GetLastPInvokeError(); }
    internal string Link(string relative, string existing)
    {
        var path = PathFor(relative); Check(existing); Check(path);
        if (!CreateHardLink(@"\\?\" + path, @"\\?\" + existing, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return path;
    }
    internal void SymbolicFile(string path, string target)
    {
        Check(path); Check(target); File.CreateSymbolicLink(path, target);
    }
    internal void RemoveSymbolicFile(string path)
    {
        Assert.StartsWith(Root + "\\", Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        Check(Path.GetDirectoryName(path)!, allowRoot: true);
        var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
        Assert.NotNull(target);
        Check(target.FullName);
        // Remove the verified link entry itself, never recurse or follow it.
        File.Delete(path);
    }
    internal ActionSafetyEnvelope Envelope(string selected, params string[] witnesses)
    {
        var scopeNative = new WindowsScopeNative(new WicCodecCapability(new ComClassAvailability()));
        var directory = scopeNative.InspectDirectory(Root);
        var pin = new ScanPin(Guid.NewGuid(), 1, 1, 1);
        var scope = new ResolvedScope(new ScopeSelection(Root, ScopeRecursion.IncludeSubfolders), Root, directory.Identity);
        var observations = new[] { selected }.Concat(witnesses).Select((path, index) =>
        {
            Check(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var facts = native.Read(stream.SafeFileHandle, path);
            TestContext.Current.TestOutputHelper?.WriteLine($"Protected original fact: {Path.GetRelativePath(Root, path)}; physical key {facts.PhysicalFileKey}; links {facts.Allocation.LinkCount}");
            var digest = Convert.ToHexString(SHA256.HashData(stream));
            var content = new ContentRevision(facts.PhysicalFileKey!.Value, stream.Length, digest);
            var success = new StageOutcome(ScanStageState.Succeeded, ScanReason.None);
            var occurrence = new ScanFileOccurrence(new(pin, index + 1, 1, 1), path, path, facts.PhysicalFileKey, success, success, content, facts.Allocation);
            var protectedFacts = new ProtectedFileFacts(content.PhysicalFileKey, content.ByteLength, path, facts.LastWriteUtc, facts.Allocation);
            return new ActionProbeObservation(occurrence, protectedFacts, protectedFacts, new(content.ByteLength, digest));
        }).ToImmutableArray();
        var members = observations.Select(value => value.ApprovedOccurrence).ToImmutableArray();
        var representatives = members.GroupBy(member => member.PhysicalFileKey!.Value)
            .Select(group => group.First()).ToArray();
        var equality = representatives.Skip(1).Select(member => new EqualityEvidence(
            representatives[0].Token, member.Token,
            representatives[0].Content!, member.Content!)).ToImmutableArray();
        var group = new CandidateGroup(Guid.NewGuid(), 1, pin, CandidateCategory.Exact,
            members, equality, false, null, new(0, RecoveryUnavailableReason.None));
        var first = members[0];
        var item = new ApprovedActionItem(group.GroupId, 1, first.Token, selected, selected, first.PhysicalFileKey!.Value, first.Content!, first.Allocation, null);
        var request = new ApprovedActionRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), 1, pin, scope, [item], [1],
            new(1, 1, ScanStatus.Complete, new(0, 0, 0, 0, 0, 0, 0), new(0, SelectionRecoveryUnavailableReason.None), "Windows Recycle Bin", "Revalidate"));
        var input = new ActionSafetyInput(group, members, members.Select(value => value.Token.OccurrenceId).ToImmutableHashSet(), [1], [], false, false);
        return new(new(new(Guid.NewGuid()), request), item, input, ActionSafetyPolicy.Evaluate(input), observations, []);
    }
    public void Dispose()
    {
        Check(Root, allowRoot: true);
        foreach (var path in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories)) Check(path);
        Directory.Delete(Root, recursive: true);
        Assert.False(Directory.Exists(Root));
        TestContext.Current.TestOutputHelper?.WriteLine("Disposable Action fixture removed.");
    }
#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteFile(string path);
    [DllImport("kernel32.dll", EntryPoint = "MoveFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool MoveFile(string from, string to);
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string path, string existing, nint reserved);
#pragma warning restore SYSLIB1054
}
