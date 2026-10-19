using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Albumen.Application.Action;
using Albumen.Domain.Scan;
using Albumen.Infrastructure.Sqlite;
using Xunit;
using static Albumen.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace Albumen.Infrastructure.Tests.Sqlite;

public sealed class SqliteActionRecordStoreTests
{
    [Fact]
    public async Task LoadReturnsEveryRequestedRowBeforeProjectingItsStrictlyOrderedJournal()
    {
        await using var fixture = new SqliteOverrideFixture();
        var first = JournalData.Requested();
        var second = SecondRequested(first);
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionJournalWriteOutcome.Persisted,
            (await stores.ActionJournal.PersistRequestedAsync(first, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.Persisted,
            (await stores.ActionJournal.PersistRequestedAsync(second, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.Persisted,
            (await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(first), TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.Persisted,
            (await stores.ActionJournal.PersistTerminalAsync(JournalData.Terminal(first), TestContext.Current.CancellationToken)).Outcome);

        var loaded = await stores.ActionRecords.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ActionRecordLoadOutcome.Loaded, loaded.Outcome);
        Assert.Equal(2, loaded.Items.Length);
        var terminal = Assert.Single(loaded.Items, item => item.Requested.ActionItemId == first.ActionItemId);
        Assert.Equal(first.Safety.Claim.Request.RequestId, terminal.RequestId);
        Assert.Equal(JournalData.Executing(first), terminal.Executing);
        Assert.Equal(JournalData.Terminal(first), terminal.Terminal);
        Assert.Empty(terminal.Revalidations);
        var requestedOnly = Assert.Single(loaded.Items, item => item.Requested.ActionItemId == second.ActionItemId);
        Assert.Null(requestedOnly.Executing);
        Assert.Null(requestedOnly.Terminal);
    }

    [Fact]
    public async Task LoadRejectsTerminalWithoutExecutingAndDoesNotOmitTheRequestedRow()
    {
        await using var fixture = new SqliteOverrideFixture();
        var requested = JournalData.Requested();
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken);
            await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(requested), TestContext.Current.CancellationToken);
            await stores.ActionJournal.PersistTerminalAsync(JournalData.Terminal(requested), TestContext.Current.CancellationToken);
        }
        await using (var connection = await fixture.OpenRawAsync())
            await ExecuteAsync(connection, "DELETE FROM action_transitions WHERE sequence=2;");

        await using var reopened = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionRecordLoadOutcome.Unavailable,
            (await reopened.ActionRecords.LoadAsync(TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task RevalidationIdsAreIdempotentAndSequencesIncreasePerItem()
    {
        await using var fixture = new SqliteOverrideFixture();
        var requested = JournalData.Requested();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken);
        var first = Revalidation(requested.ActionItemId, Guid.Parse("31313131-3131-3131-3131-313131313131"));
        var second = Revalidation(requested.ActionItemId, Guid.Parse("32323232-3232-3232-3232-323232323232"));

        Assert.Equal(ActionRevalidationWriteOutcome.Persisted,
            (await stores.ActionRecords.AppendRevalidationAsync(first, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionRevalidationWriteOutcome.ExactRetry,
            (await stores.ActionRecords.AppendRevalidationAsync(first, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionRevalidationWriteOutcome.Conflict,
            (await stores.ActionRecords.AppendRevalidationAsync(first with { Reason = ActionRevalidationReason.Failed }, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionRevalidationWriteOutcome.Persisted,
            (await stores.ActionRecords.AppendRevalidationAsync(second, TestContext.Current.CancellationToken)).Outcome);

        await using var connection = await fixture.OpenRawAsync();
        Assert.Equal("1,2", await ScalarAsync(connection,
            "SELECT group_concat(sequence, ',') FROM action_revalidations ORDER BY sequence;"));
    }

    [Theory]
    [InlineData(ActionTerminalKind.Recycled)]
    [InlineData(ActionTerminalKind.NotRemoved)]
    public async Task TerminalRowsRejectRevalidationAsResolved(ActionTerminalKind terminalKind)
    {
        await using var fixture = new SqliteOverrideFixture();
        var requested = JournalData.Requested();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken);
        await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(requested), TestContext.Current.CancellationToken);
        await stores.ActionJournal.PersistTerminalAsync(JournalData.Terminal(requested) with { Kind = terminalKind }, TestContext.Current.CancellationToken);

        Assert.Equal(ActionRevalidationWriteOutcome.Resolved,
            (await stores.ActionRecords.AppendRevalidationAsync(Revalidation(requested.ActionItemId, Guid.NewGuid()), TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task ConfirmedPresentResolvesTheRowAndRejectsLaterObservations()
    {
        await using var fixture = new SqliteOverrideFixture();
        var requested = JournalData.Requested();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken);
        var key = new PhysicalFileKey(19, new(23, 29));
        var confirmed = new ActionRevalidationFact(Guid.Parse("41414141-4141-4141-4141-414141414141"), requested.ActionItemId,
            requested.RequestedUtc.AddMinutes(1), ActionRevalidationKind.ConfirmedPresent,
            ActionRevalidationReason.OriginalFileConfirmed, new(true, key, 101, new string('a', 64)));

        Assert.Equal(ActionRevalidationWriteOutcome.Persisted,
            (await stores.ActionRecords.AppendRevalidationAsync(confirmed, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionRevalidationWriteOutcome.Resolved,
            (await stores.ActionRecords.AppendRevalidationAsync(Revalidation(requested.ActionItemId, Guid.NewGuid()), TestContext.Current.CancellationToken)).Outcome);
        var loaded = await stores.ActionRecords.LoadAsync(TestContext.Current.CancellationToken);
        var stored = Assert.Single(Assert.Single(loaded.Items).Revalidations);
        Assert.Equal(new string('A', 64), stored.Observation.ObservedSha256);
        Assert.Equal(TimeSpan.Zero, stored.ObservedUtc.Offset);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("length")]
    [InlineData("digest")]
    public async Task ConfirmedPresentMustMatchTheDurableReviewedIdentityOnAppend(string mismatch)
    {
        await using var fixture = new SqliteOverrideFixture();
        var requested = JournalData.Requested();
        var reviewed = requested.Safety.Item;
        var observation = new ActionRevalidationObservation(true, reviewed.PhysicalFileKey,
            reviewed.Content.ByteLength, reviewed.Content.Sha256);
        observation = mismatch switch
        {
            "key" => observation with { ObservedKey = new PhysicalFileKey(20, new(23, 29)) },
            "length" => observation with { ObservedLength = observation.ObservedLength + 1 },
            _ => observation with { ObservedSha256 = new string('B', 64) },
        };
        var fact = new ActionRevalidationFact(Guid.NewGuid(), requested.ActionItemId,
            requested.RequestedUtc.AddMinutes(1), ActionRevalidationKind.ConfirmedPresent,
            ActionRevalidationReason.OriginalFileConfirmed, observation);
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken);

        Assert.Equal(ActionRevalidationWriteOutcome.Conflict,
            (await stores.ActionRecords.AppendRevalidationAsync(fact, TestContext.Current.CancellationToken)).Outcome);
    }

    [Theory]
    [InlineData("\"volumeSerial\":19", "\"volumeSerial\":20")]
    [InlineData("\"observedLength\":101", "\"observedLength\":102")]
    [InlineData(
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB")]
    public async Task LoadRejectsConfirmedPresentThatDoesNotMatchDurableReviewedIdentity(
        string original, string replacement)
    {
        await using var fixture = new SqliteOverrideFixture();
        var requested = JournalData.Requested();
        var reviewed = requested.Safety.Item;
        var fact = new ActionRevalidationFact(Guid.NewGuid(), requested.ActionItemId,
            requested.RequestedUtc.AddMinutes(1), ActionRevalidationKind.ConfirmedPresent,
            ActionRevalidationReason.OriginalFileConfirmed,
            new(true, reviewed.PhysicalFileKey, reviewed.Content.ByteLength, reviewed.Content.Sha256));
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken);
            Assert.Equal(ActionRevalidationWriteOutcome.Persisted,
                (await stores.ActionRecords.AppendRevalidationAsync(fact, TestContext.Current.CancellationToken)).Outcome);
        }
        await using (var connection = await fixture.OpenRawAsync())
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT observation_payload FROM action_revalidations;";
            var payload = Assert.IsType<string>(await read.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.Contains(original, payload, StringComparison.Ordinal);
            payload = payload.Replace(original, replacement, StringComparison.Ordinal);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE action_revalidations SET observation_payload=$payload, observation_payload_sha256=$hash;";
            update.Parameters.AddWithValue("$payload", payload);
            update.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));
            Assert.Equal(1, await update.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        await using var reopened = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionRecordLoadOutcome.Unavailable,
            (await reopened.ActionRecords.LoadAsync(TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task OrphanRevalidationCannotReceiveAnExactRetryReceipt()
    {
        await using var fixture = new SqliteOverrideFixture();
        var requested = JournalData.Requested();
        var fact = Revalidation(requested.ActionItemId, Guid.NewGuid());
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken);
            await stores.ActionRecords.AppendRevalidationAsync(fact, TestContext.Current.CancellationToken);
        }
        await using (var connection = await fixture.OpenRawAsync())
            await ExecuteAsync(connection, "PRAGMA foreign_keys=OFF; DELETE FROM action_items;");

        await using var reopened = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionRevalidationWriteOutcome.Unavailable,
            (await reopened.ActionRecords.AppendRevalidationAsync(fact, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task UnrelatedOrphanRevalidationPreventsAnotherItemMutation()
    {
        await using var fixture = new SqliteOverrideFixture();
        var first = JournalData.Requested();
        var second = SecondRequested(first);
        var orphan = Revalidation(first.ActionItemId, Guid.NewGuid());
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            await stores.ActionJournal.PersistRequestedAsync(first, TestContext.Current.CancellationToken);
            await stores.ActionJournal.PersistRequestedAsync(second, TestContext.Current.CancellationToken);
            await stores.ActionRecords.AppendRevalidationAsync(orphan, TestContext.Current.CancellationToken);
        }
        await using (var connection = await fixture.OpenRawAsync())
        {
            await ExecuteAsync(connection, "PRAGMA foreign_keys=OFF;");
            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM action_items WHERE action_item_id=$id;";
            delete.Parameters.AddWithValue("$id", first.ActionItemId.ToString("D"));
            Assert.Equal(1, await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }

        await using var reopened = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionRevalidationWriteOutcome.Unavailable,
            (await reopened.ActionRecords.AppendRevalidationAsync(
                Revalidation(second.ActionItemId, Guid.NewGuid()), TestContext.Current.CancellationToken)).Outcome);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_revalidations;"));
    }

    [Fact]
    public async Task LoadRejectsChangedRevalidationPayloadOrHashWithoutRepair()
    {
        await using var fixture = new SqliteOverrideFixture();
        var requested = JournalData.Requested();
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken);
            await stores.ActionRecords.AppendRevalidationAsync(Revalidation(requested.ActionItemId,
                Guid.Parse("51515151-5151-5151-5151-515151515151")), TestContext.Current.CancellationToken);
        }
        await using (var connection = await fixture.OpenRawAsync())
            await ExecuteAsync(connection, "UPDATE action_revalidations SET observation_payload_sha256='" + new string('0', 64) + "';");

        await using var reopened = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionRecordLoadOutcome.Unavailable,
            (await reopened.ActionRecords.LoadAsync(TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task StoredRevalidationUsesCanonicalPayloadHashIdentityAndTimestamp()
    {
        await using var fixture = new SqliteOverrideFixture();
        var requested = JournalData.Requested();
        var fact = Revalidation(requested.ActionItemId, Guid.Parse("61616161-6161-6161-6161-616161616161")) with
        {
            ObservedUtc = requested.RequestedUtc.ToOffset(TimeSpan.FromHours(4)),
        };
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken);
        Assert.Equal(ActionRevalidationWriteOutcome.Persisted,
            (await stores.ActionRecords.AppendRevalidationAsync(fact, TestContext.Current.CancellationToken)).Outcome);

        await using var connection = await fixture.OpenRawAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT revalidation_id, observed_utc, result_kind, reason, observation_payload, observation_payload_sha256 FROM action_revalidations;";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(fact.RevalidationId.ToString("D").ToLowerInvariant(), reader.GetString(0));
        Assert.EndsWith("Z", reader.GetString(1), StringComparison.Ordinal);
        Assert.Equal("still_unknown", reader.GetString(2));
        Assert.Equal("original_path_missing", reader.GetString(3));
        var payload = reader.GetString(4);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), reader.GetString(5));
        Assert.Equal("{\"actionItemId\":\"14141414-1414-1414-1414-141414141414\",\"kind\":1,\"observation\":{\"observedKey\":null,\"observedLength\":null,\"observedSha256\":null,\"pathExists\":false},\"observedUtc\":\"2026-09-10T10:11:12.0000000Z\",\"reason\":1,\"revalidationId\":\"61616161-6161-6161-6161-616161616161\"}", payload);
    }

    private static ActionRevalidationFact Revalidation(Guid actionItemId, Guid revalidationId) =>
        new(revalidationId, actionItemId, new DateTimeOffset(2026, 9, 10, 10, 12, 12, TimeSpan.Zero),
            ActionRevalidationKind.StillUnknown, ActionRevalidationReason.OriginalPathMissing,
            new(false, null, null, null));

    private static ActionRequestedFact SecondRequested(ActionRequestedFact first)
    {
        var second = first with
        {
            ActionItemId = Guid.Parse("24242424-2424-2424-2424-242424242424"),
            Safety = first.Safety with
            {
                Item = first.Safety.Item with
                {
                    Occurrence = first.Safety.Item.Occurrence with { OccurrenceId = 18 },
                },
            },
        };
        var request = first.Safety.Claim.Request with
        {
            RequestId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Items = [second.Safety.Item],
        };
        return second with
        {
            Safety = second.Safety with { Claim = second.Safety.Claim with { Request = request } },
            RequestPayloadSha256 = JournalData.RequestHash(request),
        };
    }
}
