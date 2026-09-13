using System.Collections.Immutable;
using System.Reflection;
using System.Text.RegularExpressions;
using PhotoSift.Application.Action;
using PhotoSift.Application.Review;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Action;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using Xunit;

namespace PhotoSift.Infrastructure.Tests.Architecture;

public sealed class ActionBoundaryTests
{
    private static readonly ImmutableHashSet<Type> ImmutablePayloadTypes =
    [
        typeof(ApprovedActionRequest), typeof(ApprovedActionItem), typeof(ApprovalDisclosure),
        typeof(ActionSnapshot), typeof(ActionItemSnapshot), typeof(OccurrenceToken), typeof(ScanPin),
        typeof(ResolvedScope), typeof(ScopeSelection), typeof(DirectoryIdentity), typeof(PhysicalFileKey),
        typeof(FileId128), typeof(ContentRevision), typeof(AllocationEvidence), typeof(OverrideEvidenceId),
        typeof(ReviewUnresolvedSummary), typeof(SelectionRecovery),
    ];
    private static readonly ImmutableHashSet<Type> ImmutableScalars =
    [
        typeof(bool), typeof(int), typeof(long), typeof(uint), typeof(ulong), typeof(string), typeof(Guid),
        typeof(DateTimeOffset), typeof(ScanStatus), typeof(ScopeRecursion), typeof(SelectionRecoveryUnavailableReason),
        typeof(ActionItemState), typeof(ActionStatus), typeof(ActionSafetyReason),
    ];
    private static readonly string[] ActionOwnerTypeNames =
    [
        "ActionCoordinator", "ActionStore", "ActionOwnerInbox", "IActionCoordinator",
        "ActionSnapshot", "ActionItemSnapshot", "ActionCommandResult", "IActionJournalStore", "IRecycleBinExecutor",
    ];

    [Fact]
    public void ActionOwnerAndBusinessPortsBelongOnlyToApplicationWhilePolicyRemainsPureDomain()
    {
        var application = typeof(ApprovedActionRequest).Assembly;
        var coordinatorMethod = typeof(IReviewCoordinator).GetMethod("ApproveAsync");

        Assert.NotNull(coordinatorMethod);
        Assert.Equal(typeof(Task<ReviewCommandResult>), coordinatorMethod.ReturnType);
        Assert.Equal("PhotoSift.Application", application.GetName().Name);
        Assert.All(ActionOwnerTypeNames, name =>
        {
            var type = Assert.Single(application.GetTypes(), type => StringComparer.Ordinal.Equals(type.Name, name));
            Assert.Equal("PhotoSift.Application.Action", type.Namespace);
            Assert.DoesNotContain(typeof(ActionSafetyPolicy).Assembly.GetTypes(), value => value.Name == name);
        });
        Assert.True(application.GetType("PhotoSift.Application.Action.ActionStore")!.IsNotPublic);
        Assert.True(typeof(IActionCoordinator).IsAssignableFrom(typeof(ActionCoordinator)));
        Assert.Equal(typeof(Task<ActionCommandResult>), typeof(IActionCoordinator).GetMethod("ApproveAsync")!.ReturnType);
        Assert.Equal(typeof(ApprovedActionRequest), typeof(IActionCoordinator).GetMethod("ApproveAsync")!.GetParameters()[0].ParameterType);
        Assert.Equal("PhotoSift.Domain", typeof(ActionSafetyPolicy).Assembly.GetName().Name);
        Assert.DoesNotContain(application.GetReferencedAssemblies(), reference =>
            reference.Name!.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
            || reference.Name.Contains("Windows", StringComparison.OrdinalIgnoreCase)
            || reference.Name == "PresentationFramework");
    }

    [Fact]
    public void ReviewApprovalSourceContainsNoActionStateJournalOrOsMutationBoundary()
    {
        var root = FindRepositoryRoot();
        var reviewRoot = Path.Combine(root, "src", "PhotoSift.Application", "Review");
        var reviewSource = string.Join('\n', Directory.EnumerateFiles(reviewRoot, "*.cs")
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText));
        Assert.All(ActionOwnerTypeNames, forbidden => Assert.DoesNotMatch(
            $@"\b(class|record|interface|enum)\s+{Regex.Escape(forbidden)}\b", reviewSource));
        Assert.DoesNotMatch(@"\b(File|Directory)\s*\.\s*(Delete|Move|Replace)\s*\(", reviewSource);
        Assert.DoesNotMatch(@"\b(ActionStore|ActionItemSnapshot|ActionSnapshot|IActionJournalStore|IRecycleBinExecutor)\b", reviewSource);
        Assert.DoesNotContain("actual recovered", reviewSource, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DomainAndApplicationCannotOwnProviderComOrSourceMutationApis()
    {
        var root = FindRepositoryRoot();
        foreach (var project in new[] { "PhotoSift.Domain", "PhotoSift.Application" })
        {
            var source = string.Join('\n', Sources(Path.Combine(root, "src", project)).Select(File.ReadAllText));
            Assert.DoesNotMatch(@"\b(IFileOperation|IShellItem|Microsoft\.Data\.Sqlite|SqliteConnection|SqliteTransaction|DllImport|LibraryImport|ComImport|GeneratedComInterface|GeneratedComClass)\b", source);
            Assert.DoesNotMatch(@"\b(File|Directory)\s*\.\s*(Delete|Move|Replace)\s*\(", source);
            Assert.DoesNotMatch(@"\b(DeleteFile|RemoveDirectory|MoveFile|SHFileOperation|FOF_ALLOWUNDO|FOFX_EARLYFAILURE|FOFX_NORECURSION)\b", source);
        }
    }

    [Fact]
    public void NativeAndJournalImplementationsStayInTheirInfrastructureProjectsAndCannotFallback()
    {
        var root = Path.Combine(FindRepositoryRoot(), "src");
        var projects = Directory.EnumerateDirectories(root)
            .Where(directory => File.Exists(Path.Combine(directory, Path.GetFileName(directory) + ".csproj")))
            .Select(directory => Path.GetFileName(directory)!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["PhotoSift.Analysis", "PhotoSift.App", "PhotoSift.Application", "PhotoSift.Domain",
            "PhotoSift.Infrastructure.Sqlite", "PhotoSift.Infrastructure.Windows"], projects);
        // Load every production assembly explicitly: discovery must not depend
        // on which types happened to be used by earlier tests.
        foreach (var type in projects.Select(project => Assembly.Load(new AssemblyName(project)))
                     .SelectMany(assembly => assembly.GetTypes()))
            AssertJournalPlacement(type);
        foreach (var file in Sources(root))
        {
            var relative = Path.GetRelativePath(root, file);
            var source = File.ReadAllText(file);
            if (!relative.StartsWith("PhotoSift.Infrastructure.Windows" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                Assert.DoesNotMatch(@"\b(IFileOperation|IShellItem|ComImport|GeneratedComInterface|GeneratedComClass)\b", source);
            if (!relative.StartsWith("PhotoSift.Infrastructure.Sqlite" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                Assert.DoesNotMatch(@"\b(SqliteActionJournalStore|Microsoft\.Data\.Sqlite|SqliteConnection)\b", source);
            }
            if (relative.Contains(Path.DirectorySeparatorChar + "Action" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                Assert.DoesNotMatch(@"\b(File|Directory)\s*\.\s*(Delete|Move|Replace)\s*\(", source);
                Assert.DoesNotMatch(@"\b(DeleteFile|DeleteFileW|RemoveDirectory|RemoveDirectoryW|SHFileOperation|SHFileOperationW|FOF_WANTNUKEWARNING)\b", source);
                Assert.DoesNotContain("permanent-delete fallback", source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ApprovalAndActionSnapshotsExposeOnlyImmutableValuesAndSelectionIsNeverReconstructed()
    {
        foreach (var type in new[] { typeof(ApprovedActionRequest), typeof(ApprovedActionItem), typeof(ActionSnapshot), typeof(ActionItemSnapshot) })
            AssertImmutablePayload(type);
        var root = Path.Combine(FindRepositoryRoot(), "src");
        foreach (var file in Sources(root).Where(file => !file.EndsWith(Path.Combine("Review", "ReviewStore.cs"), StringComparison.Ordinal)))
            Assert.DoesNotMatch(@"\bnew\s+ApprovedActionRequest\s*\(", File.ReadAllText(file));
    }

    [Theory]
    [InlineData(typeof(MutableFieldPayload))]
    [InlineData(typeof(ArrayPayload))]
    [InlineData(typeof(ListPayload))]
    [InlineData(typeof(NestedMutablePayload))]
    public void ImmutableGuardRejectsReachableMutablePayloads(Type type) =>
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertImmutablePayload(type));

    [Theory]
    [InlineData(typeof(InterfaceSecondJournal))]
    [InlineData(typeof(RecordJournal))]
    public void JournalPlacementGuardRejectsImplementationsRegardlessOfDeclarationShape(Type type) =>
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertJournalPlacement(type));

    private static void AssertImmutablePayload(Type type)
    {
        // Admitting the root lets adversarial test-only shapes exercise the same
        // traversal. Every nested production payload must be explicitly allowed.
        var allowed = ImmutablePayloadTypes.Add(type);
        var visited = new HashSet<Type>();
        Visit(type);
        void Visit(Type value)
        {
            if (!visited.Add(value) || ImmutableScalars.Contains(value)) return;
            if (value.IsGenericType && (value.GetGenericTypeDefinition() == typeof(Nullable<>)
                || value.GetGenericTypeDefinition() == typeof(ImmutableArray<>)))
            {
                Visit(value.GetGenericArguments().Single());
                return;
            }
            Assert.Contains(value, allowed);
            Assert.True(value.IsSealed, $"Payload {value} must be sealed.");
            if (value.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(ValueType))
                Visit(baseType);
            Assert.Empty(value.GetFields(BindingFlags.Public | BindingFlags.Instance));
            foreach (var field in value.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.True(field.IsInitOnly, $"Payload field {value}.{field.Name} must be readonly.");
                Visit(field.FieldType);
            }
            foreach (var property in value.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.Empty(property.GetIndexParameters());
                if (property.SetMethod is { } setter)
                    Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
                Visit(property.PropertyType);
            }
        }
    }

    private static void AssertJournalPlacement(Type type)
    {
        if (type.IsAbstract || type.IsInterface || !typeof(IActionJournalStore).IsAssignableFrom(type)) return;
        const string owner = "PhotoSift.Infrastructure.Sqlite";
        Assert.Equal(owner, type.Assembly.GetName().Name);
        Assert.True(type.Namespace == owner || type.Namespace?.StartsWith(owner + ".", StringComparison.Ordinal) == true,
            $"Journal implementation {type} must be in the SQLite namespace.");
    }

    // Malicious fixtures live only in the test assembly, never production scans.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1051:Do not declare visible instance fields",
        Justification = "Adversarial fixture proving the architecture guard rejects public mutable fields.")]
    public sealed class MutableFieldPayload { public int Value = 1; }
    public sealed record ArrayPayload(int[] Values);
    public sealed record ListPayload(List<int> Values);
    public sealed record NestedMutablePayload(ArrayPayload Nested);
    private interface IJournalMarker;
    private sealed class InterfaceSecondJournal : IJournalMarker, IActionJournalStore
    {
        public ValueTask<ActionJournalWriteResult> PersistRequestedAsync(ActionRequestedFact fact, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ActionJournalWriteResult> PersistExecutingAsync(ActionExecutingFact fact, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ActionJournalWriteResult> PersistTerminalAsync(ActionTerminalFact fact, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed record RecordJournal : IActionJournalStore
    {
        public ValueTask<ActionJournalWriteResult> PersistRequestedAsync(ActionRequestedFact fact, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ActionJournalWriteResult> PersistExecutingAsync(ActionExecutingFact fact, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ActionJournalWriteResult> PersistTerminalAsync(ActionTerminalFact fact, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static IEnumerable<string> Sources(string root) => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
        .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PhotoSift.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the PhotoSift repository root.");
    }
}
