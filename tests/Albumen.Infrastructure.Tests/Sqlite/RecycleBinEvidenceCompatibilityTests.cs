using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Albumen.Application.Action;
using Albumen.Domain.Action;
using Albumen.Infrastructure.Sqlite;
using Xunit;
using static Albumen.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace Albumen.Infrastructure.Tests.Sqlite;

public sealed class RecycleBinEvidenceCompatibilityTests
{
    [Fact]
    public void OldElevenFieldEvidenceKeepsExactJsonBytesWhenNewEvidenceIsUnknown()
    {
        const string oldJson = "{\"ValidPreDeleteCount\":1,\"UnexpectedPreDeleteCount\":0,\"PermissionGranted\":true,\"PerformOperationsHResult\":0,\"OperationsAborted\":false,\"MatchingPostDeleteCount\":1,\"UnexpectedPostDeleteCount\":0,\"DeleteHResult\":0,\"NewlyCreatedItemPresent\":true,\"CallbackException\":false,\"OriginalObjectProvenUnchanged\":false}";
        var evidence = JsonSerializer.Deserialize<RecycleBinEvidence>(oldJson)!;
        Assert.Null(evidence.PreparationHResult);
        Assert.Null(evidence.ValidationReason);
        Assert.Null(evidence.SelectedHandleReleased);
        Assert.Null(evidence.GetAbortedHResult);
        Assert.Null(evidence.RunnerInterrupted);
        Assert.Null(evidence.FinishOperationsHResult);
        Assert.Null(evidence.NamespaceHandoffOccurred);
        Assert.Null(evidence.PerformOperationsEntered);
        Assert.Equal(oldJson, JsonSerializer.Serialize(evidence));
    }

    [Fact]
    public void KnownFalseHandoffAndPerformEntryArePreservedRatherThanOmittedAsUnknown()
    {
        var evidence = new RecycleBinEvidence(0, 0, false, null, null, 0, 0, null, false, false, false)
        { NamespaceHandoffOccurred = false, PerformOperationsEntered = false };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(evidence));
        Assert.False(json.RootElement.GetProperty("NamespaceHandoffOccurred").GetBoolean());
        Assert.False(json.RootElement.GetProperty("PerformOperationsEntered").GetBoolean());
        Assert.Equal(evidence, JsonSerializer.Deserialize<RecycleBinEvidence>(json.RootElement.GetRawText()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OldAndExtendedTerminalEvidenceSurviveReopenWithoutChangingCanonicalBytesOrHash(int version)
    {
        await using var fixture = new SqliteOverrideFixture();
        var requested = JournalData.Requested();
        var terminal = JournalData.Terminal(requested);
        if (version > 0) terminal = terminal with { Evidence = terminal.Evidence with
        {
            PreparationHResult = 0, ValidationReason = ActionSafetyReason.None, SelectedHandleReleased = true,
            GetAbortedHResult = 0, RunnerInterrupted = false, FinishOperationsHResult = 0,
        } };
        if (version == 2) terminal = terminal with { Evidence = terminal.Evidence with
        { NamespaceHandoffOccurred = true, PerformOperationsEntered = true } };
        string payload;
        string hash;
        await using (var stores = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistRequestedAsync(requested, TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistExecutingAsync(JournalData.Executing(requested), TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(ActionJournalWriteOutcome.Persisted, (await stores.ActionJournal.PersistTerminalAsync(terminal, TestContext.Current.CancellationToken)).Outcome);
            await using var connection = await fixture.OpenRawAsync();
            payload = Assert.IsType<string>(await ScalarAsync(connection, "SELECT transition_payload FROM action_transitions WHERE sequence=3;"));
            hash = Assert.IsType<string>(await ScalarAsync(connection, "SELECT transition_payload_sha256 FROM action_transitions WHERE sequence=3;"));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))), hash);
            using var json = JsonDocument.Parse(payload);
            var evidence = json.RootElement.GetProperty("evidence");
            Assert.Equal(version switch { 0 => 11, 1 => 17, _ => 19 }, evidence.EnumerateObject().Count());
            if (version > 0)
            {
                Assert.Equal(0, evidence.GetProperty("preparationHResult").GetInt32());
                Assert.Equal(0, evidence.GetProperty("validationReason").GetInt32());
                Assert.True(evidence.GetProperty("selectedHandleReleased").GetBoolean());
                Assert.Equal(0, evidence.GetProperty("getAbortedHResult").GetInt32());
                Assert.False(evidence.GetProperty("runnerInterrupted").GetBoolean());
                Assert.Equal(0, evidence.GetProperty("finishOperationsHResult").GetInt32());
            }
            if (version == 2)
            {
                Assert.True(evidence.GetProperty("namespaceHandoffOccurred").GetBoolean());
                Assert.True(evidence.GetProperty("performOperationsEntered").GetBoolean());
            }
        }
        await using (var reopened = new SqliteAlbumenStores(fixture.DatabasePath))
        {
            Assert.Equal(ActionJournalWriteOutcome.ExactRetry, (await reopened.ActionJournal.PersistTerminalAsync(terminal, TestContext.Current.CancellationToken)).Outcome);
            Assert.Equal(ActionJournalWriteOutcome.ExactRetry, (await reopened.ActionJournal.PersistExecutingAsync(JournalData.Executing(requested), TestContext.Current.CancellationToken)).Outcome);
        }
        await using var verify = await fixture.OpenRawAsync();
        Assert.Equal(payload, await ScalarAsync(verify, "SELECT transition_payload FROM action_transitions WHERE sequence=3;"));
        Assert.Equal(hash, await ScalarAsync(verify, "SELECT transition_payload_sha256 FROM action_transitions WHERE sequence=3;"));
        Assert.Equal(2L, await ScalarAsync(verify, "SELECT COUNT(*) FROM action_transitions;"));
    }
}
