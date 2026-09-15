using PhotoSift.Application.Action;
using PhotoSift.Application.LocalData;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Infrastructure.Sqlite.Action;
using PhotoSift.Infrastructure.Sqlite.Review;
using PhotoSift.Infrastructure.Sqlite.Scan;

namespace PhotoSift.Infrastructure.Sqlite;

public sealed class SqlitePhotoSiftStores : IAsyncDisposable
{
    private readonly SqliteDatabaseOwner owner;
    public SqlitePhotoSiftStores(string databasePath) : this(databasePath, null) { }
    internal SqlitePhotoSiftStores(string databasePath, SqliteDatabaseObserver? observer)
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
