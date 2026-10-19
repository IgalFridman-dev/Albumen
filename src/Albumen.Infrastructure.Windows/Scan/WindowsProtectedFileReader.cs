using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using Albumen.Application.Scan;

namespace Albumen.Infrastructure.Windows.Scan;

public sealed class WindowsProtectedFileReader : IProtectedFileReader
{
    private readonly WindowsFileFactsReader facts;

    public WindowsProtectedFileReader()
        : this(new WindowsFileNative())
    {
    }

    internal WindowsProtectedFileReader(IWindowsFileFactsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        facts = new WindowsFileFactsReader(source);
    }

    public ValueTask<IProtectedFileLease> OpenAsync(ProtectedOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        using var chain = facts.ValidateScope(request, openedChain: null, cancellationToken);
        var path = WindowsFileFactsReader.NormalizePath(request.ResolvedPath);
        // Establish locality before acquiring a data-read handle. Both opens avoid recalling
        // remote content; the metadata open also pins the final name through validation.
        using var metadata = WindowsFileNative.OpenMetadata(path);
        var initial = facts.ReadContent(metadata, request);
        SafeFileHandle? handle = WindowsFileNative.OpenContent(path);
        FileStream? content = null;
        try
        {
            var opened = facts.ReadContent(handle, request, initial);
            cancellationToken.ThrowIfCancellationRequested();
            // CreateFileW did not use FILE_FLAG_OVERLAPPED. FileStream must agree.
            content = new FileStream(handle, FileAccess.Read, bufferSize: 64 * 1024, isAsync: false);
            handle = null; // FileStream now owns the content handle.
            var lease = new WindowsProtectedFileLease(content, opened, request, facts,
                chain.Proofs.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
            content = null;
            return ValueTask.FromResult<IProtectedFileLease>(lease);
        }
        finally
        {
            content?.Dispose();
            handle?.Dispose();
        }
    }
}
