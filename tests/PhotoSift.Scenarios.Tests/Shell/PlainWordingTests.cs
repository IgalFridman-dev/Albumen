using PhotoSift.App.Shell;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Scenarios.Tests.Shell;

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
    [InlineData(ScanStatus.Running, ScanStageState.Succeeded, 3L, "Scanning: comparing photos")]
    [InlineData(ScanStatus.Running, ScanStageState.Succeeded, 0L, "Scanning: finishing up")]
    [InlineData(ScanStatus.Complete, ScanStageState.Succeeded, 0L, "Finished: every file was checked")]
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
}
