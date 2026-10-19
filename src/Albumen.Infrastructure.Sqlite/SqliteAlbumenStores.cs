using Albumen.Application.Action;
using Albumen.Application.LocalData;
using Albumen.Application.Review;
using Albumen.Application.Scan;
using Albumen.Infrastructure.Sqlite.Action;
using Albumen.Infrastructure.Sqlite.Review;
using Albumen.Infrastructure.Sqlite.Scan;

namespace Albumen.Infrastructure.Sqlite;

public sealed class SqliteAlbumenStores : IAsyncDisposable
{
    private readonly SqliteDatabaseOwner owner;
    public SqliteAlbumenStores(string databasePath) : this(databasePath, null) { }
    internal SqliteAlbumenStores(string databasePath, SqliteDatabaseObserver? observer)
    {
        owner = new(databasePath, observer);
        OverrideEvidence = new SqliteOverrideEvidenceStore(owner);
        ActionJournal = new SqliteActionJournalStore(owner);
        ActionRecords = new SqliteActionRecordStore(owner);
        ScanIdentities = new SqliteScanIdentityStore(owner);
        ScanCheckpoints = new SqliteScanCheckpointStore(owner);
        ScanData = new SqliteScanDataControl(owner);
    }
    public IOverrideEvidenceStore OverrideEvidence { get; }
    public IActionJournalStore ActionJournal { get; }
    public IActionRecordStore ActionRecords { get; }
    public IScanIdentityStore ScanIdentities { get; }
    public IScanCheckpointStore ScanCheckpoints { get; }
    public ILocalScanDataControl ScanData { get; }
    public ValueTask DisposeAsync() => owner.DisposeAsync();
}
