using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using PhotoSift.Application.Action;
using PhotoSift.Application.Review;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Scan;
using PhotoSift.Infrastructure.Sqlite;
using Xunit;
using static PhotoSift.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace PhotoSift.Infrastructure.Tests.Sqlite;

public sealed class SqliteJournalReviewRegressionTests
{
    [Theory]
    [InlineData("current")]
    [InlineData("members")]
    [InlineData("equality")]
    [InlineData("external-survivors")]
    [InlineData("override-affected")]
    [InlineData("override-content")]
    [InlineData("observation-allocation")]
    [InlineData("request-item")]
    public async Task IncomingMalformedNestedCollectionsCannotCreateRequested(string target)
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        var fact = Malformed(JournalData.Requested(), target);
        Assert.Equal(ActionJournalWriteOutcome.Conflict,
            (await stores.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken)).Outcome);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_items;"));
    }

    [Theory]
    [InlineData("current")]
    [InlineData("members")]
    [InlineData("equality")]
    [InlineData("external-survivors")]
    [InlineData("override-affected")]
    [InlineData("override-content")]
    [InlineData("observation-allocation")]
    [InlineData("request-item")]
    public async Task HashConsistentStoredMalformedCollectionsCannotBecomeExecuting(string target)
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        var fact = JournalData.Requested();
        Assert.Equal(ActionJournalWriteOutcome.Persisted,
            (await stores.ActionJournal.PersistRequestedAsync(fact, TestContext.Current.CancellationToken)).Outcome);
        string changed;
        await using (var connection = await fixture.OpenRawAsync())
        {
            var node = JsonNode.Parse(Assert.IsType<string>(await ScalarAsync(connection, "SELECT requested_payload FROM action_items;")))!;
            var body = node["fact"]!;
            var safety = body["safety"]!;
            switch (target)
            {
                case "current": safety["input"]!["currentOccurrences"] = new JsonArray((JsonNode?)null); break;
                case "members": safety["input"]!["approvedGroup"]!["members"] = new JsonArray((JsonNode?)null); break;
                case "equality": safety["input"]!["approvedGroup"]!["equality"] = new JsonArray((JsonNode?)null); break;
                case "external-survivors": safety["decision"]!["survivor"]!["externalSurvivors"] = new JsonArray((JsonNode?)null); break;
                case "override-affected": body["overrideEvidence"]!["affectedPhysicalRevisions"] = new JsonArray((JsonNode?)null); break;
                case "override-content": body["overrideEvidence"]!["affectedPhysicalRevisions"]![0]!["content"] = null; break;
                case "observation-allocation": safety["observations"]![0]!["opened"]!["allocation"] = null; break;
                case "request-item": safety["claim"]!["request"]!["items"]![0] = null; break;
            }
            changed = node.ToJsonString();
            await using var mutation = connection.CreateCommand();
            mutation.CommandText = "UPDATE action_items SET requested_payload=$payload, requested_payload_sha256=$hash;";
            mutation.Parameters.AddWithValue("$payload", changed);
            mutation.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(changed))));
            await mutation.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(ActionJournalWriteOutcome.Unavailable,
            (await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(fact), TestContext.Current.CancellationToken)).Outcome);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions;"));
        Assert.Equal(changed, await ScalarAsync(verify, "SELECT requested_payload FROM action_items;"));
    }

    [Theory]
    [InlineData("requested", false, false)] [InlineData("requested", false, true)]
    [InlineData("requested", true, false)] [InlineData("requested", true, true)]
    [InlineData("executing", false, false)] [InlineData("executing", false, true)]
    [InlineData("executing", true, false)] [InlineData("executing", true, true)]
    [InlineData("terminal", false, false)] [InlineData("terminal", false, true)]
    [InlineData("terminal", true, false)] [InlineData("terminal", true, true)]
    public async Task EveryAcknowledgmentChecksCorruptOrConflictingSiblingsInBothSortPositions(string operation, bool corrupt, bool siblingAfter)
    {
        await using var fixture = new SqliteOverrideFixture();
        await using var donor = new SqliteOverrideFixture();
        var target = JournalData.Requested();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        Assert.Equal(ActionJournalWriteOutcome.Persisted,
            (await stores.ActionJournal.PersistRequestedAsync(target, TestContext.Current.CancellationToken)).Outcome);
        if (operation == "terminal") Assert.Equal(ActionJournalWriteOutcome.Persisted,
            (await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(target), TestContext.Current.CancellationToken)).Outcome);
        var sibling = Sibling(target, siblingAfter, conflict: !corrupt);
        await using (var donorStores = new SqlitePhotoSiftStores(donor.DatabasePath))
            Assert.Equal(ActionJournalWriteOutcome.Persisted,
                (await donorStores.ActionJournal.PersistRequestedAsync(sibling, TestContext.Current.CancellationToken)).Outcome);
        await using (var source = await donor.OpenRawAsync())
        await using (var destination = await fixture.OpenRawAsync())
        {
            if (corrupt) await ExecuteAsync(source, "UPDATE action_items SET requested_payload_sha256='broken';");
            await CopyItemAsync(source, destination);
        }
        var result = operation switch
        {
            "requested" => await stores.ActionJournal.PersistRequestedAsync(target, TestContext.Current.CancellationToken),
            "executing" => await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(target), TestContext.Current.CancellationToken),
            _ => await stores.ActionJournal.PersistTerminalAsync(JournalData.Terminal(target), TestContext.Current.CancellationToken),
        };
        Assert.Equal(corrupt ? ActionJournalWriteOutcome.Unavailable : ActionJournalWriteOutcome.Conflict, result.Outcome);
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(2L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_items;"));
        Assert.Equal(operation == "terminal" ? 1L : 0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions;"));
    }

    private static ActionRequestedFact Malformed(ActionRequestedFact fact, string target)
    {
        var safety = fact.Safety;
        return target switch
        {
            "current" => fact with { Safety = safety with { Input = safety.Input with { CurrentOccurrences = [null!] } } },
            "members" => fact with { Safety = safety with { Input = safety.Input with { ApprovedGroup = safety.Input.ApprovedGroup with { Members = [null!] } } } },
            "equality" => fact with { Safety = safety with { Input = safety.Input with { ApprovedGroup = safety.Input.ApprovedGroup with { Equality = [null!] } } } },
            "external-survivors" => fact with { Safety = safety with { Decision = safety.Decision with { Survivor = safety.Decision.Survivor with { ExternalSurvivors = [null!] } } } },
            "override-affected" => fact with { OverrideEvidence = fact.OverrideEvidence! with { AffectedPhysicalRevisions = [null!] } },
            "override-content" => fact with { OverrideEvidence = fact.OverrideEvidence! with { AffectedPhysicalRevisions = [fact.OverrideEvidence!.AffectedPhysicalRevisions[0] with { Content = null! }] } },
            "observation-allocation" => fact with { Safety = safety with { Observations = [safety.Observations[0] with { Opened = safety.Observations[0].Opened with { Allocation = null! } }] } },
            _ => fact with { Safety = safety with { Claim = safety.Claim with { Request = safety.Claim.Request with { Items = [null!, safety.Item] } } } },
        };
    }

    private static ActionRequestedFact Sibling(ActionRequestedFact target, bool after, bool conflict)
    {
        var request = target.Safety.Claim.Request;
        if (conflict) request = request with { SelectionRevision = request.SelectionRevision + 1 };
        var item = request.Items[0];
        var occurrence = new ScanFileOccurrence(item.Occurrence, item.DiscoveredPath, item.ResolvedPath, item.PhysicalFileKey,
            new(ScanStageState.Succeeded, ScanReason.None), new(ScanStageState.Succeeded, ScanReason.None), item.Content, item.Allocation);
        var group = target.Safety.Input.ApprovedGroup with { GroupId = item.GroupId, Members = [occurrence] };
        var input = target.Safety.Input with { ApprovedGroup = group, CurrentOccurrences = [occurrence],
            FreshEligibleOccurrenceIds = [99], RemainingApprovedOccurrenceIds = [99], OverrideApprovedOccurrenceIds = [99] };
        return target with
        {
            ActionItemId = Guid.Parse(after ? "ffffffff-ffff-ffff-ffff-ffffffffffff" : "01010101-0101-0101-0101-010101010101"),
            RequestPayloadSha256 = JournalData.RequestHash(request),
            Safety = target.Safety with { Claim = target.Safety.Claim with { Request = request }, Item = item, Input = input,
                Decision = ActionSafetyPolicy.Evaluate(input), Observations = [target.Safety.Observations[0] with { ApprovedOccurrence = occurrence }], PrecedingOutcomes = [] },
        };
    }

    private static async Task CopyItemAsync(SqliteConnection source, SqliteConnection destination)
    {
        await using var read = source.CreateCommand();
        read.CommandText = "SELECT action_item_id, request_id, request_payload_sha256, occurrence_id, occurrence_revision, requested_utc, requested_payload, requested_payload_sha256 FROM action_items;";
        await using var reader = await read.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        await using var insert = destination.CreateCommand();
        insert.CommandText = "INSERT INTO action_items VALUES ($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7);";
        for (var index = 0; index < 8; index++) insert.Parameters.AddWithValue($"$p{index}", reader.GetValue(index));
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
