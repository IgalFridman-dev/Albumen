using System.Collections.Immutable;
using Albumen.Domain.Scan;
using Albumen.Domain.Scope;

namespace Albumen.Application.Scan;

public sealed record ScanIdentityRecord
{
    private ScanPin pin = null!;
    private ScopeSelection selection = null!;
    private string resolvedPath = null!;
    private DirectoryIdentity rootIdentity = null!;
    private DateTimeOffset createdUtc;

    public ScanIdentityRecord(ScanPin pin, ScopeSelection selection, string resolvedPath,
        DirectoryIdentity rootIdentity, DateTimeOffset createdUtc)
    {
        Pin = pin;
        Selection = selection;
        ResolvedPath = resolvedPath;
        RootIdentity = rootIdentity;
        CreatedUtc = createdUtc;
    }

    public ScanPin Pin
    {
        get => pin;
        init
        {
            ValidatePin(value);
            pin = value;
        }
    }

    public ScopeSelection Selection
    {
        get => selection;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Enum.IsDefined(value.Recursion))
                throw new ArgumentOutOfRangeException(nameof(value), value.Recursion,
                    "Scope recursion must be defined.");
            selection = value;
        }
    }

    public string ResolvedPath
    {
        get => resolvedPath;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            resolvedPath = value;
        }
    }

    public DirectoryIdentity RootIdentity
    {
        get => rootIdentity;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            rootIdentity = value;
        }
    }

    public DateTimeOffset CreatedUtc
    {
        get => createdUtc;
        init
        {
            if (value.Offset != TimeSpan.Zero)
                throw new ArgumentException("Scan creation time must be UTC.", nameof(value));
            createdUtc = value;
        }
    }

    internal static void ValidatePin(ScanPin value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.ScanId == Guid.Empty)
            throw new ArgumentException("Scan ID must be nonempty.", nameof(value));
        ValidatePositive(value.ScopeRevision, nameof(value.ScopeRevision));
        ValidatePositive(value.RuleSetRevision, nameof(value.RuleSetRevision));
        ValidatePositive(value.AnalysisRevision, nameof(value.AnalysisRevision));
    }

    internal static void ValidatePositive(long value, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, value, "Identity revisions and attempts must be positive.");
    }
}

public sealed record ScanOccurrenceIdentityRecord
{
    private OccurrenceToken token = null!;
    private string discoveredPath = null!;
    private string resolvedPath = null!;

    public ScanOccurrenceIdentityRecord(OccurrenceToken token, string discoveredPath,
        string resolvedPath, PhysicalFileKey? physicalFileKey)
    {
        Token = token;
        DiscoveredPath = discoveredPath;
        ResolvedPath = resolvedPath;
        PhysicalFileKey = physicalFileKey;
    }

    public OccurrenceToken Token
    {
        get => token;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            ScanIdentityRecord.ValidatePin(value.Pin);
            ScanIdentityRecord.ValidatePositive(value.OccurrenceId, nameof(value.OccurrenceId));
            ScanIdentityRecord.ValidatePositive(value.OccurrenceRevision, nameof(value.OccurrenceRevision));
            ScanIdentityRecord.ValidatePositive(value.Attempt, nameof(value.Attempt));
            token = value;
        }
    }

    public string DiscoveredPath
    {
        get => discoveredPath;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            discoveredPath = value;
        }
    }

    public string ResolvedPath
    {
        get => resolvedPath;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            resolvedPath = value;
        }
    }

    // Nullable as one atomic value so a partially known physical identity cannot be represented.
    public PhysicalFileKey? PhysicalFileKey { get; init; }
}

public enum ScanIdentityPersistOutcome
{
    Persisted,
    ExactRetry,
    Conflict,
    MissingScan,
    Unavailable,
}

public sealed record ScanIdentityPersistResult
{
    public ScanIdentityPersistResult(ScanIdentityPersistOutcome outcome)
    {
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        Outcome = outcome;
    }

    public ScanIdentityPersistOutcome Outcome { get; }
}

public enum ScanIdentityReadOutcome
{
    Found,
    Missing,
    Corrupt,
    Unavailable,
}

public sealed record ScanIdentityReadResult
{
    public ScanIdentityReadResult(ScanIdentityReadOutcome outcome, ScanIdentityRecord? record)
    {
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        if ((outcome == ScanIdentityReadOutcome.Found) != (record is not null))
            throw new ArgumentException("A Scan record is present exactly when the read outcome is Found.", nameof(record));
        Outcome = outcome;
        Record = record;
    }

    public ScanIdentityReadOutcome Outcome { get; }
    public ScanIdentityRecord? Record { get; }
}

public sealed record ScanOccurrenceIdentityReadResult
{
    public ScanOccurrenceIdentityReadResult(ScanIdentityReadOutcome outcome,
        ImmutableArray<ScanOccurrenceIdentityRecord> records)
    {
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        if (records.IsDefault) throw new ArgumentException("Occurrence reads must be initialized.", nameof(records));
        if (outcome != ScanIdentityReadOutcome.Found && !records.IsEmpty)
            throw new ArgumentException("Non-found reads cannot expose occurrence records.", nameof(records));
        Outcome = outcome;
        Records = records;
    }

    public ScanIdentityReadOutcome Outcome { get; }
    public ImmutableArray<ScanOccurrenceIdentityRecord> Records { get; }
}

public interface IScanIdentityStore
{
    ValueTask<ScanIdentityPersistResult> PersistScanAsync(
        ScanIdentityRecord record, CancellationToken cancellationToken);
    ValueTask<ScanIdentityPersistResult> PersistOccurrenceAsync(
        ScanOccurrenceIdentityRecord record, CancellationToken cancellationToken);
    ValueTask<ScanIdentityReadResult> ReadScanAsync(Guid scanId, CancellationToken cancellationToken);
    ValueTask<ScanOccurrenceIdentityReadResult> ReadOccurrencesAsync(
        Guid scanId, CancellationToken cancellationToken);
}
