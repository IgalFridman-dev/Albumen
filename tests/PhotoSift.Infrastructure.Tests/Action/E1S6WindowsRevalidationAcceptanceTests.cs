using System.Collections.Immutable;
using System.Security.Cryptography;
using PhotoSift.Application.Action;
using PhotoSift.Domain.Action;
using PhotoSift.Infrastructure.Windows.Action;
using PhotoSift.Infrastructure.Windows.Scan;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.ActionExecution;

public sealed class E1S6WindowsRevalidationAcceptanceTests
{
    [Fact]
    public async Task RestartedMixedResultsRevalidateReadOnlyWithoutInventingRecycleSuccessOrBytes()
    {
        using var fixture = new ActionFileFixture();
        var present = fixture.Create("restarted-present.jpg", "reviewed-content");
        var missing = fixture.Create("restarted-missing.jpg", "other-content");
        var presentRequest = Request(present);
        var missingRequest = Request(missing);
        Assert.Equal(0, fixture.Delete(missing));
        var probe = new WindowsActionRevalidationProbe();

        var confirmed = await probe.ProbeAsync(presentRequest, TestContext.Current.CancellationToken);
        var unresolved = await probe.ProbeAsync(missingRequest, TestContext.Current.CancellationToken);

        Assert.Equal(ActionRevalidationKind.ConfirmedPresent, confirmed.Kind);
        Assert.Equal(ActionRevalidationReason.OriginalFileConfirmed, confirmed.Reason);
        Assert.Equal(ActionRevalidationKind.StillUnknown, unresolved.Kind);
        Assert.Equal(ActionRevalidationReason.OriginalPathMissing, unresolved.Reason);
        Assert.True(File.Exists(present));
        var summary = ActionRecordPolicy.Derive(ImmutableArray.Create(
            new ActionRecordEvidence(presentRequest.ReviewedKey, 4096, ActionRecordEvidenceState.ConfirmedPresent),
            new ActionRecordEvidence(missingRequest.ReviewedKey, 4096, ActionRecordEvidenceState.Unknown)));
        Assert.Equal(ActionRecordCertainty.HasUnknown, summary.Certainty);
        Assert.Equal(ActionRecordOutcomeMix.NoneMoved, summary.OutcomeMix);
        Assert.Equal(0, summary.RecoveredBytes);
    }

    private static ActionRevalidationRequest Request(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var facts = new WindowsFileNative().Read(stream.SafeFileHandle, path);
        return new(Guid.NewGuid(), path, facts.PhysicalFileKey!.Value, stream.Length,
            Convert.ToHexString(SHA256.HashData(stream)));
    }
}
