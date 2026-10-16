using PhotoSift.Domain.Scope;
using Xunit;

namespace PhotoSift.Domain.Tests.Scope;

public sealed class ScopePathContainmentTests
{
    [Theory]
    [InlineData(@"C:\Photos", @"C:\Photos", true)]
    [InlineData(@"C:\Photos", @"c:\photos\Trips\a.jpg", true)]
    [InlineData(@"C:\Photos\", @"\\?\C:\Photos\Trips", true)]
    [InlineData(@"C:\Photos", @"C:\Photos2\a.jpg", false)]
    [InlineData(@"C:\Photos", @"D:\Photos\a.jpg", false)]
    public void ContainsUsesWindowsSegmentBoundaries(
        string root,
        string candidate,
        bool expected) =>
        Assert.Equal(expected, ScopePathContainment.Contains(root, candidate));

    [Theory]
    [InlineData(@"C:/Photos", @"C:\Photos\Trips", true)]
    [InlineData(@"\\?\UNC\server\share\Photos", @"\\server\share\Photos\Trips", true)]
    [InlineData(@"C:\Café", @"C:\Café\a.jpg", false)]
    [InlineData(@"C:\Å", @"C:\Å\a.jpg", false)]
    public void ContainsCanonicalizesWindowsSyntaxWithoutNormalizingUnicode(
        string root,
        string candidate,
        bool expected) =>
        Assert.Equal(expected, ScopePathContainment.Contains(root, candidate));

    [Fact]
    public void ContainsComparesPathsLongerThanTheLegacyWindowsLimit()
    {
        var longDirectoryName = new string('a', 261);

        Assert.True(ScopePathContainment.Contains(
            @"C:\Photos",
            $@"C:\Photos\{longDirectoryName}\a.jpg"));
    }
}
