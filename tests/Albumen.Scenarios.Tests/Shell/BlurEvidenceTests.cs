using System.Collections.Immutable;
using Albumen.App.Shell;
using Albumen.Application.Review;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Xunit;

namespace Albumen.Scenarios.Tests.Shell;

public sealed class BlurEvidenceTests
{
    [Fact]
    public void OnlyPhotosFarBlurrierThanTheSharpestInTheSetAreFlagged()
    {
        var group = OpenSet((1, 900), (2, 150), (3, 600), (4, null));

        Assert.Equal([false, true, false, false], Members(group).Select(member => member.LooksBlurrier));
        Assert.EndsWith($"; {ScanOccurrenceViewModel.BlurrierText}", Members(group)[1].ItemStatus);
        Assert.DoesNotContain(ScanOccurrenceViewModel.BlurrierText, Members(group)[0].ItemStatus);
    }

    [Fact]
    public void AFlatOrUnmeasuredSetMakesNoBlurClaim()
    {
        Assert.All(Members(OpenSet((1, 20), (2, 1))), member => Assert.False(member.LooksBlurrier));
        Assert.All(Members(OpenSet((1, null), (2, null))), member => Assert.False(member.LooksBlurrier));
    }

    [Fact]
    public void BlurEvidenceNeverMarksAPhoto()
    {
        var group = OpenSet((1, 900), (2, 10));

        Assert.All(Members(group), member => Assert.False(member.IsSelected));
        Assert.Equal("Kept", Members(group)[1].EligibilityText);
    }

    private static ScanOccurrenceViewModel[] Members(ReviewGroupViewModel group) =>
        Enumerable.Range(0, group.MemberCount).Select(index => group.Members[index]).ToArray();

    private static ReviewGroupViewModel OpenSet(params (long Id, double? Sharpness)[] photos)
    {
        var pin = new ScanPin(Guid.Parse("14000000-0000-0000-0000-000000000001"), 1, 3, 5);
        var members = photos.Select(photo => Analyzed(pin, photo.Id) with { Sharpness = photo.Sharpness })
            .ToImmutableArray();
        var candidate = new CandidateGroup(Guid.Parse("34000000-0000-0000-0000-000000000004"), 4, pin,
            CandidateCategory.NearDuplicate, members, [], false, null, new(1024, RecoveryUnavailableReason.None));
        var snapshot = new ReviewGroupSnapshot(candidate,
            members.Select(member => new ReviewMemberSnapshot(
                member, ReviewEligibilityState.Eligible, ReviewUnavailableReason.None, false)).ToImmutableArray(),
            [], new(0, SelectionRecoveryUnavailableReason.None), false, null);
        return new ReviewGroupViewModel(snapshot, ReviewCategoryKind.Near, 1, 1, true,
            (_, _) => Task.CompletedTask);
    }

    private static ScanFileOccurrence Analyzed(ScanPin pin, long id)
    {
        var key = new PhysicalFileKey(7, new(0, checked((ulong)id)));
        var path = $@"C:\Photos\{id}.jpg";
        return new(new(pin, id, 2, 3), path, path, key,
            new(ScanStageState.Succeeded, ScanReason.None),
            new(ScanStageState.Succeeded, ScanReason.None),
            new(key, 512, new string('A', 64)),
            new(512, 1, true));
    }
}
