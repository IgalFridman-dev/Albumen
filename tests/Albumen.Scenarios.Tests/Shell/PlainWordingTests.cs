using Albumen.App.Shell;
using Albumen.Application.Scan;
using Albumen.Application.Semantic;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

// PM-009 wording pass: what people read uses plain words, not Exact / Near / Uncertain.
public sealed class PlainWordingTests
{
    [Theory]
    [InlineData(ReviewCategoryKind.Exact, "Identical copies")]
    [InlineData(ReviewCategoryKind.Near, "Look-alike")]
    [InlineData(ReviewCategoryKind.Uncertain, "Maybe similar")]
    [InlineData(ReviewCategoryKind.CouldNotAnalyze, "Couldn't check")]
    public void KindsUsePlainNames(ReviewCategoryKind category, string name) =>
        Assert.Equal(name, ReviewCategoryNames.For(category));

    // 1.1.5: the narrower "What was found" column says each kind in a few words.
    [Theory]
    [InlineData(ReviewCategoryKind.Exact, "The same file saved more than once.")]
    [InlineData(ReviewCategoryKind.Near, "The same photo resized, recompressed or lightly edited.")]
    [InlineData(ReviewCategoryKind.Uncertain, "Photos that look close.")]
    [InlineData(ReviewCategoryKind.CouldNotAnalyze, "Unsupported files.")]
    // Igal, 2026-10-25: "Matching photos…" alone didn't say matching what.
    [InlineData(ReviewCategoryKind.OtherMatches, "Photos that match the search but aren't in any set.")]
    public void CategoriesUseShortDescriptions(ReviewCategoryKind category, string description)
    {
        var row = new ReviewCategoryViewModel(category, ReviewCategoryNames.For(category), 0, 1, 4,
            _ => throw new InvalidOperationException("An empty kind has no sets."), _ => null);

        Assert.Equal(description, row.Description);
    }

    [Theory]
    [InlineData(ScanStatus.Idle, ScanStageState.Pending, 0L, "Not started")]
    [InlineData(ScanStatus.Checking, ScanStageState.Pending, 0L, "Getting ready: checking the folder")]
    [InlineData(ScanStatus.Running, ScanStageState.Running, 3L, "Scanning: finding photos")]
    [InlineData(ScanStatus.Running, ScanStageState.Succeeded, 3L, "Scanning: reading photos")]
    [InlineData(ScanStatus.Running, ScanStageState.Succeeded, 0L, "Scanning: finishing up")]
    [InlineData(ScanStatus.Complete, ScanStageState.Succeeded, 0L, "Finished: every file in the folder was included")]
    [InlineData(ScanStatus.Incomplete, ScanStageState.Succeeded, 0L, "Stopped early: some files weren't checked")]
    [InlineData(ScanStatus.Faulted, ScanStageState.Succeeded, 0L, "Stopped by an error: results may be incomplete")]
    public void ScanStatusNamesTheStageWithoutCounts(
        ScanStatus status, ScanStageState enumeration, long outstanding, string expected)
    {
        var fixture = new ExactShellFixture();
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = fixture.Workflow.Current.Scan with
            {
                Status = status,
                Enumeration = new(enumeration, ScanReason.None),
                Counts = new(10, 7 - outstanding, 1, 1, 1, outstanding),
            },
        });

        Assert.Equal(expected, fixture.ViewModel.ScanStatus);
    }

    [Fact]
    public void ScanCountsSayHowManyFilesWereCheckedAndListOnlyNonZeroProblems()
    {
        var fixture = new ExactShellFixture();
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = fixture.Workflow.Current.Scan with
            {
                Status = ScanStatus.Running,
                Counts = new(1284, 809, 2, 1, 0, 472),
            },
        });

        Assert.Equal("812 of 1,284 files checked · 2 unsupported · 1 failed", fixture.ViewModel.ScanCountsText);
    }

    [Fact]
    public void CanceledFilesAreNotCountedAsChecked()
    {
        var fixture = new ExactShellFixture();
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = fixture.Workflow.Current.Scan with
            {
                Status = ScanStatus.Incomplete,
                Counts = new(1284, 1270, 2, 0, 12, 0),
            },
        });

        Assert.Equal("1,272 of 1,284 files checked · 2 unsupported · 12 canceled", fixture.ViewModel.ScanCountsText);
    }

    [Theory]
    [InlineData(NearDuplicateMode.Conservative, ScanStageState.Pending, "Scanning: comparing look-alikes")]
    [InlineData(NearDuplicateMode.Conservative, ScanStageState.Succeeded, "Scanning: finishing up")]
    [InlineData(NearDuplicateMode.Disabled, ScanStageState.Pending, "Scanning: finishing up")]
    public void TheLookAlikePassHasItsOwnStage(NearDuplicateMode near, ScanStageState visualFeatures, string expected)
    {
        var fixture = new ExactShellFixture();
        var pin = new ScanPin(Guid.Parse("86000000-0000-0000-0000-000000000001"), 1, 1, 1);
        var key = new PhysicalFileKey(1, new(0, 1));
        var row = new ScanFileOccurrence(new(pin, 1, 1, 1), @"C:\Photos\one.jpg", @"C:\Photos\one.jpg",
            key, new(ScanStageState.Succeeded, ScanReason.None),
            new(ScanStageState.Succeeded, ScanReason.None), new(key, 10, new string('A', 64)), new(10, 1, true))
        {
            VisualFeatures = new(visualFeatures, ScanReason.None),
            NearDuplicate = new(visualFeatures, ScanReason.None),
        };
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Rules = new(1, 1, new(ExactDuplicateMode.Enabled, near)),
            Scan = new ScanSnapshot(pin, ScanStatus.Running, new(ScanStageState.Succeeded, ScanReason.None),
                [row], [], new(1, 1, 0, 0, 0, 0)),
        });

        Assert.Equal(expected, fixture.ViewModel.ScanStatus);
    }

    [Fact]
    public void IndexingPhotosSaysWhatItDoesAndThatCancelKeepsTheResults()
    {
        var fixture = new ExactShellFixture();
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = fixture.Workflow.Current.Scan with
            {
                Status = ScanStatus.Running,
                Enumeration = new(ScanStageState.Succeeded, ScanReason.None),
                Semantic = new(SemanticState.Indexing, 10000, 1300, 1234, new string('a', 64), ""),
            },
        });

        Assert.Equal("Scanning: indexing photos", fixture.ViewModel.ScanStatus);
        Assert.Equal("1,234 of 10,000 photos indexed. Cancel skips the rest; identical copies and look-alikes already found are kept.",
            fixture.ViewModel.SemanticProgressText);
        Assert.True(fixture.ViewModel.HasSemanticProgress);
        Assert.DoesNotContain("vector", fixture.ViewModel.SemanticProgressText, StringComparison.OrdinalIgnoreCase);

        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = fixture.Workflow.Current.Scan with
            {
                Status = ScanStatus.Complete,
                Semantic = new(SemanticState.Ready, 10000, 10000, 9990, new string('a', 64), ""),
            },
        });

        Assert.False(fixture.ViewModel.HasSemanticProgress);
    }

    // E2-S4: a photo counts as reused only when none of its results had to be computed again.
    [Theory]
    [InlineData(ScanStatus.Complete, 1200L, 1200L, 1200L, 34L, "Reused earlier results for 1,200 of 1,234 photos.")]
    [InlineData(ScanStatus.Incomplete, 1200L, 1200L, 1200L, 34L, "Reused earlier results for 1,200 of 1,234 photos.")]
    [InlineData(ScanStatus.Complete, 1200L, 1200L, 1100L, 134L, "Reused earlier results for 1,100 of 1,234 photos.")]
    [InlineData(ScanStatus.Complete, 1200L, 1150L, -1L, 0L, "Reused earlier results for 1,150 of 1,234 photos.")]
    [InlineData(ScanStatus.Complete, 1200L, 1200L, 0L, 1234L, "")]
    [InlineData(ScanStatus.Complete, 0L, 0L, 0L, 1234L, "")]
    [InlineData(ScanStatus.Running, 1200L, 1200L, 1200L, 34L, "")]
    public void TheFinishedScanSaysHowManyResultsWereReused(
        ScanStatus status, long near, long sharpness, long vectors, long vectorMisses, string expected)
    {
        const long Photos = 1234;
        var fixture = new ExactShellFixture();
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = fixture.Workflow.Current.Scan with
            {
                Status = status,
                // -1 vectors: the index did not run, so only the look-alike features count.
                Reuse = new(new(near, Photos - near, 0), new(sharpness, Photos - sharpness, 0),
                    vectors < 0 ? SignatureReuseCounts.None : new(vectors, vectorMisses, 0)),
            },
        });

        Assert.Equal(expected, fixture.ViewModel.SignatureReuseText);
        Assert.Equal(expected.Length != 0, fixture.ViewModel.HasSignatureReuse);
        Assert.DoesNotContain("vector", fixture.ViewModel.SignatureReuseText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OneReusedPhotoIsSingularAndNoReuseSaysNothing()
    {
        var fixture = new ExactShellFixture();
        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = fixture.Workflow.Current.Scan with
            {
                Status = ScanStatus.Complete,
                Reuse = new(new(1, 0, 0), new(1, 0, 0), SignatureReuseCounts.None),
            },
        });
        Assert.Equal("Reused earlier results for 1 of 1 photo.", fixture.ViewModel.SignatureReuseText);

        fixture.Workflow.Publish(fixture.Workflow.Current with
        {
            Scan = fixture.Workflow.Current.Scan with { Reuse = SignatureReuse.None },
        });
        Assert.False(fixture.ViewModel.HasSignatureReuse);
    }
}
