using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;

namespace PhotoSift.Application.Scan;

public sealed record ScanRunContext(ScanPin Pin, ResolvedScope Scope);

public sealed record EnumeratedFile(
    string DiscoveredPath,
    string ResolvedPath,
    PhysicalFileKey? PhysicalFileKey,
    StageOutcome Outcome);

public abstract record ScanEnumerationEvent;

public sealed record ScanFileFound(EnumeratedFile File) : ScanEnumerationEvent;

public sealed record ScanEnumerationEnded(StageOutcome Outcome) : ScanEnumerationEvent;

public interface IScanEnumerator
{
    IAsyncEnumerable<ScanEnumerationEvent> EnumerateAsync(
        ScanRunContext context,
        CancellationToken cancellationToken);
}

public sealed record ProtectedFileFacts(
    PhysicalFileKey PhysicalFileKey,
    long ByteLength,
    string ResolvedPath,
    DateTimeOffset? LastWriteUtc,
    AllocationEvidence Allocation);

public sealed record ProtectedOpenRequest(
    ScanRunContext Context,
    string DiscoveredPath,
    string ResolvedPath,
    PhysicalFileKey ExpectedPhysicalFileKey);

public interface IProtectedFileLease : IAsyncDisposable
{
    Stream Content { get; }

    ProtectedFileFacts OpenedFacts { get; }

    ValueTask<ProtectedFileFacts> RefreshAsync(CancellationToken cancellationToken);
}

public interface IProtectedFileReader
{
    ValueTask<IProtectedFileLease> OpenAsync(
        ProtectedOpenRequest request,
        CancellationToken cancellationToken);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1032:Implement standard exception constructors",
    Justification = "A scan read failure must always carry its typed outcome.")]
public sealed class ScanReadException : IOException
{
    public ScanReadException(
        StageOutcome outcome,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        Outcome = outcome;
    }

    public StageOutcome Outcome { get; }
}

public sealed record StreamDigest(long ByteLength, string Sha256);

public sealed record StreamEquality(bool Equal, StreamDigest Left, StreamDigest Right);

public interface IExactContentAnalyzer
{
    ValueTask<StreamDigest> HashAsync(
        Stream content,
        long expectedLength,
        CancellationToken cancellationToken);

    ValueTask<StreamEquality> CompareAsync(
        Stream left,
        Stream right,
        long expectedLength,
        CancellationToken cancellationToken);
}
