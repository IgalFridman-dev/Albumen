namespace PhotoSift.Scenarios.Tests.Shell;

/// <summary>A database path in a fresh temp folder, removed on dispose.</summary>
internal sealed class TemporaryDatabase : IDisposable
{
    private readonly string folder = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "PhotoSift.Scenarios.Tests", Guid.NewGuid().ToString("N"));

    public TemporaryDatabase() => Directory.CreateDirectory(folder);

    public string Path => System.IO.Path.Combine(folder, "photosift.db");

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
