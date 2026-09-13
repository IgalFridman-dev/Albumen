using PhotoSift.Application.Action;
using PhotoSift.Application.Review;
using PhotoSift.Infrastructure.Sqlite.Action;
using PhotoSift.Infrastructure.Sqlite.Review;

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
    }
    public IOverrideEvidenceStore OverrideEvidence { get; }
    public IActionJournalStore ActionJournal { get; }
    public IActionRecordStore ActionRecords { get; }
    public ValueTask DisposeAsync() => owner.DisposeAsync();
}
