using System.Collections.Immutable;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using Xunit;

namespace PhotoSift.Application.Tests.Scan;

public sealed class ScanIdentityContractsTests
{
    private static readonly ScanPin ValidPin = new(
        Guid.Parse("f15c9f48-2069-4ee5-9a18-fd92d885627b"), 1, 2, 3);

    [Fact]
    public void ScanRecordRejectsInvalidIdentityAndScopeFacts()
    {
        var selection = new ScopeSelection(@"C:\photos", ScopeRecursion.IncludeSubfolders);
        var root = new DirectoryIdentity(1, 2, 3);
        var utc = new DateTimeOffset(2026, 9, 14, 10, 11, 12, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => new ScanIdentityRecord(
            ValidPin with { ScanId = Guid.Empty }, selection, @"C:\photos", root, utc));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanIdentityRecord(
            ValidPin with { ScopeRevision = 0 }, selection, @"C:\photos", root, utc));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanIdentityRecord(
            ValidPin with { RuleSetRevision = 0 }, selection, @"C:\photos", root, utc));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanIdentityRecord(
            ValidPin with { AnalysisRevision = 0 }, selection, @"C:\photos", root, utc));
        Assert.Throws<ArgumentException>(() => new ScanIdentityRecord(
            ValidPin, selection, " ", root, utc));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScopeSelection(@"C:\photos", (ScopeRecursion)int.MaxValue));
        Assert.Throws<ArgumentException>(() => new ScanIdentityRecord(
            ValidPin, selection, @"C:\photos", root, utc.ToOffset(TimeSpan.FromHours(2))));
    }

    [Fact]
    public void OccurrenceRecordRejectsInvalidTokenAndPaths()
    {
        var token = new OccurrenceToken(ValidPin, 1, 2, 3);

        Assert.Throws<ArgumentException>(() => new ScanOccurrenceIdentityRecord(
            token with { Pin = ValidPin with { ScanId = Guid.Empty } }, @"C:\a.jpg", @"C:\a.jpg", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanOccurrenceIdentityRecord(
            token with { OccurrenceId = 0 }, @"C:\a.jpg", @"C:\a.jpg", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanOccurrenceIdentityRecord(
            token with { OccurrenceRevision = 0 }, @"C:\a.jpg", @"C:\a.jpg", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScanOccurrenceIdentityRecord(
            token with { Attempt = 0 }, @"C:\a.jpg", @"C:\a.jpg", null));
        Assert.Throws<ArgumentException>(() => new ScanOccurrenceIdentityRecord(
            token, "", @"C:\a.jpg", null));
        Assert.Throws<ArgumentException>(() => new ScanOccurrenceIdentityRecord(
            token, @"C:\a.jpg", " ", null));
    }

    [Fact]
    public void PortIsBusinessShapedAndOccurrenceReadsAreImmutable()
    {
        var sqliteTypes = typeof(IScanIdentityStore).GetMethods()
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)
                .Append(method.ReturnType))
            .Where(type => type.FullName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) is true)
            .ToArray();

        Assert.Empty(sqliteTypes);
        Assert.Equal(typeof(ImmutableArray<ScanOccurrenceIdentityRecord>),
            typeof(ScanOccurrenceIdentityReadResult).GetProperty(nameof(ScanOccurrenceIdentityReadResult.Records))!.PropertyType);
    }
}
