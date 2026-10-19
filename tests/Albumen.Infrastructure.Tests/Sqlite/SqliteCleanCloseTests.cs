using System.IO;
using Albumen.Application.Action;
using Albumen.Infrastructure.Sqlite;
using Xunit;

namespace Albumen.Infrastructure.Tests.Sqlite;

public sealed class SqliteCleanCloseTests
{
    [Fact]
    public async Task CleanDisposeLeavesNoSidecarsSoTheNextStartLoadsActionRecords()
    {
        await using var fixture = new SqliteOverrideFixture();
        await using (var first = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            Assert.Equal(ActionRecordLoadOutcome.Loaded,
                (await first.ActionRecords.LoadAsync(TestContext.Current.CancellationToken)).Outcome);
        }

        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            Assert.False(File.Exists(fixture.DatabasePath + suffix), $"Clean dispose retained {suffix}.");

        await using var second = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionRecordLoadOutcome.Loaded,
            (await second.ActionRecords.LoadAsync(TestContext.Current.CancellationToken)).Outcome);
    }
}
