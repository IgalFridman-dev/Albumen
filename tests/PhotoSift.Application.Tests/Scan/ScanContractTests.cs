using System.Collections.Immutable;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using Xunit;

namespace PhotoSift.Application.Tests.Scan;

public sealed class ScanContractTests
{
    private static readonly ScanPin Pin = new(
        new Guid("11111111-1111-1111-1111-111111111111"),
        2,
        3,
        4);

    [Fact]
    public void EnumerationContractsKeepDiscoveredAndResolvedPathsSeparate()
    {
        var key = new PhysicalFileKey(7, new FileId128(8, 9));
        var outcome = new StageOutcome(ScanStageState.Succeeded, ScanReason.None);
        var file = new EnumeratedFile(@"C:\Selected\one.jpg", @"C:\Resolved\one.jpg", key, outcome);

        ScanEnumerationEvent found = new ScanFileFound(file);
        ScanEnumerationEvent ended = new ScanEnumerationEnded(outcome);

        Assert.Equal(@"C:\Selected\one.jpg", Assert.IsType<ScanFileFound>(found).File.DiscoveredPath);
        Assert.Equal(@"C:\Resolved\one.jpg", Assert.IsType<ScanFileFound>(found).File.ResolvedPath);
        Assert.Equal(outcome, Assert.IsType<ScanEnumerationEnded>(ended).Outcome);
    }

    [Fact]
    public void ProtectedReadContractsRetainOnlyValidatedValueFacts()
    {
        var context = Context();
        var key = new PhysicalFileKey(7, new FileId128(8, 9));
        var allocation = new AllocationEvidence(4096, 1, true);
        var facts = new ProtectedFileFacts(key, 3, @"C:\Resolved\one.jpg", DateTimeOffset.UnixEpoch, allocation);
        var request = new ProtectedOpenRequest(context, @"C:\Selected\one.jpg", facts.ResolvedPath, key);

        Assert.Equal(context, request.Context);
        Assert.Equal(key, facts.PhysicalFileKey);
        Assert.Equal(3, facts.ByteLength);
        Assert.Equal(allocation, facts.Allocation);
    }

    [Fact]
    public void ScanReadExceptionPreservesTypedOutcomeAndInnerException()
    {
        var outcome = new StageOutcome(ScanStageState.FailedRetryable, ScanReason.SharingViolation);
        var inner = new IOException("sharing conflict");

        var exception = new ScanReadException(outcome, "Could not open file.", inner);

        Assert.Equal(outcome, exception.Outcome);
        Assert.Equal("Could not open file.", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void WorkerMessagesRetainPinnedRevisionEvidence()
    {
        var context = Context();
        var key = new PhysicalFileKey(7, new FileId128(8, 9));
        var token = new OccurrenceToken(Pin, 1, 2, 3);
        var outcome = new StageOutcome(ScanStageState.Succeeded, ScanReason.None);
        var file = new EnumeratedFile(@"C:\Selected\one.jpg", @"C:\Resolved\one.jpg", key, outcome);
        var work = new OccurrenceWork(context, token, file);
        var content = new ContentRevision(
            key,
            3,
            "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD");
        var facts = new ProtectedFileFacts(key, 3, file.ResolvedPath, DateTimeOffset.UnixEpoch, new AllocationEvidence(4096, 1, true));
        var signature = new SignatureEvidence(token, content, facts, facts);
        var comparisonWork = new ComparisonWork(work, content, work, content);
        var streamDigest = new StreamDigest(3, content.Sha256);
        var streamEquality = new StreamEquality(true, streamDigest, streamDigest);
        var comparison = new ComparisonEvidence(comparisonWork, true, facts, facts, streamEquality);
        var commit = new SignatureCommit(true, ImmutableArray.Create(comparisonWork));

        Assert.Equal(Pin, signature.Token.Pin);
        Assert.Equal(content, signature.Content);
        Assert.True(comparison.Equal);
        Assert.True(commit.Accepted);
        Assert.Single(commit.Comparisons);
    }

    [Fact]
    public void ScanSnapshotRetainsImmutableRowsGroupsAndDerivedCountValues()
    {
        var snapshot = new ScanSnapshot(
            Pin,
            ScanStatus.Running,
            new StageOutcome(ScanStageState.Running, ScanReason.None),
            ImmutableArray<ScanFileOccurrence>.Empty,
            ImmutableArray<CandidateGroup>.Empty,
            new ScanCounts(10, 4, 2, 1, 1, 2));

        Assert.Equal(Pin, snapshot.Pin);
        Assert.Equal(ScanStatus.Running, snapshot.Status);
        Assert.Equal(2, snapshot.Counts.Outstanding);
        Assert.True(snapshot.Occurrences.IsEmpty);
        Assert.True(snapshot.Groups.IsEmpty);
    }

    [Fact]
    public async Task PortsSupportAsynchronousDomainOnlyImplementations()
    {
        var context = Context();
        var sink = new ContractSink();
        await foreach (var item in new ContractEnumerator().EnumerateAsync(context, TestContext.Current.CancellationToken))
        {
            Assert.IsType<ScanEnumerationEnded>(item);
        }

        await using var lease = await new ContractReader().OpenAsync(
            new ProtectedOpenRequest(
                context,
                @"C:\Selected\one.jpg",
                @"C:\Resolved\one.jpg",
                new PhysicalFileKey(7, new FileId128(8, 9))),
            TestContext.Current.CancellationToken);
        var digest = await new ContractAnalyzer().HashAsync(lease.Content, 0, TestContext.Current.CancellationToken);
        await using var run = new ContractExecutor().Start(context, sink);

        Assert.Equal(0, digest.ByteLength);
        Assert.Same(Stream.Null, lease.Content);
        Assert.True(run.Completion.IsCompletedSuccessfully);
    }

    private static ScanRunContext Context() =>
        new(
            Pin,
            new ResolvedScope(
                new ScopeSelection(@"C:\Selected", ScopeRecursion.IncludeSubfolders),
                @"C:\Resolved",
                new DirectoryIdentity(7, 8, 9)));

    private sealed class ContractEnumerator : IScanEnumerator
    {
        public async IAsyncEnumerable<ScanEnumerationEvent> EnumerateAsync(
            ScanRunContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ScanEnumerationEnded(new StageOutcome(ScanStageState.Succeeded, ScanReason.None));
        }
    }

    private sealed class ContractLease : IProtectedFileLease
    {
        public Stream Content => Stream.Null;

        public ProtectedFileFacts OpenedFacts { get; } = new(
            new PhysicalFileKey(7, new FileId128(8, 9)),
            0,
            @"C:\Resolved\one.jpg",
            DateTimeOffset.UnixEpoch,
            new AllocationEvidence(0, 1, true));

        public ValueTask<ProtectedFileFacts> RefreshAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(OpenedFacts);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ContractReader : IProtectedFileReader
    {
        public ValueTask<IProtectedFileLease> OpenAsync(
            ProtectedOpenRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IProtectedFileLease>(new ContractLease());
    }

    private sealed class ContractAnalyzer : IExactContentAnalyzer
    {
        public ValueTask<StreamDigest> HashAsync(
            Stream content,
            long expectedLength,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new StreamDigest(0, new string('0', 64)));

        public ValueTask<StreamEquality> CompareAsync(
            Stream left,
            Stream right,
            long expectedLength,
            CancellationToken cancellationToken)
        {
            var digest = new StreamDigest(0, new string('0', 64));
            return ValueTask.FromResult(new StreamEquality(true, digest, digest));
        }
    }

    private sealed class ContractSink : IScanResultSink
    {
        public ValueTask<OccurrenceWork?> RegisterAsync(ScanRunContext context, EnumeratedFile file) =>
            ValueTask.FromResult<OccurrenceWork?>(null);

        public ValueTask<SignatureCommit> CommitSignatureAsync(SignatureEvidence evidence) =>
            ValueTask.FromResult(new SignatureCommit(false, ImmutableArray<ComparisonWork>.Empty));

        public ValueTask<bool> CommitComparisonAsync(ComparisonEvidence evidence) => ValueTask.FromResult(false);

        public ValueTask<bool> FailAsync(OccurrenceToken token, StageOutcome outcome) => ValueTask.FromResult(false);

        public ValueTask EnumerationEndedAsync(ScanRunContext context, StageOutcome outcome) => ValueTask.CompletedTask;
    }

    private sealed class ContractRun : IScanRun
    {
        public Task Completion => Task.CompletedTask;

        public Task CancelAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ContractExecutor : IScanExecutor
    {
        public IScanRun Start(ScanRunContext context, IScanResultSink sink) => new ContractRun();
    }
}
