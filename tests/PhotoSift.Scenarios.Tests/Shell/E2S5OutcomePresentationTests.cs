using PhotoSift.Application.Scan;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Workflow;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

public sealed class E2S5OutcomePresentationTests
{
    [Fact]
    public void ShippedOutcomeMatrixKeepsEveryCategoryDistinctWithLiteralNextSteps()
    {
        var fixture = new ExactShellFixture();
        var pin = new ScanPin(Guid.Parse("85000000-0000-0000-0000-000000000001"), 1, 1, 1);
        var candidate = Succeeded(pin, 1, @"C:\Photos\candidate.jpg", 1);
        var acceptable = Succeeded(pin, 2, @"C:\Photos\acceptable.jpg", 2);
        var uncertain = Succeeded(pin, 3, @"C:\Photos\uncertain.jpg", 3);
        var unsupported = NotApplicable(pin, 4, @"C:\Photos\unsupported.gif",
            new(ScanStageState.Unsupported, ScanReason.UnsupportedExtension));
        var canceled = NotApplicable(pin, 5, @"C:\Photos\canceled.jpg",
            new(ScanStageState.Canceled, ScanReason.Canceled));
        var failed = NotApplicable(pin, 6, @"C:\Photos\failed.jpg",
            new(ScanStageState.FailedRetryable, ScanReason.AccessDenied));
        var outstanding = new ScanFileOccurrence(new(pin, 7, 1, 1), @"C:\Photos\outstanding.jpg",
            @"C:\Photos\outstanding.jpg", null, new(ScanStageState.Succeeded, ScanReason.None),
            new(ScanStageState.Running, ScanReason.None), null, new(null, null, false));
        var candidateGroup = Group(pin, candidate, uncertain: false);
        var uncertainGroup = Group(pin, uncertain, uncertain: true);
        var scan = new ScanSnapshot(pin, ScanStatus.Incomplete,
            new(ScanStageState.Succeeded, ScanReason.None),
            [candidate, acceptable, uncertain, unsupported, canceled, failed, outstanding],
            [candidateGroup, uncertainGroup], new(7, 3, 1, 1, 1, 1));

        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            CurrentPhase = WorkflowPhase.Review,
            Rules = new(1, 1),
            Scan = scan,
        });

        Assert.Equal(new[]
        {
            ("Candidate", 1L, "Review the group and choose manually."),
            ("Analyzed acceptable", 1L, "No duplicate candidate was found. Keep it, or scan again after the file changes."),
            ("Uncertain", 1L, "Review it before selecting; PhotoSift could not classify it confidently."),
            ("Unsupported", 1L, "Use a supported JPEG or PNG file, then start a fresh Scan."),
            ("Canceled/unprocessed", 1L, "Start a fresh Scan to analyze this file."),
            ("Failed", 1L, "Check file access and health, then start a fresh Scan."),
            ("Outstanding", 1L, "Wait for this Scan to finish, or cancel it and start a fresh Scan."),
        }, fixture.ViewModel.ScanOutcomeSummaries.Select(item =>
            (item.Label, item.Count, item.NextStep)).ToArray());

        Assert.Equal(new[]
        {
            (@"C:\Photos\unsupported.gif", "Unsupported"),
            (@"C:\Photos\canceled.jpg", "Canceled/unprocessed"),
            (@"C:\Photos\failed.jpg", "Failed"),
            (@"C:\Photos\outstanding.jpg", "Outstanding"),
        }, fixture.ViewModel.CouldNotAnalyze.Select(item =>
            (item.DiscoveredPath, item.OutcomeLabel)).ToArray());
        Assert.All(fixture.ViewModel.CouldNotAnalyze,
            item => Assert.False(string.IsNullOrWhiteSpace(item.NextStep)));
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = scan with
            {
                Status = ScanStatus.Complete,
                Occurrences = [candidate, acceptable, uncertain, unsupported, canceled, failed],
                Counts = new(6, 3, 1, 1, 1, 0),
            },
        });
        Assert.Equal(
            "Scan complete: every file in the folder was checked. Unsupported, failed, and canceled files are listed separately.",
            fixture.ViewModel.CoverageStatus);
    }

    private static ScanFileOccurrence Succeeded(ScanPin pin, long id, string path, ulong fileId)
    {
        var key = new PhysicalFileKey(1, new(0, fileId));
        return new(new(pin, id, 1, 1), path, path, key,
            new(ScanStageState.Succeeded, ScanReason.None),
            new(ScanStageState.Succeeded, ScanReason.None),
            new(key, 10, new string((char)('A' + id), 64)), new(10, 1, true));
    }

    private static ScanFileOccurrence NotApplicable(
        ScanPin pin,
        long id,
        string path,
        StageOutcome prerequisite) => new(new(pin, id, 1, 1), path, path, null,
            prerequisite, new(ScanStageState.NotApplicable, prerequisite.Reason),
            null, new(null, null, false));

    private static CandidateGroup Group(ScanPin pin, ScanFileOccurrence member, bool uncertain) =>
        new(Guid.NewGuid(), 1, pin, CandidateCategory.Exact, [member], [], uncertain, null,
            new(null, RecoveryUnavailableReason.UnknownAllocation));
}
