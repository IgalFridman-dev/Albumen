using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Albumen.Application.Action;
using Albumen.Application.Review;
using Albumen.Application.Scan;
using Albumen.Domain.Action;
using Albumen.Domain.Review;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;
using Albumen.Infrastructure.Sqlite;
using Xunit;
using static Albumen.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace Albumen.Infrastructure.Tests.Sqlite;

public sealed class SqliteActionJournalStoreTests
{
    [Fact]
    public async Task FractionalSqliteIntegersCannotMasqueradeAsExactRequestedPins()
    {
        await using var fixture = new SqliteOverrideFixture();
        var fact = JournalData.Requested();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken)).Outcome);
        await using (var connection = await fixture.OpenRawAsync())
            await ExecuteAsync(connection, "UPDATE action_items SET occurrence_id=1.5, occurrence_revision=5.5;");
        Assert.Equal(ActionJournalWriteOutcome.Unavailable, (await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(fact), TestContext.Current.CancellationToken)).Outcome);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions;"));
    }

    [Fact]
    public async Task DigestTextUsesUppercaseCanonicalEncodingForEquivalentHexadecimalEvidence()
    {
        await using var fixture = new SqliteOverrideFixture();
        var fact = JournalData.Requested();
        var observation = fact.Safety.Observations[0];
        var lower = fact with { Safety = fact.Safety with { Observations = [observation with { Digest = observation.Digest with { Sha256 = new string('a', 64) } }] } };
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistRequestedAsync(lower, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.ExactRetry, (await stores.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken)).Outcome);
        await using var verify = await fixture.OpenRawAsync();
        using var json = JsonDocument.Parse(Assert.IsType<string>(await ScalarAsync(verify, "SELECT requested_payload FROM action_items;")));
        Assert.Equal(new string('A', 64), json.RootElement.GetProperty("fact").GetProperty("safety").GetProperty("observations")[0].GetProperty("digest").GetProperty("sha256").GetString());
    }

    [Theory]
    [InlineData("fact")]
    [InlineData("safety")]
    [InlineData("group")]
    public async Task HashConsistentNullRequestedPayloadFailsClosedWithoutRepair(string target)
    {
        await using var fixture = new SqliteOverrideFixture();
        var fact = JournalData.Requested();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken)).Outcome);
        string changed;
        await using (var connection = await fixture.OpenRawAsync())
        {
            var payload = Assert.IsType<string>(await ScalarAsync(connection, "SELECT requested_payload FROM action_items;"));
            var json = JsonNode.Parse(payload)!;
            if (target == "fact") json["fact"] = null;
            else if (target == "safety") json["fact"]!["safety"] = null;
            else json["fact"]!["safety"]!["input"]!["approvedGroup"] = null;
            changed = json.ToJsonString();
            await using var mutation = connection.CreateCommand();
            mutation.CommandText = "UPDATE action_items SET requested_payload=$payload, requested_payload_sha256=$hash;";
            mutation.Parameters.AddWithValue("$payload", changed);
            mutation.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(changed))));
            await mutation.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(ActionJournalWriteOutcome.Unavailable, (await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(fact), TestContext.Current.CancellationToken)).Outcome);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(changed, await ScalarAsync(verify, "SELECT requested_payload FROM action_items;"));
        Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions;"));
    }

    [Fact]
    public async Task NonUtcApprovalIdentityRemainsVerifiableAfterCanonicalUtcReadback()
    {
        await using var fixture = new SqliteOverrideFixture();
        var fact = JournalData.Requested();
        var request = fact.Safety.Claim.Request with { ApprovedUtc = fact.RequestedUtc.ToOffset(TimeSpan.FromHours(3)) };
        fact = fact with { Safety = fact.Safety with { Claim = fact.Safety.Claim with { Request = request } }, RequestPayloadSha256 = JournalData.RequestHash(request) };
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(fact), TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.ExactRetry, (await stores.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task EveryActionConnectionVerifiesNumericFullBeforeTransactionAndReturnsOnlyAfterCommit()
    {
        await using var fixture = new SqliteOverrideFixture();
        var checks = 0;
        var observer = new SqliteDatabaseObserver(BeforeActionTransaction: async (connection, _) =>
        {
            Assert.Equal(2L, await ScalarAsync(connection, "PRAGMA synchronous;"));
            Assert.Equal(1L, await ScalarAsync(connection, "PRAGMA foreign_keys;"));
            Assert.Equal("wal", await ScalarAsync(connection, "PRAGMA journal_mode;"));
            // SQLite rejects synchronous changes in an active transaction.
            await ExecuteAsync(connection, "PRAGMA synchronous=FULL;");
            checks++;
        });
        var fact = JournalData.Requested();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath, observer);
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken)).Outcome);
        await using (var verify = await fixture.OpenRawAsync()) Assert.Equal(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_items;"));
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(fact), TestContext.Current.CancellationToken)).Outcome);
        await using (var verify = await fixture.OpenRawAsync()) Assert.Equal(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions WHERE sequence=2;"));
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistTerminalAsync(JournalData.Terminal(fact), TestContext.Current.CancellationToken)).Outcome);
        await using (var verify = await fixture.OpenRawAsync()) Assert.Equal(2L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions;"));
        Assert.Equal(3, checks);
    }

    [Fact]
    public async Task RequestedPayloadRetainsCompleteRequestSafetyFactsAndCanonicalHash()
    {
        await using var fixture = new SqliteOverrideFixture();
        var fact = JournalData.Requested();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken)).Outcome);
        await using var connection = await fixture.OpenRawAsync();
        var payload = Assert.IsType<string>(await ScalarAsync(connection, "SELECT requested_payload FROM action_items;"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), await ScalarAsync(connection, "SELECT requested_payload_sha256 FROM action_items;"));
        using var json = JsonDocument.Parse(payload);
        var root = json.RootElement;
        Assert.Equal("action-safety-v1", root.GetProperty("actionSafetyPolicyVersion").GetString());
        Assert.Equal("review-action-claim-v1", root.GetProperty("claimVersion").GetString());
        var stored = root.GetProperty("fact");
        Assert.Equal(fact.ActionItemId.ToString("D"), stored.GetProperty("actionItemId").GetString());
        Assert.Equal("2026-09-10T10:11:12.0000000Z", stored.GetProperty("requestedUtc").GetString());
        var safety = stored.GetProperty("safety");
        Assert.Equal(@"C:\Photos\é';--.jpg", safety.GetProperty("item").GetProperty("discoveredPath").GetString());
        Assert.Equal(7L, safety.GetProperty("claim").GetProperty("request").GetProperty("scan").GetProperty("scopeRevision").GetInt64());
        Assert.Equal(1, safety.GetProperty("claim").GetProperty("request").GetProperty("scope").GetProperty("selection").GetProperty("recursion").GetInt32());
        Assert.Equal(8192, safety.GetProperty("observations")[0].GetProperty("final").GetProperty("allocation").GetProperty("allocatedBytes").GetInt64());
        Assert.Equal(3, safety.GetProperty("observations")[0].GetProperty("final").GetProperty("allocation").GetProperty("linkCount").GetInt32());
        Assert.Equal(new string('A', 64), safety.GetProperty("observations")[0].GetProperty("digest").GetProperty("sha256").GetString());
        Assert.Equal(fact.Safety.Claim.ClaimId.Value.ToString("D"), safety.GetProperty("claim").GetProperty("claimId").GetProperty("value").GetString());
        Assert.Equal(1, safety.GetProperty("precedingOutcomes").GetArrayLength());
        Assert.Equal(99, safety.GetProperty("precedingOutcomes")[0].GetProperty("occurrenceId").GetInt64());
        Assert.Equal(fact.OverrideEvidence!.ConsequenceTextVersion, stored.GetProperty("overrideEvidence").GetProperty("consequenceTextVersion").GetString());
        Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("overridePayloadSha256").GetString()!);
    }

    [Fact]
    public async Task AllThreeExactRetriesObserveExistingRowsWithoutCreatingAnotherTransition()
    {
        await using var fixture = new SqliteOverrideFixture();
        var fact = JournalData.Requested();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        var journal = stores.ActionJournal;
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await journal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.ExactRetry, (await journal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await journal.PersistExecutingAsync(JournalData.Executing(fact), TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.ExactRetry, (await journal.PersistExecutingAsync(JournalData.Executing(fact), TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await journal.PersistTerminalAsync(JournalData.Terminal(fact), TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.ExactRetry, (await journal.PersistTerminalAsync(JournalData.Terminal(fact), TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.ExactRetry, (await journal.PersistExecutingAsync(JournalData.Executing(fact), TestContext.Current.CancellationToken)).Outcome);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_items;"));
        Assert.Equal(2L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions;"));
    }

    [Theory]
    [InlineData("item-id")]
    [InlineData("request-payload")]
    [InlineData("requested-time")]
    [InlineData("safety")]
    [InlineData("override")]
    [InlineData("false-hash")]
    public async Task RequestedIdentityAndCompletePayloadConflictsCannotOverwriteHistory(string mutation)
    {
        await using var fixture = new SqliteOverrideFixture();
        var first = JournalData.Requested();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistRequestedAsync(first, TestContext.Current.CancellationToken)).Outcome);
        var changedRequest = first.Safety.Claim.Request with { SelectionRevision = 100 };
        var conflicting = mutation switch
        {
            "item-id" => first with { ActionItemId = Guid.NewGuid() },
            "request-payload" => first with { ActionItemId = Guid.NewGuid(), Safety = first.Safety with { Claim = first.Safety.Claim with { Request = changedRequest } }, RequestPayloadSha256 = JournalData.RequestHash(changedRequest) },
            "requested-time" => first with { RequestedUtc = first.RequestedUtc.AddSeconds(1) },
            "safety" => first with { Safety = first.Safety with { PrecedingOutcomes = [] } },
            "override" => first with { OverrideEvidence = first.OverrideEvidence! with { ConsequenceTextVersion = "different" } },
            _ => first with { RequestPayloadSha256 = new string('0', 64) },
        };
        Assert.Equal(ActionJournalWriteOutcome.Conflict, (await stores.ActionJournal.PersistRequestedAsync(conflicting, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ActionJournalWriteOutcome.ExactRetry, (await stores.ActionJournal.PersistRequestedAsync(first, TestContext.Current.CancellationToken)).Outcome);
    }

    [Theory]
    [InlineData(2)] [InlineData(3)]
    public async Task TransitionRequiresItsExactPredecessor(int sequence)
    {
        await using var fixture = new SqliteOverrideFixture();
        var fact = JournalData.Requested();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        if (sequence == 3) Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken)).Outcome);
        var result = sequence == 2
            ? await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(fact), TestContext.Current.CancellationToken)
            : await stores.ActionJournal.PersistTerminalAsync(JournalData.Terminal(fact), TestContext.Current.CancellationToken);
        Assert.Equal(ActionJournalWriteOutcome.InvalidPredecessor, result.Outcome);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions;"));
    }

    [Theory]
    [InlineData(2)] [InlineData(3)]
    public async Task ConflictingTransitionCannotRewriteAnExistingSequence(int sequence)
    {
        await using var fixture = new SqliteOverrideFixture();
        var fact = JournalData.Requested();
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        await stores.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken);
        await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(fact), TestContext.Current.CancellationToken);
        await stores.ActionJournal.PersistTerminalAsync(JournalData.Terminal(fact), TestContext.Current.CancellationToken);
        var result = sequence == 2
            ? await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(fact) with { ExecutingUtc = fact.RequestedUtc.AddHours(2) }, TestContext.Current.CancellationToken)
            : await stores.ActionJournal.PersistTerminalAsync(JournalData.Terminal(fact) with { Kind = ActionTerminalKind.Recycled }, TestContext.Current.CancellationToken);
        Assert.Equal(ActionJournalWriteOutcome.Conflict, result.Outcome);
    }

    [Theory]
    [InlineData(1, "delete")] [InlineData(1, "rewrite")] [InlineData(1, "throw")]
    [InlineData(2, "delete")] [InlineData(2, "rewrite")] [InlineData(2, "throw")]
    [InlineData(3, "delete")] [InlineData(3, "rewrite")] [InlineData(3, "throw")]
    public async Task MissingRewrittenOrFailedInsertRollsBackAndCannotAcknowledge(int sequence, string mutation)
    {
        await using var fixture = new SqliteOverrideFixture();
        var fact = JournalData.Requested();
        await using (var seed = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            if (sequence > 1) await seed.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken);
            if (sequence > 2) await seed.ActionJournal.PersistExecutingAsync(JournalData.Executing(fact), TestContext.Current.CancellationToken);
        }
        var observer = new SqliteDatabaseObserver(AfterActionInsert: async (connection, transaction, _) =>
        {
            if (mutation == "throw") throw new IOException("Before commit");
            var table = sequence == 1 ? "action_items" : "action_transitions";
            var column = sequence == 1 ? "requested_payload" : "transition_payload";
            await ExecuteAsync(connection, mutation == "delete" ? $"DELETE FROM {table};" : $"UPDATE {table} SET {column}='{{}}';", transaction);
        });
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath, observer))
        {
            var result = sequence switch
            {
                1 => await stores.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken),
                2 => await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(fact), TestContext.Current.CancellationToken),
                _ => await stores.ActionJournal.PersistTerminalAsync(JournalData.Terminal(fact), TestContext.Current.CancellationToken),
            };
            Assert.Equal(ActionJournalWriteOutcome.Unavailable, result.Outcome);
        }
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(sequence == 1 ? 0L : 1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_items;"));
        Assert.Equal(sequence == 3 ? 1L : 0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions;"));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task CancellationAfterInsertRollsBackBeforeCommit(int sequence)
    {
        await using var fixture = new SqliteOverrideFixture();
        var fact = JournalData.Requested();
        await using (var seed = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            if (sequence > 1) await seed.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken);
            if (sequence > 2) await seed.ActionJournal.PersistExecutingAsync(JournalData.Executing(fact), TestContext.Current.CancellationToken);
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var observer = new SqliteDatabaseObserver(AfterActionInsert: (_, _, _) => { cancellation.Cancel(); return ValueTask.CompletedTask; });
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath, observer);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            _ = sequence switch
            {
                1 => await stores.ActionJournal.PersistRequestedAsync(fact, cancellation.Token),
                2 => await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(fact), cancellation.Token),
                _ => await stores.ActionJournal.PersistTerminalAsync(JournalData.Terminal(fact), cancellation.Token),
            };
        });
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(sequence == 1 ? 0L : 1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_items;"));
        Assert.Equal(sequence == 3 ? 1L : 0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions;"));
    }

    [Fact]
    public async Task LockedDatabaseIsUnavailableWithoutCreatingIntent()
    {
        await using var fixture = new SqliteOverrideFixture();
        await SeedAsync(fixture, 2);
        await using var locked = await fixture.OpenRawAsync();
        await using var transaction = locked.BeginTransaction(deferred: false);
        await using var stores = new SqliteAlbumenStores(fixture.DatabasePath);
        Assert.Equal(ActionJournalWriteOutcome.Unavailable, (await stores.ActionJournal.PersistRequestedAsync(JournalData.Requested(), TestContext.Current.CancellationToken)).Outcome);
    }
}

internal static class JournalData
{
    internal static ActionRequestedFact Requested()
    {
        var evidence = OverrideEvidenceData.Create();
        var pin = evidence.Pin.Session.Scan;
        var key = new PhysicalFileKey(19, new(23, 29));
        var content = new ContentRevision(key, 101, new string('A', 64));
        var item = new ApprovedActionItem(evidence.Pin.GroupId, 17, new(pin, 1, 5, 2),
            @"C:\Photos\é';--.jpg", @"C:\Photos\é';--.jpg", key, content, new(8192, 3, true), evidence.EvidenceId);
        var previous = item with { Occurrence = item.Occurrence with { OccurrenceId = 99 }, GroupId = Guid.Parse("11111111-1111-1111-1111-111111111111") };
        var scope = new ResolvedScope(new(@"C:\Photos", ScopeRecursion.IncludeSubfolders), @"C:\Photos", new(19, 0, 99));
        var request = new ApprovedActionRequest(Guid.Parse("12121212-1212-1212-1212-121212121212"), DateTimeOffset.UnixEpoch,
            evidence.Pin.Session.ReviewSessionId, 6, pin, scope, [previous, item], [1, 99],
            new(2, 1, ScanStatus.Complete, new(0, 0, 0, 0, 0, 0, 0), new(8192, SelectionRecoveryUnavailableReason.None), "Recycle Bin", "Revalidate"));
        var occurrence = new ScanFileOccurrence(item.Occurrence, item.DiscoveredPath, item.ResolvedPath, key,
            new(ScanStageState.Succeeded, ScanReason.None), new(ScanStageState.Succeeded, ScanReason.None), content, item.Allocation);
        var group = new CandidateGroup(item.GroupId, item.GroupRevision, pin, CandidateCategory.Exact,
            [occurrence], [], false, null, new(0, RecoveryUnavailableReason.None));
        var input = new ActionSafetyInput(group, [occurrence], [1], [1], [1], true, false);
        var observed = new ProtectedFileFacts(key, 101, item.ResolvedPath, DateTimeOffset.UnixEpoch, item.Allocation);
        var safety = new ActionSafetyEnvelope(new(new(Guid.Parse("13131313-1313-1313-1313-131313131313")), request), item,
            input, ActionSafetyPolicy.Evaluate(input), [new(occurrence, observed, observed, new(101, new string('A', 64)))],
            [new(previous.GroupId, 99, PriorActionDisposition.AffirmedNotRemoved)]);
        return new(Guid.Parse("14141414-1414-1414-1414-141414141414"), new(2026, 9, 10, 10, 11, 12, TimeSpan.Zero), safety, RequestHash(request), evidence);
    }
    internal static string RequestHash(ApprovedActionRequest request) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));
    internal static ActionExecutingFact Executing(ActionRequestedFact fact) => new(fact.ActionItemId, fact.RequestedUtc.AddSeconds(1));
    internal static ActionTerminalFact Terminal(ActionRequestedFact fact) => new(fact.ActionItemId, fact.RequestedUtc.AddSeconds(2), ActionTerminalKind.NotRemoved,
        ActionSafetyReason.CannotRecycle, new(1, 0, false, null, null, 0, 0, null, false, false, true));
}
