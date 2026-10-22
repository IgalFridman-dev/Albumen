using System.Collections.Immutable;
using Albumen.Domain.Scan;

namespace Albumen.Application.Scan;

public sealed record OccurrenceWork(
    ScanRunContext Context,
    OccurrenceToken Token,
    EnumeratedFile File);

public sealed record SignatureEvidence(
    OccurrenceToken Token,
    ContentRevision Content,
    ProtectedFileFacts Opened,
    ProtectedFileFacts Final);

public sealed record ComparisonWork(
    OccurrenceWork Left,
    ContentRevision LeftContent,
    OccurrenceWork Right,
    ContentRevision RightContent);

public sealed record ComparisonEvidence(
    ComparisonWork Work,
    bool Equal,
    ProtectedFileFacts LeftFinal,
    ProtectedFileFacts RightFinal,
    StreamEquality Content);

public sealed record SignatureCommit(
    bool Accepted,
    ImmutableArray<ComparisonWork> Comparisons);

public sealed record NearFeatureResult(
    OccurrenceToken Token,
    ContentRevision? Content,
    PerceptualSignature? Signature,
    StageOutcome Outcome,
    double? Sharpness = null);

public sealed record NearDuplicateBatch(
    ScanPin Pin,
    string ImplementationRevision,
    ImmutableArray<NearFeatureResult> Results);

public sealed record NearDuplicateCommit(bool Accepted);

public interface IScanResultSink
{
    ValueTask<OccurrenceWork?> RegisterAsync(ScanRunContext context, EnumeratedFile file);

    ValueTask<SignatureCommit> CommitSignatureAsync(SignatureEvidence evidence);

    ValueTask<bool> CommitComparisonAsync(ComparisonEvidence evidence);

    ValueTask<bool> FailAsync(OccurrenceToken token, StageOutcome outcome);

    ValueTask EnumerationEndedAsync(ScanRunContext context, StageOutcome outcome);
}

public interface IScanRun : IAsyncDisposable
{
    Task Completion { get; }

    Task CancelAsync();
}

public interface IScanExecutor
{
    IScanRun Start(ScanRunContext context, IScanResultSink sink);
}
