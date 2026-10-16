using PhotoSift.Domain.Scope;
using PhotoSift.Infrastructure.Windows.Scope;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Scope;

public sealed class WicCodecCapabilityTests
{
    [Theory]
    [InlineData(SupportedImageFormat.Jpeg, "9456a480-e88b-43ea-9e73-0b2d9b71b1ca")]
    [InlineData(SupportedImageFormat.Png, "389ea17b-5078-4cde-b6ef-25c15175c751")]
    [InlineData(SupportedImageFormat.Heic, "e9a4a80a-44fe-4de4-8971-7150b10a5199")]
    public void UsesDocumentedWicDecoderClass(
        SupportedImageFormat format,
        string expectedClassId)
    {
        var availability = new RecordingComClassAvailability(available: true);
        var probe = new WicCodecCapability(availability);

        Assert.True(probe.IsAvailable(format));
        Assert.Equal(Guid.Parse(expectedClassId), availability.RequestedClassId);
    }

    [Fact]
    public void MissingHeifRegistrationIsUnavailable()
    {
        var availability = new RecordingComClassAvailability(available: false);
        var probe = new WicCodecCapability(availability);

        Assert.False(probe.IsAvailable(SupportedImageFormat.Heic));
    }

    private sealed class RecordingComClassAvailability(bool available) : IComClassAvailability
    {
        public Guid RequestedClassId { get; private set; }

        public bool IsAvailable(Guid classId)
        {
            RequestedClassId = classId;
            return available;
        }
    }
}
