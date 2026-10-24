using Albumen.Application.Scan;
using Albumen.Application.Shell;
using Albumen.Domain.Scan;
using Albumen.Domain.Workflow;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

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
        var lookAlike = Succeeded(pin, 8, @"C:\Photos\look-alike.jpg", 8);
        var maybeSimilar = Succeeded(pin, 9, @"C:\Photos\maybe-similar.jpg", 9);
        var both = Succeeded(pin, 10, @"C:\Photos\both.jpg", 10);
        // A photo is counted once, in the first of Maybe similar, identical, look-alike: the candidate
        // (identical and look-alike) counts as identical; "both" (identical and Maybe similar) counts
        // as Uncertain, like every photo in a Maybe-similar set.
        var identicalGroup = Group(pin, CandidateCategory.Exact, uncertain: false, candidate, both);
        var lookAlikeGroup = Group(pin, CandidateCategory.NearDuplicate, uncertain: false, candidate, lookAlike);
        var uncertainGroup = Group(pin, CandidateCategory.Exact, uncertain: true, uncertain);
        var maybeSimilarGroup = Group(pin, CandidateCategory.Uncertain, uncertain: false, maybeSimilar, both);
        var scan = new ScanSnapshot(pin, ScanStatus.Incomplete,
            new(ScanStageState.Succeeded, ScanReason.None),
            [candidate, acceptable, uncertain, unsupported, canceled, failed, outstanding, lookAlike, maybeSimilar, both],
            [identicalGroup, lookAlikeGroup, uncertainGroup, maybeSimilarGroup], new(10, 6, 1, 1, 1, 1));

        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            CurrentPhase = WorkflowPhase.Review,
            Rules = new(1, 1),
            Scan = scan,
        });

        // No "Candidate" and no "Outstanding": the rows say where the photos are.
        Assert.Equal(new[]
        {
            ("In identical sets", 1L, "Review the set and choose manually."),
            ("In look-alike sets", 1L, "Review the set and choose manually."),
            ("Unique photos", 1L, "No duplicate candidate was found. Keep it, or scan again after the file changes."),
            ("Uncertain", 3L, "Review it before selecting; Albumen could not classify it confidently."),
            ("Unsupported", 1L, "Use a supported JPEG or PNG file, then start a fresh Scan."),
            ("Canceled/unprocessed", 1L, "Start a fresh Scan to analyze this file."),
            ("Failed", 1L, "Check file access and health, then start a fresh Scan."),
        }, fixture.ViewModel.ScanOutcomeSummaries.Select(item =>
            (item.Label, item.Count, item.NextStep)).ToArray());

        Assert.Equal(new[]
        {
            (@"C:\Photos\unsupported.gif", "Unsupported"),
            (@"C:\Photos\canceled.jpg", "Canceled/unprocessed"),
            (@"C:\Photos\failed.jpg", "Failed"),
            (@"C:\Photos\outstanding.jpg", "Not finished"),
        }, fixture.ViewModel.CouldNotAnalyze.Select(item =>
            (item.DiscoveredPath, item.OutcomeLabel)).ToArray());
        Assert.All(fixture.ViewModel.CouldNotAnalyze,
            item => Assert.False(string.IsNullOrWhiteSpace(item.NextStep)));
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = scan with
            {
                Status = ScanStatus.Complete,
                Occurrences = [candidate, acceptable, uncertain, unsupported, canceled, failed, lookAlike, maybeSimilar, both],
                Counts = new(9, 6, 1, 1, 1, 0),
            },
        });
        Assert.Equal(
            "Scan complete: every file in the folder was included. Unsupported, failed, and canceled files are listed separately.",
            fixture.ViewModel.CoverageStatus);
    }

    private static ScanFileOccurrence Succeeded(ScanPin pin, long id, string path, ulong fileId)
    {
        var key = new PhysicalFileKey(1, new(0, fileId));
        return new(new(pin, id, 1, 1), path, path, key,
            new(ScanStageState.Succeeded, ScanReason.None),
            new(ScanStageState.Succeeded, ScanReason.None),
            new(key, 10, new string("0123456789ABCDEF"[(int)id % 16], 64)), new(10, 1, true));
    }

    private static ScanFileOccurrence NotApplicable(
        ScanPin pin,
        long id,
        string path,
        StageOutcome prerequisite) => new(new(pin, id, 1, 1), path, path, null,
            prerequisite, new(ScanStageState.NotApplicable, prerequisite.Reason),
            null, new(null, null, false));

    private static CandidateGroup Group(ScanPin pin, CandidateCategory category, bool uncertain,
        params ScanFileOccurrence[] members) =>
        new(Guid.NewGuid(), 1, pin, category, [.. members], [], uncertain, null,
            new(null, RecoveryUnavailableReason.UnknownAllocation));
}
