using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using PhotoSift.Application.Review;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Sqlite;

public sealed class E1S4SqliteAcceptanceTests
{
    [Fact]
    public async Task ReopenPreservesTheOriginalRowAfterExactRetryConflictAndFailedInsert()
    {
        await using var fixture = new SqliteOverrideFixture();
        var original = OverrideEvidenceData.Create() with { SelectedOccurrenceIds = [3, 1, 2] };
        var exactRetry = original with
        {
            SelectedOccurrenceIds = [2, 3, 1],
            AffectedPhysicalRevisions = original.AffectedPhysicalRevisions.Reverse().ToImmutableArray(),
        };
        var conflict = original with { SelectedOccurrenceIds = [99] };
        var rejected = OverrideEvidenceData.Create(
            Guid.Parse("46e1c5ee-238b-471f-933f-eb401e88425b"));

        await using (var store = fixture.CreateStore(SqliteFailureInjection.FailSecondInsert()))
        {
            Assert.Equal(OverridePersistOutcome.Persisted,
                (await store.PersistAsync(original, TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(OverridePersistOutcome.ExactRetry,
                (await store.PersistAsync(exactRetry, TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(OverridePersistOutcome.Conflict,
                (await store.PersistAsync(conflict, TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(OverridePersistOutcome.Unavailable,
                (await store.PersistAsync(rejected, TestContext.Current.CancellationToken)).Outcome);
        }
        SqliteConnection.ClearAllPools();

        await using (var reopened = fixture.CreateStore())
        {
            var retained = await reopened.ReadAsync(original.EvidenceId, TestContext.Current.CancellationToken);
            var missing = await reopened.ReadAsync(rejected.EvidenceId, TestContext.Current.CancellationToken);
            Assert.Equal(OverrideReadOutcome.Found, retained.Outcome);
            OverrideEvidenceData.AssertEquivalent(original, retained.Evidence);
            Assert.Equal(OverrideReadOutcome.Missing, missing.Outcome);
            Assert.Equal(OverridePersistOutcome.ExactRetry,
                (await reopened.PersistAsync(exactRetry, TestContext.Current.CancellationToken)).Outcome);
        }
        SqliteConnection.ClearAllPools();

        await using var verify = await fixture.OpenRawAsync();
        await using var version = verify.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(2L, Convert.ToInt64(
            await version.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture));
        await using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM override_evidence;";
        Assert.Equal(1L, Convert.ToInt64(
            await count.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture));
        await using var columns = verify.CreateCommand();
        columns.CommandText = "SELECT name FROM pragma_table_info('override_evidence') ORDER BY cid;";
        await using var reader = await columns.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var actualColumns = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            actualColumns.Add(reader.GetString(0));
        }
        Assert.Equal(OverrideEvidenceData.ExpectedColumns, actualColumns);
    }
}
