using PhotoSift.Application.Scan;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Action;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.ActionExecution;

[Trait("Category", "GoldenVm")]
public sealed class WindowsActionLeaseTests
{
    [Theory]
    [InlineData("sync", ActionSafetyReason.Synchronized)]
    [InlineData("placeholder", ActionSafetyReason.Placeholder)]
    [InlineData("unknown", ActionSafetyReason.UnknownSafety)]
    [InlineData("remote", ActionSafetyReason.UnsafeLocation)]
    [InlineData("unknownVolume", ActionSafetyReason.UnknownSafety)]
    [InlineData("access", ActionSafetyReason.AccessDenied)]
    [InlineData("reparse", ActionSafetyReason.UnsafeLocation)]
    public async Task UnsafeNativeFactsFailBeforePermission(string change, ActionSafetyReason reason)
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var envelope = fixture.Envelope(selected, fixture.Create("witness.jpg"));
        var source = new ActionObservedFacts((path, facts) => path != selected ? facts : change switch
        {
            "remote" => facts with { VolumeKind = ScopeVolumeKind.Remote },
            "unknownVolume" => facts with { VolumeKind = ScopeVolumeKind.Unknown },
            "access" => throw new ScanReadException(new(ScanStageState.FailedRetryable, ScanReason.AccessDenied), "Fixture access denial"),
            _ => WindowsActionRaceTests.Alter(facts, change),
        });
        var failure = await Assert.ThrowsAsync<WindowsActionLeaseException>(() => WindowsActionLease.OpenAsync(envelope, CancellationToken.None, source));
        Assert.Equal(reason, failure.Reason);
        Assert.True(File.Exists(selected));
        fixture.AssertWritable(selected);
    }

    [Theory]
    [InlineData(ScopeVolumeKind.Fixed)]
    [InlineData(ScopeVolumeKind.Removable)]
    public async Task SupportedLocalVolumeAllowsProtectedProof(ScopeVolumeKind kind)
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var source = new ActionObservedFacts((_, facts) => facts with { VolumeKind = kind });
        using var lease = await WindowsActionLease.OpenAsync(fixture.Envelope(selected, fixture.Create("witness.jpg")), CancellationToken.None, source);
        Assert.Equal(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
    }

    [Fact]
    public async Task ExternalLinkCountUsesExactKnownPathsAndIsNotAnAssumedRecycleDecrement()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var external = fixture.Link("external.jpg", selected);
        var envelope = fixture.Envelope(selected);
        Assert.Equal(ActionSafetyDecisionKind.Allowed, envelope.Decision.Kind);
        using var lease = await WindowsActionLease.OpenAsync(envelope, CancellationToken.None);
        Assert.Equal(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
        Assert.Equal(0, fixture.Delete(selected));
        Assert.True(File.Exists(external));
    }

    [Fact]
    public async Task NoSurvivorAndNoExactOverrideFailsClosed()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var failure = await Assert.ThrowsAsync<WindowsActionLeaseException>(() => WindowsActionLease.OpenAsync(fixture.Envelope(selected), CancellationToken.None));
        Assert.Equal(ActionSafetyReason.NoCurrentSurvivor, failure.Reason);
        Assert.True(File.Exists(selected));
    }

    [Fact]
    public async Task ExactOverrideCanAuthorizeFinalOccurrenceWithoutInventingWitness()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var envelope = fixture.Envelope(selected);
        var input = envelope.Input with { OverrideEvidenceExact = true, OverrideApprovedOccurrenceIds = [1] };
        envelope = envelope with { Input = input, Decision = ActionSafetyPolicy.Evaluate(input) };
        using var lease = await WindowsActionLease.OpenAsync(envelope, CancellationToken.None);
        Assert.Equal(ActionSafetyReason.None, lease.ValidateAndReleaseForPreDelete());
    }

    [Fact]
    public async Task CanceledPreparationReleasesResourcesWithoutPermission()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var envelope = fixture.Envelope(selected, fixture.Create("witness.jpg"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = await Assert.ThrowsAsync<WindowsActionLeaseException>(() => WindowsActionLease.OpenAsync(envelope, cancellation.Token));
        Assert.Equal(ActionSafetyReason.Interrupted, failure.Reason);
        fixture.AssertWritable(selected);
    }

    [Fact]
    public async Task PreparationRehashRejectsSelectedWithUnchangedApprovedMetadata()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var witness = fixture.Create("witness.jpg");
        var envelope = fixture.Envelope(selected, witness);
        var approved = envelope.Observations[0];
        fixture.WritePreservingApprovedTimestamp(selected, "changed", approved);
        fixture.AssertOnlyDigestChanged(selected, approved);
        var failure = await Assert.ThrowsAsync<WindowsActionLeaseException>(async () =>
        {
            using var unexpected = await WindowsActionLease.OpenAsync(envelope, CancellationToken.None);
        });
        Assert.Equal(ActionSafetyReason.Changed, failure.Reason);
        Assert.True(File.Exists(selected));
        Assert.True(File.Exists(witness));
        fixture.AssertOnlyDigestChanged(selected, approved);
        fixture.AssertWritable(selected);
        fixture.AssertWritable(witness);
    }

    [Fact]
    public async Task EscapedApprovedScopeFailsClosed()
    {
        using var fixture = new ActionFileFixture();
        var selected = fixture.Create("selected.jpg");
        var envelope = fixture.Envelope(selected, fixture.Create("witness.jpg"));
        var request = envelope.Claim.Request with { Scope = envelope.Claim.Request.Scope with { Selection = new ScopeSelection(fixture.PathFor("other"), ScopeRecursion.IncludeSubfolders) } };
        envelope = envelope with { Claim = envelope.Claim with { Request = request } };
        var failure = await Assert.ThrowsAsync<WindowsActionLeaseException>(() => WindowsActionLease.OpenAsync(envelope, CancellationToken.None));
        Assert.Equal(ActionSafetyReason.UnsafeLocation, failure.Reason);
        fixture.AssertWritable(selected);
    }
}
