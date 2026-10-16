using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using Xunit;

namespace PhotoSift.Application.Tests.Review;

public sealed class ApprovedActionRequestTests
{
    [Fact]
    public async Task CompleteApprovalCarriesTheCanonicalSelectedItemsAndReviewedFacts()
    {
        await using var fixture = new ApprovalFixture(new ApprovalEvidenceStore());
        await fixture.OpenSelectedAsync();
        var before = fixture.Owner.Current;
        var command = fixture.ApprovalCommand();

        var result = await fixture.ApproveAsync(command);

        var request = Assert.IsType<ApprovedActionRequest>(result.ApprovedRequest);
        Assert.Equal(command.IdempotencyKey, request.RequestId);
        Assert.Equal(command.ApprovedUtc.ToUniversalTime(), request.ApprovedUtc);
        Assert.Equal(command.Session.ReviewSessionId, request.ReviewSessionId);
        Assert.Equal(before.SelectionRevision + 1, request.SelectionRevision);
        Assert.Equal(command.Session.Scan, request.Scan);
        Assert.Equal(fixture.Request.Scope.Preflight!.Scope, request.Scope);
        Assert.Equal([1L, 2L], request.SelectedOccurrenceIds);
        Assert.Equal(request.SelectedOccurrenceIds,
            request.Items.Select(item => item.Occurrence.OccurrenceId));
        Assert.All(request.Items, item =>
        {
            var sourceGroup = fixture.Request.Scan.Groups.Single(group => group.GroupId == item.GroupId);
            var source = sourceGroup.Members.Single(member => member.Token.OccurrenceId == item.Occurrence.OccurrenceId);
            Assert.Equal(sourceGroup.GroupRevision, item.GroupRevision);
            Assert.Equal(source.Token, item.Occurrence);
            Assert.Equal(source.DiscoveredPath, item.DiscoveredPath);
            Assert.Equal(source.ResolvedPath, item.ResolvedPath);
            Assert.Equal(source.PhysicalFileKey, item.PhysicalFileKey);
            Assert.Equal(source.Content, item.Content);
            Assert.Equal(source.Allocation, item.Allocation);
            Assert.Null(item.OverrideEvidenceId);
        });
        Assert.Equal(2, request.Disclosure.SelectedOccurrenceCount);
        Assert.Equal(1, request.Disclosure.DistinctPhysicalKeyCount);
        Assert.Equal(ScanStatus.Complete, request.Disclosure.Coverage);
        Assert.Equal(new SelectionRecovery(4096, SelectionRecoveryUnavailableReason.None),
            request.Disclosure.Recovery);
        Assert.Equal("Windows Recycle Bin", request.Disclosure.FutureDestination);
        Assert.Contains("revalidate", request.Disclosure.RevalidationNotice,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new ReviewUnresolvedSummary(0, 0, 0, 0, 0, 0, 3),
            request.Disclosure.Unresolved);
        Assert.Equal(0, request.Disclosure.Unresolved.Total);
        Assert.Equal(3, request.Disclosure.Unresolved.UnselectedSurvivorMembers);
    }

    [Fact]
    public async Task IncompleteApprovalKeepsEveryUnresolvedSubtotalAndOrdinarySurvivorsSeparate()
    {
        await using var fixture = new ApprovalFixture(
            new ApprovalEvidenceStore(), ScanStatus.Incomplete);
        await fixture.OpenSelectedAsync(leaveSecondGroupUnresolved: true);

        var result = await fixture.ApproveAsync(fixture.ApprovalCommand());

        var disclosure = Assert.IsType<ApprovedActionRequest>(result.ApprovedRequest).Disclosure;
        Assert.Equal(ScanStatus.Incomplete, disclosure.Coverage);
        Assert.Equal(1, disclosure.Unresolved.IncompleteEnumeration);
        Assert.Equal(2, disclosure.Unresolved.FailedScanRows);
        Assert.Equal(3, disclosure.Unresolved.CanceledScanRows);
        Assert.Equal(4, disclosure.Unresolved.UnprocessedScanRows);
        Assert.Equal(1, disclosure.Unresolved.PendingReviewMembers);
        Assert.Equal(1, disclosure.Unresolved.UnavailableReviewMembers);
        Assert.Equal(1, disclosure.Unresolved.UnselectedSurvivorMembers);
        Assert.Equal(12, disclosure.Unresolved.Total);
        Assert.Equal(13, checked(disclosure.Unresolved.Total
            + disclosure.Unresolved.UnselectedSurvivorMembers));
    }

    [Fact]
    public async Task UnavailableRecoveryReasonIsCarriedWithoutClaimingRecoveredBytes()
    {
        await using var fixture = new ApprovalFixture(new ApprovalEvidenceStore());
        var group = fixture.Request.Scan.Groups[0];
        var unknown = ReviewFixture.Copy(group.Members[0], allocation: new(null, 2, true));
        var alias = ReviewFixture.Copy(group.Members[1], key: unknown.PhysicalFileKey,
            content: unknown.Content, allocation: unknown.Allocation);
        fixture.ReplaceRequest(fixture.Request with
        {
            Scan = fixture.Request.Scan with
            {
                Occurrences = fixture.Request.Scan.Occurrences
                    .Replace(group.Members[0], unknown)
                    .Replace(group.Members[1], alias),
                Groups = fixture.Request.Scan.Groups.SetItem(0,
                    group with { Members = group.Members.SetItem(0, unknown).SetItem(1, alias) }),
            },
        });
        await fixture.OpenSelectedAsync();

        var result = await fixture.ApproveAsync(fixture.ApprovalCommand());

        var recovery = Assert.IsType<ApprovedActionRequest>(result.ApprovedRequest).Disclosure.Recovery;
        Assert.Null(recovery.Bytes);
        Assert.Equal(SelectionRecoveryUnavailableReason.UnknownAllocation, recovery.Reason);
    }

    [Fact]
    public async Task OverrideApprovalCarriesTheApplicableDurableEvidenceIdOnEveryItem()
    {
        var store = new ApprovalEvidenceStore();
        await using var fixture = new ApprovalFixture(store);
        await fixture.OpenOverrideSelectedAsync();

        var result = await fixture.ApproveAsync(fixture.ApprovalCommand());

        var request = Assert.IsType<ApprovedActionRequest>(result.ApprovedRequest);
        Assert.Equal([1L, 2L, 3L], request.SelectedOccurrenceIds);
        Assert.All(request.Items, item =>
            Assert.Equal(fixture.OverrideEvidence!.EvidenceId, item.OverrideEvidenceId));
        Assert.Equal([fixture.OverrideEvidence!.EvidenceId], store.ReadEvidenceIds);
    }
}
