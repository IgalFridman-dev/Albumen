using System.Reflection;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Architecture;

public sealed class PermanentDeleteFallbackInstanceTests
{
    [Theory]
    [InlineData(nameof(FileDeleteSentinel))]
    [InlineData(nameof(DirectoryDeleteSentinel))]
    [InlineData(nameof(FileMoveSentinel))]
    [InlineData(nameof(DirectoryMoveSentinel))]
    [InlineData(nameof(FileReplaceSentinel))]
    public void GuardDetectsInstanceMutationFromIlWithoutExecutingSentinel(string methodName)
    {
        var sentinel = typeof(PermanentDeleteFallbackInstanceTests).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Contains(PermanentDeleteFallbackTests.CalledMethods(sentinel), PermanentDeleteFallbackTests.IsForbiddenMutation);
    }

    // These methods are IL-only adversarial fixtures. They are never invoked and
    // no real path is supplied to them; no filesystem mutation is performed.
    private static void FileDeleteSentinel(string path) => new FileInfo(path).Delete();
    private static void DirectoryDeleteSentinel(string path) => new DirectoryInfo(path).Delete();
    private static void FileMoveSentinel(string path, string destination) => new FileInfo(path).MoveTo(destination);
    private static void DirectoryMoveSentinel(string path, string destination) => new DirectoryInfo(path).MoveTo(destination);
    private static void FileReplaceSentinel(string path, string destination) => new FileInfo(path).Replace(destination, null);
}
