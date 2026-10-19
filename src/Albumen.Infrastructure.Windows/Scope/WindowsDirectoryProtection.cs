using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Albumen.Domain.Scope;
using Albumen.Infrastructure.Windows.Scan;

namespace Albumen.Infrastructure.Windows.Scope;

internal sealed partial class WindowsDirectoryProtection : IWindowsDirectoryProtection
{
    private const int BufferSize = 64 * 1024;
    private const int NameOffset = 88; // FILE_ID_EXTD_DIR_INFO.FileName
    private readonly ResolvedScope scope;
    private readonly WindowsDirectoryFacts expected;
    private readonly string discoveredPath;
    private readonly WindowsFileFactsReader facts;
    private readonly WindowsScopeReadGuard chain;
    private readonly ImmutableDictionary<string, WindowsDirectoryProof> proofs;
    private readonly SafeFileHandle directory;
    private bool disposed;

    internal WindowsDirectoryProtection(ResolvedScope scope, WindowsDirectoryFacts expected,
        string discoveredPath, IWindowsFileFactsSource source, CancellationToken cancellationToken)
    {
        this.scope = scope;
        this.expected = expected;
        this.discoveredPath = discoveredPath;
        facts = new WindowsFileFactsReader(source);
        chain = facts.ValidateDirectory(scope, discoveredPath, expected.ResolvedPath,
            expected.Identity, openedChain: null, cancellationToken);
        SafeFileHandle? opened = null;
        try
        {
            // The listing itself uses this retained no-recall handle, never a
            // fresh pathname lookup after releasing directory/ancestor proof.
            opened = WindowsFileNative.OpenDirectoryForEnumeration(expected.ResolvedPath);
            facts.ValidateDirectoryHandle(opened, expected);
            proofs = chain.Proofs.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
            directory = opened;
            opened = null;
        }
        catch
        {
            opened?.Dispose();
            chain.Dispose();
            throw;
        }
    }

    public void Validate(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        // Sharing does not freeze cloud/attribute metadata. Refresh the chain
        // while its original guards remain retained, and compare identity proofs.
        using var refreshed = facts.ValidateDirectory(scope, discoveredPath, expected.ResolvedPath,
            expected.Identity, proofs, cancellationToken);
        facts.ValidateDirectoryHandle(directory, expected);
    }

    public IEnumerable<WindowsScopeEntry> Enumerate(CancellationToken cancellationToken)
    {
        Validate(cancellationToken);
        var buffer = new byte[BufferSize];
        var restart = true;
        while (ReadBatch(buffer, restart))
        {
            restart = false;
            var offset = 0;
            while (true)
            {
                Validate(cancellationToken);
                var (entry, next) = ReadEntry(buffer, offset);
                if (entry is not null) yield return entry;
                if (next == 0) break;
                offset = checked(offset + next);
            }
            Validate(cancellationToken);
        }
        Validate(cancellationToken);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        directory.Dispose();
        chain.Dispose();
    }

    private unsafe bool ReadBatch(byte[] buffer, bool restart)
    {
        fixed (byte* address = buffer)
        {
            if (ReadDirectoryInformation(directory, restart ? 20 : 19, address, (uint)buffer.Length)) return true;
        }
        var error = Marshal.GetLastPInvokeError();
        if (error == NativeMethods.ErrorNoMoreFiles) return false;
        throw WindowsFileNative.Failure(error, expected.ResolvedPath);
    }

    private (WindowsScopeEntry? Entry, int Next) ReadEntry(byte[] buffer, int offset)
    {
        if (offset < 0 || offset > buffer.Length - NameOffset) throw new IOException("Invalid directory entry offset.");
        var data = buffer.AsSpan(offset);
        var next = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data));
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(data[56..]);
        var nameLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[60..]));
        var reparse = (attributes & NativeMethods.FileAttributeReparsePoint) != 0;
        var tag = reparse ? BinaryPrimitives.ReadUInt32LittleEndian(data[68..]) : 0;
        if (nameLength <= 0 || (nameLength & 1) != 0 || nameLength > data.Length - NameOffset
            || (next != 0 && (next < NameOffset + nameLength || next > data.Length - NameOffset || (next & 7) != 0)))
            throw new IOException("Invalid directory entry length.");
        var name = Encoding.Unicode.GetString(data.Slice(NameOffset, nameLength));
        if (name is "." or "..") return (null, next);
        if (name.AsSpan().IndexOfAny('\\', '/', ':') >= 0 || name.AsSpan().Contains('\0'))
            throw new IOException("Invalid directory entry name.");
        var cloud = WindowsScopeNative.MapCloudState(attributes,
            NativeMethods.CfGetPlaceholderStateFromAttributeTag(attributes, tag));
        return (new WindowsScopeEntry(Path.Combine(expected.ResolvedPath, name),
            (attributes & NativeMethods.FileAttributeDirectory) != 0, reparse, cloud), next);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadDirectoryInformation(SafeFileHandle handle, int informationClass,
        byte* information, uint bufferSize);
}
