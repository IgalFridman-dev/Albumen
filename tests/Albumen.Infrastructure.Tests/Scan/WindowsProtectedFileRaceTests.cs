using System.ComponentModel;
using System.Runtime.InteropServices;
using Albumen.Application.Scan;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Infrastructure.Windows.Scan;
using Albumen.Infrastructure.Windows.Scope;
using Xunit;

namespace Albumen.Infrastructure.Tests.Scan;

public sealed class WindowsProtectedFileRaceTests
{
    [Theory]
    [InlineData("write")]
    [InlineData("delete")]
    [InlineData("rename")]
    [InlineData("replace")]
    public async Task LiveLeaseDeniesConcurrentSourceMutationUntilRelease(string operation)
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile("protected.jpg");
        var replacement = fixture.CreateFile("replacement.jpg", "different");
        var renamed = fixture.PathFor("renamed.jpg");
        var request = fixture.Request(path);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contender = Task.Run(async () =>
        {
            await opened.Task.WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                if (operation == "write")
                {
                    Assert.Throws<IOException>(() =>
                    {
                        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete);
                    });
                }
                else
                {
                    var error = operation switch
                    {
                        "delete" => fixture.TryDelete(path),
                        "rename" => fixture.TryMove(path, renamed),
                        "replace" => fixture.TryReplace(path, replacement),
                        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
                    };
                    Assert.Equal(32, error);
                }
            }
            finally
            {
                attempted.TrySetResult();
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            await using var lease = await new WindowsProtectedFileReader().OpenAsync(request, CancellationToken.None);
            opened.SetResult();
            await attempted.Task.WaitAsync(TestContext.Current.CancellationToken);
            await contender;
            var final = await lease.RefreshAsync(CancellationToken.None);
            Assert.Equal(lease.OpenedFacts.PhysicalFileKey, final.PhysicalFileKey);
            Assert.Equal(7, final.ByteLength);
            using var text = new StreamReader(lease.Content, leaveOpen: true);
            Assert.Equal("fixture", await text.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            opened.TrySetCanceled(TestContext.Current.CancellationToken);
            await contender;
        }

        using var afterRelease = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
    }

    [Fact]
    public async Task AncestorRenameAndReplacementCannotLeaveAStaleSuccessfulRefresh()
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile(Path.Combine("album", "source.jpg"));
        var request = fixture.Request(path);
        await using var lease = await new WindowsProtectedFileReader().OpenAsync(request, CancellationToken.None);
        var oldAncestor = Path.GetDirectoryName(path)!;
        var movedAncestor = fixture.PathFor("moved-album");
        var error = fixture.TryMove(oldAncestor, movedAncestor);
        if (error == 0)
        {
            fixture.CreateFile(Path.Combine("album", "source.jpg"), "different");
            var failure = await Assert.ThrowsAsync<ScanReadException>(async () =>
                await lease.RefreshAsync(CancellationToken.None));
            Assert.Equal(new StageOutcome(ScanStageState.FailedRetryable, ScanReason.Changed), failure.Outcome);
        }
        else
        {
            Assert.True(error is 5 or 32, $"Expected access or sharing denial; native error was {error}.");
            var final = await lease.RefreshAsync(CancellationToken.None);
            Assert.Equal(lease.OpenedFacts.PhysicalFileKey, final.PhysicalFileKey);
            Assert.Equal(path, final.ResolvedPath);
            Assert.Equal(7, final.ByteLength);
            using var text = new StreamReader(lease.Content, leaveOpen: true);
            Assert.Equal("fixture", await text.ReadToEndAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ContainedDirectoryAliasIsValidatedAgainAfterRetargeting()
    {
        using var fixture = new ProtectedFileFixture();
        var path = fixture.CreateFile(Path.Combine("actual", "source.jpg"));
        fixture.CreateFile(Path.Combine("other", "source.jpg"), "different");
        var alias = fixture.PathFor("alias");
        Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(path)!);
        try
        {
            var request = fixture.Request(path) with { DiscoveredPath = Path.Combine(alias, "source.jpg") };
            await using var lease = await new WindowsProtectedFileReader().OpenAsync(request, CancellationToken.None);
            Directory.Delete(alias);
            Directory.CreateSymbolicLink(alias, fixture.PathFor("other"));
            var failure = await Assert.ThrowsAsync<ScanReadException>(async () =>
                await lease.RefreshAsync(CancellationToken.None));
            Assert.Equal(ScanReason.Changed, failure.Outcome.Reason);
        }
        finally
        {
            Directory.Delete(alias);
        }
    }
}

internal sealed class ProtectedFileFixture : IDisposable
{
    private readonly DirectoryInfo container = Directory.CreateTempSubdirectory("AlbumenProtected-");
    private readonly WindowsScopeNative native = new(new WicCodecCapability(new ComClassAvailability()));

    internal ProtectedFileFixture() => Directory.CreateDirectory(RootPath);

    internal string ContainerPath => container.FullName;
    private string RootPath => Path.Combine(container.FullName, "scope");

    internal string PathFor(string relative)
    {
        var path = Path.GetFullPath(Path.Combine(RootPath, relative));
        RequireContained(path);
        return path;
    }

    internal string CreateFile(string relative, string text = "fixture")
    {
        var path = PathFor(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    internal PhysicalFileKey Key(string path)
    {
        if (!Directory.Exists(path))
        {
            return native.InspectFileIdentity(path);
        }

        var identity = native.InspectDirectory(path).Identity;
        return new PhysicalFileKey(identity.VolumeSerialNumber,
            new FileId128(identity.FileIdHigh, identity.FileIdLow));
    }

    internal ProtectedOpenRequest Request(string path, ScopeRecursion recursion = ScopeRecursion.IncludeSubfolders)
    {
        RequireContained(path);
        var root = native.InspectDirectory(RootPath);
        return new ProtectedOpenRequest(
            new ScanRunContext(new ScanPin(Guid.NewGuid(), 1, 1, 1),
                new ResolvedScope(new ScopeSelection(RootPath, recursion), root.ResolvedPath, root.Identity)),
            path, path, Key(path));
    }

    internal string Copy(string relative, string existing)
    {
        RequireContained(existing);
        var path = PathFor(relative);
        File.Copy(existing, path);
        return path;
    }

    internal string HardLink(string relative, string existing)
    {
        RequireContained(existing);
        var path = PathFor(relative);
        if (!CreateHardLink(Extended(path), Extended(existing), 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return path;
    }

    internal void ReplaceRoot()
    {
        var previous = Path.Combine(ContainerPath, "previous");
        RequireContained(previous);
        Directory.Move(RootPath, previous);
        Directory.CreateDirectory(RootPath);
        CreateFile("root.jpg", "different");
    }

    internal int TryDelete(string path)
    {
        RequireContained(path);
        return DeleteFile(Extended(path)) ? 0 : Marshal.GetLastPInvokeError();
    }

    internal int TryMove(string path, string destination)
    {
        RequireContained(path);
        RequireContained(destination);
        return MoveFileEx(Extended(path), Extended(destination), 0) ? 0 : Marshal.GetLastPInvokeError();
    }

    internal int TryReplace(string path, string replacement)
    {
        RequireContained(path);
        RequireContained(replacement);
        return ReplaceFile(Extended(path), Extended(replacement), 0, 0, 0, 0)
            ? 0 : Marshal.GetLastPInvokeError();
    }

    private void RequireContained(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(ContainerPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Fixture operation escaped its dedicated container.");
        }
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(container.FullName);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !container.Name.StartsWith("AlbumenProtected-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Fixture cleanup target is invalid.");
        }

        container.Delete(recursive: true);
    }

    private static string Extended(string path) => @"\\?\" + path;

#pragma warning disable SYSLIB1054 // Test-only mutation imports; this test project does not enable unsafe code.
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFile(string fileName);

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "ReplaceFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReplaceFile(string replacedFileName, string replacementFileName,
        nint backupFileName, uint flags, nint exclude, nint reserved);
#pragma warning restore SYSLIB1054
}
