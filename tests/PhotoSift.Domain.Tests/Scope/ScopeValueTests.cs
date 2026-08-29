using PhotoSift.Domain.Scope;
using Xunit;

namespace PhotoSift.Domain.Tests.Scope;

public sealed class ScopeValueTests
{
    [Fact]
    public void ScopeSelectionRetainsAnAbsoluteSelectedPathAndRecursionChoice()
    {
        var selection = new ScopeSelection(@"C:\Photos", ScopeRecursion.IncludeSubfolders);

        Assert.Equal(@"C:\Photos", selection.SelectedPath);
        Assert.Equal(ScopeRecursion.IncludeSubfolders, selection.Recursion);
    }

    [Fact]
    public void ScopeSelectionRejectsNullPaths()
    {
        Assert.Throws<ArgumentNullException>(
            () => new ScopeSelection(null!, ScopeRecursion.TopLevelOnly));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ScopeSelectionRejectsBlankPaths(string selectedPath)
    {
        Assert.Throws<ArgumentException>(
            () => new ScopeSelection(selectedPath!, ScopeRecursion.TopLevelOnly));
    }

    [Fact]
    public void ScopeSelectionRejectsRelativePaths()
    {
        Assert.Throws<ArgumentException>(
            () => new ScopeSelection(@"Photos\Trips", ScopeRecursion.TopLevelOnly));
    }

    [Fact]
    public void DirectoryIdentityRetainsTheNativeDirectoryIdentityValues()
    {
        var identity = new DirectoryIdentity(1, 2, 3);

        Assert.Equal((ulong)1, identity.VolumeSerialNumber);
        Assert.Equal((ulong)2, identity.FileIdHigh);
        Assert.Equal((ulong)3, identity.FileIdLow);
    }

    [Fact]
    public void ResolvedScopeKeepsTheSelectedPathSeparateFromTheResolvedPath()
    {
        var selection = new ScopeSelection(@"C:\Selected", ScopeRecursion.TopLevelOnly);
        var identity = new DirectoryIdentity(1, 2, 3);

        var scope = new ResolvedScope(selection, @"C:\Resolved", identity);

        Assert.Same(selection, scope.Selection);
        Assert.Equal(@"C:\Resolved", scope.ResolvedPath);
        Assert.Same(identity, scope.RootIdentity);
    }
}
