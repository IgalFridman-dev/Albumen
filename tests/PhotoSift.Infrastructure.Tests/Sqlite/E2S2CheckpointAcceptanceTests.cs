using Microsoft.Data.Sqlite;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Infrastructure.Sqlite;
using Xunit;
using static PhotoSift.Infrastructure.Tests.Sqlite.SqliteSchemaVersionTwoTests;

namespace PhotoSift.Infrastructure.Tests.Sqlite;

public sealed class E2S2CheckpointAcceptanceTests
{
    private static readonly StageOutcome Succeeded =
        new(ScanStageState.Succeeded, ScanReason.None);
    private static readonly StageOutcome Running =
        new(ScanStageState.Running, ScanReason.None);
    private static readonly StageOutcome Pending =
        new(ScanStageState.Pending, ScanReason.None);
    private static readonly StageOutcome Unsupported =
        new(ScanStageState.Unsupported, ScanReason.UnsupportedExtension);

    [Fact]
    public async Task ReopenedRunningCheckpointProjectsInterruptionWithoutRewritingStoredTruth()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using (var setup = new SqlitePhotoSiftStores(fixture.DatabasePath))
        {
            await BootstrapAsync(setup, scan);
            Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
                (await setup.ScanCheckpoints.WriteInitialOccurrenceAsync(
                    Initial(scan, 7, Succeeded, Running), TestContext.Current.CancellationToken)).Outcome);
        }
        var before = await AuthorityFingerprintAsync(fixture);

        ScanCheckpointReadResult read;
        await using (var reopened = new SqlitePhotoSiftStores(fixture.DatabasePath))
            read = await reopened.ScanCheckpoints.ReadAsync(
                scan.Pin.ScanId, TestContext.Current.CancellationToken);

        var exact = Assert.Single(read.Stages, stage => stage.Stored.Stage == ScanStage.Exact);
        Assert.Equal(ScanCheckpointReadOutcome.Found, read.Outcome);
        Assert.Equal(Running, exact.Stored.Outcome);
        Assert.Equal(new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure), exact.Outcome);
        Assert.True(exact.RecoveredFromInterruption);
        Assert.Equal(1, exact.Stored.Revision);
        Assert.Equal(Transition(107), exact.Stored.TransitionId);
        Assert.Equal(before, await AuthorityFingerprintAsync(fixture));
    }

    [Fact]
    public async Task TerminalUnsuccessfulEnumerationKeepsExactNotApplicableReasonAndPrerequisite()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using (var setup = new SqlitePhotoSiftStores(fixture.DatabasePath))
        {
            await BootstrapAsync(setup, scan);
            await setup.ScanCheckpoints.WriteInitialOccurrenceAsync(
                Initial(scan, 7, Unsupported,
                    new StageOutcome(ScanStageState.NotApplicable, ScanReason.UnsupportedExtension)),
                TestContext.Current.CancellationToken);
        }

        await using var reopened = new SqlitePhotoSiftStores(fixture.DatabasePath);
        var read = await reopened.ScanCheckpoints.ReadAsync(
            scan.Pin.ScanId, TestContext.Current.CancellationToken);

        var enumeration = Assert.Single(read.Stages, stage => stage.Stored.Stage == ScanStage.Enumeration);
        var exact = Assert.Single(read.Stages, stage => stage.Stored.Stage == ScanStage.Exact);
        Assert.Equal(Unsupported, enumeration.Stored.Outcome);
        Assert.Equal(new StageOutcome(ScanStageState.NotApplicable, ScanReason.UnsupportedExtension),
            exact.Stored.Outcome);
        Assert.Equal(new ScanStagePrerequisite(ScanStage.Enumeration, Unsupported), exact.Stored.Prerequisite);
        Assert.Equal(new ScanAccounting(1, 0, 1, 0, 0, 0), read.Accounting);
    }

    [Fact]
    public async Task ReadRepairsOnlyDriftedAccountingFromAuthoritativeRows()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using (var setup = new SqlitePhotoSiftStores(fixture.DatabasePath))
        {
            await BootstrapAsync(setup, scan);
            await setup.ScanCheckpoints.WriteInitialOccurrenceAsync(
                Initial(scan, 7, Succeeded, Running), TestContext.Current.CancellationToken);
        }
        await MutateAsync(fixture, """
            UPDATE scan_accounting
            SET discovered=0, analyzed=0, unsupported=0, failed=0, canceled=0, outstanding=0;
            """);
        var authoritativeBefore = await AuthorityFingerprintAsync(fixture);

        ScanCheckpointReadResult read;
        await using (var reopened = new SqlitePhotoSiftStores(fixture.DatabasePath))
            read = await reopened.ScanCheckpoints.ReadAsync(
                scan.Pin.ScanId, TestContext.Current.CancellationToken);

        Assert.Equal(ScanCheckpointReadOutcome.Found, read.Outcome);
        Assert.Equal(new ScanAccounting(1, 0, 0, 0, 0, 1), read.Accounting);
        Assert.Equal(authoritativeBefore, await AuthorityFingerprintAsync(fixture));
        Assert.Equal("1|0|0|0|0|1", await AccountingFingerprintAsync(fixture));
    }

    [Fact]
    public async Task FailedAggregateRepairReturnsUnavailableInsteadOfStaleFoundData()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using (var setup = new SqlitePhotoSiftStores(fixture.DatabasePath))
        {
            await BootstrapAsync(setup, scan);
            await setup.ScanCheckpoints.WriteInitialOccurrenceAsync(
                Initial(scan, 7, Succeeded, Running), TestContext.Current.CancellationToken);
        }
        await MutateAsync(fixture, """
            UPDATE scan_accounting
            SET discovered=0, analyzed=0, unsupported=0, failed=0, canceled=0, outstanding=0;
            """);
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        Assert.Equal(ScanIdentityReadOutcome.Found,
            (await stores.ScanIdentities.ReadScanAsync(
                scan.Pin.ScanId, TestContext.Current.CancellationToken)).Outcome);
        await using var lockConnection = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite);
        await using var writeLock = lockConnection.BeginTransaction(deferred: false);

        var read = await stores.ScanCheckpoints.ReadAsync(
            scan.Pin.ScanId, TestContext.Current.CancellationToken);

        Assert.Equal(ScanCheckpointReadOutcome.Unavailable, read.Outcome);
        Assert.Null(read.Lifecycle);
        Assert.Empty(read.Stages);
        Assert.Null(read.Accounting);
        Assert.Equal("0|0|0|0|0|0", await AccountingFingerprintAsync(fixture));
    }

    public static TheoryData<string> AuthoritativeCorruptions => new()
    {
        {
            "PRAGMA ignore_check_constraints=ON; "
            + "UPDATE scan_lifecycle SET state=99;"
        },
        {
            "PRAGMA foreign_keys=OFF; "
            + "INSERT INTO scan_lifecycle SELECT upper(scan_id), revision, transition_id, state FROM scan_lifecycle;"
        },
        {
            "PRAGMA ignore_check_constraints=ON; "
            + "UPDATE scan_stage_checkpoints SET state=2, reason=9 WHERE stage=1;"
        },
        {
            "UPDATE scan_stage_checkpoints "
            + "SET state=7, reason=9, prerequisite_state=4, prerequisite_reason=9 WHERE stage=1;"
        },
        {
            "PRAGMA foreign_keys=OFF; "
            + "UPDATE scan_stage_checkpoints "
            + "SET scan_id='aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa' WHERE stage=1;"
        },
        {
            "DELETE FROM scan_stage_checkpoints WHERE stage=1;"
        },
    };

    [Theory]
    [MemberData(nameof(AuthoritativeCorruptions))]
    public async Task MalformedDuplicateMixedOrContradictoryAuthorityIsCorruptAndNeverRepaired(
        string mutation)
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using (var setup = new SqlitePhotoSiftStores(fixture.DatabasePath))
        {
            await BootstrapAsync(setup, scan);
            await setup.ScanCheckpoints.WriteInitialOccurrenceAsync(
                Initial(scan, 7, Succeeded, Running), TestContext.Current.CancellationToken);
        }
        await MutateAsync(fixture, mutation);
        var before = await WholeCheckpointFingerprintAsync(fixture);

        ScanCheckpointReadResult read;
        await using (var reopened = new SqlitePhotoSiftStores(fixture.DatabasePath))
            read = await reopened.ScanCheckpoints.ReadAsync(
                scan.Pin.ScanId, TestContext.Current.CancellationToken);

        Assert.Equal(ScanCheckpointReadOutcome.Corrupt, read.Outcome);
        Assert.Null(read.Lifecycle);
        Assert.Empty(read.Stages);
        Assert.Null(read.Accounting);
        Assert.Equal(before, await WholeCheckpointFingerprintAsync(fixture));
    }

    [Fact]
    public async Task OccurrencesWithoutCompleteRequiredStageRowsCannotBeSilentlyOmitted()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using (var setup = new SqlitePhotoSiftStores(fixture.DatabasePath))
        {
            await BootstrapAsync(setup, scan);
            await setup.ScanCheckpoints.WriteInitialOccurrenceAsync(
                Initial(scan, 7, Succeeded, Running), TestContext.Current.CancellationToken);
        }
        await MutateAsync(fixture, """
            INSERT INTO scan_file_occurrences (
                scan_id, occurrence_id, occurrence_revision, attempt,
                discovered_path, resolved_path, volume_serial, file_id_high, file_id_low)
            SELECT scan_id, 8, 1, 1,
                   'C:\photos\omitted.jpg', 'C:\photos\omitted.jpg', NULL, NULL, NULL
            FROM scans;
            """);

        await using var reopened = new SqlitePhotoSiftStores(fixture.DatabasePath);
        var read = await reopened.ScanCheckpoints.ReadAsync(
            scan.Pin.ScanId, TestContext.Current.CancellationToken);

        Assert.Equal(ScanCheckpointReadOutcome.Corrupt, read.Outcome);
    }

    [Fact]
    public async Task AccountingKeepsAllCategoriesSeparateAndCountsOccurrencesOnce()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        await BootstrapAsync(stores, scan);
        for (var occurrenceId = 1L; occurrenceId <= 6; occurrenceId++)
            await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(
                Initial(scan, occurrenceId, Succeeded, Running), TestContext.Current.CancellationToken);
        await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(
            Initial(scan, 7, Unsupported,
                new StageOutcome(ScanStageState.NotApplicable, ScanReason.UnsupportedExtension)),
            TestContext.Current.CancellationToken);

        var terminal = new[]
        {
            Succeeded,
            new StageOutcome(ScanStageState.Unsupported, ScanReason.CodecUnavailable),
            new StageOutcome(ScanStageState.FailedRetryable, ScanReason.ReadFailure),
            new StageOutcome(ScanStageState.FailedTerminal, ScanReason.UnknownSafety),
            new StageOutcome(ScanStageState.Canceled, ScanReason.Canceled),
        };
        for (var index = 0; index < terminal.Length; index++)
            await stores.ScanCheckpoints.TransitionStageAsync(new(
                scan.Pin.ScanId, index + 1, ScanStage.Exact, 1,
                Transition(200 + index), terminal[index]), TestContext.Current.CancellationToken);

        var read = await stores.ScanCheckpoints.ReadAsync(
            scan.Pin.ScanId, TestContext.Current.CancellationToken);

        Assert.Equal(14, read.Stages.Length);
        Assert.Equal(new ScanAccounting(7, 1, 2, 2, 1, 1), read.Accounting);
    }

    [Fact]
    public async Task CompleteRejectsAnEmptyScanAtomically()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        await BootstrapToExactAsync(stores, scan);

        var result = await CompleteAsync(stores, scan, expectedRevision: 3);

        Assert.Equal(ScanCheckpointWriteOutcome.IllegalTransition, result.Outcome);
        Assert.Equal("2|3", await LifecycleStateRevisionAsync(fixture));
    }

    [Theory]
    [InlineData(ScanStageState.Pending)]
    [InlineData(ScanStageState.Running)]
    public async Task CompleteRejectsOutstandingRowsAtomically(ScanStageState state)
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        await BootstrapAsync(stores, scan);
        await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(
            Initial(scan, 7, Succeeded, state == ScanStageState.Pending ? Pending : Running),
            TestContext.Current.CancellationToken);
        await MoveToExactAsync(stores, scan);

        var result = await CompleteAsync(stores, scan, expectedRevision: 3);

        Assert.Equal(ScanCheckpointWriteOutcome.IllegalTransition, result.Outcome);
        Assert.Equal("2|3", await LifecycleStateRevisionAsync(fixture));
    }

    [Fact]
    public async Task CompleteAcceptsCanonicalUnsupportedEnumerationAsSafelyTerminal()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        await BootstrapAsync(stores, scan);
        await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(
            Initial(scan, 7, Unsupported,
                new StageOutcome(ScanStageState.NotApplicable, ScanReason.UnsupportedExtension)),
            TestContext.Current.CancellationToken);
        await MoveToExactAsync(stores, scan);

        var result = await CompleteAsync(stores, scan, expectedRevision: 3);

        Assert.Equal(ScanCheckpointWriteOutcome.Accepted, result.Outcome);
        Assert.Equal(ScanLifecycleState.Complete, result.Snapshot!.Lifecycle!.State);
        Assert.Equal(new ScanAccounting(1, 0, 1, 0, 0, 0), result.Snapshot.Accounting);
    }

    [Fact]
    public async Task CompleteRejectsContradictorySiblingPrerequisiteAtomically()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using (var stores = new SqlitePhotoSiftStores(fixture.DatabasePath))
        {
            await BootstrapAsync(stores, scan);
            await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(
                Initial(scan, 7, Succeeded, Running), TestContext.Current.CancellationToken);
            await MoveToExactAsync(stores, scan);
        }
        await MutateAsync(fixture, """
            UPDATE scan_stage_checkpoints
            SET state=7, reason=9, prerequisite_state=4, prerequisite_reason=9
            WHERE stage=1;
            UPDATE scan_accounting
            SET discovered=1, analyzed=0, unsupported=0, failed=1, canceled=0, outstanding=0;
            """);
        await using var reopened = new SqlitePhotoSiftStores(fixture.DatabasePath);

        var result = await CompleteAsync(reopened, scan, expectedRevision: 3);

        Assert.Equal(ScanCheckpointWriteOutcome.IllegalTransition, result.Outcome);
        Assert.Equal("2|3", await LifecycleStateRevisionAsync(fixture));
    }

    [Fact]
    public async Task CompleteRejectsMismatchedAccountingWithoutRepairingIt()
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using (var stores = new SqlitePhotoSiftStores(fixture.DatabasePath))
        {
            await BootstrapAsync(stores, scan);
            await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(
                Initial(scan, 7, Succeeded, Running), TestContext.Current.CancellationToken);
            await stores.ScanCheckpoints.TransitionStageAsync(new(
                scan.Pin.ScanId, 7, ScanStage.Exact, 1, Transition(301), Succeeded),
                TestContext.Current.CancellationToken);
            await MoveToExactAsync(stores, scan);
        }
        await MutateAsync(fixture, """
            UPDATE scan_accounting
            SET discovered=0, analyzed=0, unsupported=0, failed=0, canceled=0, outstanding=0;
            """);
        await using var reopened = new SqlitePhotoSiftStores(fixture.DatabasePath);

        var result = await CompleteAsync(reopened, scan, expectedRevision: 3);

        Assert.Equal(ScanCheckpointWriteOutcome.IllegalTransition, result.Outcome);
        Assert.Equal("2|3", await LifecycleStateRevisionAsync(fixture));
        Assert.Equal("0|0|0|0|0|0", await AccountingFingerprintAsync(fixture));
    }

    public static TheoryData<ScanStageState, ScanReason, long, long, long, long>
        TerminalExactCompletionCases => new()
        {
            { ScanStageState.Succeeded, ScanReason.None, 1, 0, 0, 0 },
            { ScanStageState.Unsupported, ScanReason.CodecUnavailable, 0, 1, 0, 0 },
            { ScanStageState.FailedRetryable, ScanReason.ReadFailure, 0, 0, 1, 0 },
            { ScanStageState.FailedTerminal, ScanReason.UnknownSafety, 0, 0, 1, 0 },
            { ScanStageState.Canceled, ScanReason.Canceled, 0, 0, 0, 1 },
        };

    [Theory]
    [MemberData(nameof(TerminalExactCompletionCases))]
    public async Task CompleteAcceptsSuccessfulEnumerationAndAnyTerminalExactWithMatchingAccounting(
        ScanStageState exactState,
        ScanReason exactReason,
        long analyzed,
        long unsupported,
        long failed,
        long canceled)
    {
        await using var fixture = new ScanIdentityFixture();
        var scan = ScanIdentityData.Scan();
        await using var stores = new SqlitePhotoSiftStores(fixture.DatabasePath);
        await BootstrapAsync(stores, scan);
        await stores.ScanCheckpoints.WriteInitialOccurrenceAsync(
            Initial(scan, 7, Succeeded, Running), TestContext.Current.CancellationToken);
        var exact = new StageOutcome(exactState, exactReason);
        await stores.ScanCheckpoints.TransitionStageAsync(new(
            scan.Pin.ScanId, 7, ScanStage.Exact, 1, Transition(302), exact),
            TestContext.Current.CancellationToken);
        await MoveToExactAsync(stores, scan);

        var result = await CompleteAsync(stores, scan, expectedRevision: 3);

        Assert.Equal(ScanCheckpointWriteOutcome.Accepted, result.Outcome);
        Assert.Equal(ScanLifecycleState.Complete, result.Snapshot!.Lifecycle!.State);
        Assert.Equal(new ScanAccounting(1, analyzed, unsupported, failed, canceled, 0),
            result.Snapshot.Accounting);
    }

    private static async Task BootstrapAsync(SqlitePhotoSiftStores stores, ScanIdentityRecord scan)
    {
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(ScanIdentityPersistOutcome.Persisted,
            (await stores.ScanIdentities.PersistScanAsync(scan, token)).Outcome);
        Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
            (await stores.ScanCheckpoints.TransitionLifecycleAsync(new(
                scan.Pin.ScanId, 1, Transition(1), ScanLifecycleState.Preflight), token)).Outcome);
    }

    private static async Task BootstrapToExactAsync(SqlitePhotoSiftStores stores, ScanIdentityRecord scan)
    {
        await BootstrapAsync(stores, scan);
        await MoveToExactAsync(stores, scan);
    }

    private static async Task MoveToExactAsync(SqlitePhotoSiftStores stores, ScanIdentityRecord scan)
    {
        var token = TestContext.Current.CancellationToken;
        Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
            (await stores.ScanCheckpoints.TransitionLifecycleAsync(new(
                scan.Pin.ScanId, 1, Transition(2), ScanLifecycleState.Enumerating), token)).Outcome);
        Assert.Equal(ScanCheckpointWriteOutcome.Accepted,
            (await stores.ScanCheckpoints.TransitionLifecycleAsync(new(
                scan.Pin.ScanId, 2, Transition(3), ScanLifecycleState.Exact), token)).Outcome);
    }

    private static ValueTask<ScanCheckpointWriteResult> CompleteAsync(
        SqlitePhotoSiftStores stores,
        ScanIdentityRecord scan,
        long expectedRevision) => stores.ScanCheckpoints.TransitionLifecycleAsync(new(
            scan.Pin.ScanId, expectedRevision, Transition(4), ScanLifecycleState.Complete),
            TestContext.Current.CancellationToken);

    private static InitialOccurrenceCheckpointCommand Initial(
        ScanIdentityRecord scan,
        long occurrenceId,
        StageOutcome enumeration,
        StageOutcome exact)
    {
        var occurrence = ScanIdentityData.Occurrence(scan.Pin, occurrenceId);
        return new(
            occurrence,
            new ScanCheckpoint(scan.Pin.ScanId, occurrenceId, ScanStage.Enumeration, 1,
                Transition(100 + occurrenceId), enumeration, null),
            new ScanCheckpoint(scan.Pin.ScanId, occurrenceId, ScanStage.Exact, 1,
                Transition(100 + occurrenceId), exact,
                new ScanStagePrerequisite(ScanStage.Enumeration, enumeration)));
    }

    private static Guid Transition(long suffix) =>
        Guid.Parse($"00000000-0000-4000-8000-{suffix:000000000000}");

    private static async Task MutateAsync(ScanIdentityFixture fixture, string sql)
    {
        await using var raw = await fixture.OpenRawAsync(SqliteOpenMode.ReadWrite);
        await ExecuteAsync(raw, sql);
        SqliteConnection.ClearAllPools();
    }

    private static async Task<string> AuthorityFingerprintAsync(ScanIdentityFixture fixture)
    {
        await using var raw = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        return Assert.IsType<string>(await ScalarAsync(raw, """
            SELECT
                (SELECT group_concat(value, ';') FROM (
                    SELECT scan_id || '|' || scope_revision || '|' || rule_set_revision
                           || '|' || analysis_revision || '|' || selected_path || '|' || recursion
                           || '|' || resolved_path || '|' || root_volume_serial
                           || '|' || root_file_id_high || '|' || root_file_id_low || '|' || created_utc AS value
                    FROM scans ORDER BY scan_id)) || '#' ||
                (SELECT group_concat(value, ';') FROM (
                    SELECT scan_id || '|' || revision || '|' || transition_id || '|' || state AS value
                    FROM scan_lifecycle ORDER BY scan_id)) || '#' ||
                (SELECT group_concat(value, ';') FROM (
                    SELECT scan_id || '|' || occurrence_id || '|' || occurrence_revision || '|' || attempt
                           || '|' || discovered_path || '|' || resolved_path
                           || '|' || ifnull(volume_serial, 'null')
                           || '|' || ifnull(file_id_high, 'null')
                           || '|' || ifnull(file_id_low, 'null') AS value
                    FROM scan_file_occurrences ORDER BY scan_id, occurrence_id)) || '#' ||
                (SELECT group_concat(value, ';') FROM (
                    SELECT scan_id || '|' || occurrence_id || '|' || stage || '|' || revision
                           || '|' || transition_id || '|' || state || '|' || reason
                           || '|' || ifnull(prerequisite_stage, 'null')
                           || '|' || ifnull(prerequisite_state, 'null')
                           || '|' || ifnull(prerequisite_reason, 'null') AS value
                    FROM scan_stage_checkpoints ORDER BY scan_id, occurrence_id, stage));
            """));
    }

    private static async Task<string> WholeCheckpointFingerprintAsync(ScanIdentityFixture fixture) =>
        (await AuthorityFingerprintAsync(fixture)) + "#" + (await AccountingFingerprintAsync(fixture));

    private static async Task<string> AccountingFingerprintAsync(ScanIdentityFixture fixture)
    {
        await using var raw = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        return Assert.IsType<string>(await ScalarAsync(raw, """
            SELECT discovered || '|' || analyzed || '|' || unsupported || '|'
                   || failed || '|' || canceled || '|' || outstanding
            FROM scan_accounting ORDER BY scan_id;
            """));
    }

    private static async Task<string> LifecycleStateRevisionAsync(ScanIdentityFixture fixture)
    {
        await using var raw = await fixture.OpenRawAsync(SqliteOpenMode.ReadOnly);
        return Assert.IsType<string>(await ScalarAsync(raw,
            "SELECT state || '|' || revision FROM scan_lifecycle ORDER BY scan_id;"));
    }
}
