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
    public void PreparingPhotosForSearchSaysWhatItDoesAndThatCancelKeepsTheResults()
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

        Assert.Equal("Scanning: getting photos ready for search and look-alikes", fixture.ViewModel.ScanStatus);
        Assert.Equal("1,234 of 10,000 photos ready. Cancel skips the rest; identical copies and look-alikes found so far are kept.",
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
}
