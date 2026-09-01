using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using PhotoSift.Application.Shell;
using PhotoSift.Domain.Scope;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.App.Shell;

public sealed class ShellViewModel : INotifyPropertyChanged
{
    private readonly IChooseScopeWorkflow chooseScopeWorkflow;
    private readonly SynchronizationContext? synchronizationContext;
    private readonly object scopeOperationGate = new();
    private ScopeOperationCancellation? currentScopeOperation;
    private ShellSnapshot? currentSnapshot;
    private ImmutableArray<PhaseItemViewModel> phases = [];
    private ImmutableArray<ScopeSummaryItemViewModel> scopeSummary = [];
    private ImmutableArray<string> blockingReasons = [];
    private string blockingNoticeAutomationName = "Scope blocked";
    private string instruction = string.Empty;
    private string scopeStatus = string.Empty;
    private string selectedPath = string.Empty;
    private string resolvedPath = string.Empty;
    private string supportedCount = string.Empty;
    private string ruleSetStatus = string.Empty;
    private bool includeSubfolders;
    private bool canChangeRecursion;
    private bool isChecking;
    private bool isReady;
    private bool isBlocked;
    private bool isCanceled;

    public ShellViewModel(IChooseScopeWorkflow chooseScopeWorkflow)
    {
        this.chooseScopeWorkflow = chooseScopeWorkflow
            ?? throw new ArgumentNullException(nameof(chooseScopeWorkflow));
        synchronizationContext = SynchronizationContext.Current;
        ChooseScopeCommand = new AsyncCommand(
            () => ReplaceScopeOperationAsync(
                cancellationToken => this.chooseScopeWorkflow.HandleAsync(
                    new ChooseScopeCommand(),
                    cancellationToken)),
            allowConcurrentExecutions: true);
        RetryScopeCommand = new AsyncCommand(
            () => ReplaceScopeOperationAsync(
                cancellationToken => this.chooseScopeWorkflow.HandleAsync(
                    new RetryScopePreflightCommand(),
                    cancellationToken)),
            allowConcurrentExecutions: true);
        ChangeRecursionCommand = new AsyncCommand(
            parameter => ReplaceScopeOperationAsync(
                cancellationToken => this.chooseScopeWorkflow.HandleAsync(
                    new ChangeScopeRecursionCommand(RequireBoolean(parameter)),
                    cancellationToken),
                isNeutral: () => IsSameValueRecursion(parameter)),
            allowConcurrentExecutions: true);
        this.chooseScopeWorkflow.SnapshotChanged += OnSnapshotChanged;
        Apply(this.chooseScopeWorkflow.Current);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImmutableArray<PhaseItemViewModel> Phases
    {
        get => phases;
        private set => SetField(ref phases, value);
    }

    public string Instruction
    {
        get => instruction;
        private set => SetField(ref instruction, value);
    }

    public string ScopeStatus
    {
        get => scopeStatus;
        private set => SetField(ref scopeStatus, value);
    }

    public string SelectedPath
    {
        get => selectedPath;
        private set => SetField(ref selectedPath, value);
    }

    public string ResolvedPath
    {
        get => resolvedPath;
        private set => SetField(ref resolvedPath, value);
    }

    public string SupportedCount
    {
        get => supportedCount;
        private set => SetField(ref supportedCount, value);
    }

    public string RuleSetStatus
    {
        get => ruleSetStatus;
        private set => SetField(ref ruleSetStatus, value);
    }

    public ImmutableArray<ScopeSummaryItemViewModel> ScopeSummary
    {
        get => scopeSummary;
        private set => SetField(ref scopeSummary, value);
    }

    public ImmutableArray<string> BlockingReasons
    {
        get => blockingReasons;
        private set => SetField(ref blockingReasons, value);
    }

    public string BlockingNoticeAutomationName
    {
        get => blockingNoticeAutomationName;
        private set => SetField(ref blockingNoticeAutomationName, value);
    }

    public bool IncludeSubfolders
    {
        get => includeSubfolders;
        private set => SetField(ref includeSubfolders, value);
    }

    public bool CanChangeRecursion
    {
        get => canChangeRecursion;
        private set => SetField(ref canChangeRecursion, value);
    }

    public bool IsChecking
    {
        get => isChecking;
        private set => SetField(ref isChecking, value);
    }

    public bool IsReady
    {
        get => isReady;
        private set => SetField(ref isReady, value);
    }

    public bool IsBlocked
    {
        get => isBlocked;
        private set => SetField(ref isBlocked, value);
    }

    public bool IsCanceled
    {
        get => isCanceled;
        private set => SetField(ref isCanceled, value);
    }

    public AsyncCommand ChooseScopeCommand { get; }

    public AsyncCommand RetryScopeCommand { get; }

    public AsyncCommand ChangeRecursionCommand { get; }

    internal ShellSnapshot CurrentSnapshot => currentSnapshot
        ?? throw new InvalidOperationException("The shell has no current snapshot.");

    private void OnSnapshotChanged(object? sender, ShellSnapshot snapshot)
    {
        if (synchronizationContext is null ||
            ReferenceEquals(SynchronizationContext.Current, synchronizationContext))
        {
            Apply(snapshot);
            return;
        }

        synchronizationContext.Send(_ => Apply(snapshot), state: null);
    }

    private void Apply(ShellSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Phases = Project(snapshot.Phases);
        Instruction = snapshot.Instruction;
        ScopeStatus = snapshot.ScopeState switch
        {
            ScopeSessionState.Cold => "Choose one local folder",
            ScopeSessionState.Checking => "Checking location and access",
            ScopeSessionState.Ready => "Ready",
            ScopeSessionState.Blocked => "Blocked",
            ScopeSessionState.Canceled => "Not verified",
            _ => throw new InvalidOperationException(
                $"Unknown Scope state: {snapshot.ScopeState}"),
        };
        SelectedPath = snapshot.Selection?.SelectedPath ?? string.Empty;
        ResolvedPath = snapshot.Preflight?.Scope?.ResolvedPath ?? string.Empty;
        IncludeSubfolders = ProjectRecursion(snapshot.Selection);
        CanChangeRecursion = snapshot.Selection is not null;
        SupportedCount = FormatCount(snapshot.Preflight?.CompleteCounts?.Supported);
        RuleSetStatus = snapshot.RuleSetStatus;
        ScopeSummary = ProjectSummary(snapshot.Preflight);
        BlockingReasons = ProjectBlockingReasons(snapshot.Preflight);
        BlockingNoticeAutomationName = BlockingReasons.IsDefaultOrEmpty
            ? "Scope blocked"
            : BlockingReasons[0];
        IsChecking = snapshot.ScopeState is ScopeSessionState.Checking;
        IsReady = snapshot.ScopeState is ScopeSessionState.Ready;
        IsBlocked = snapshot.ScopeState is ScopeSessionState.Blocked;
        IsCanceled = snapshot.ScopeState is ScopeSessionState.Canceled;
        currentSnapshot = snapshot;
    }

    private static bool ProjectRecursion(ScopeSelection? selection)
    {
        if (selection is null)
        {
            return false;
        }

        return selection.Recursion switch
        {
            ScopeRecursion.TopLevelOnly => false,
            ScopeRecursion.IncludeSubfolders => true,
            _ => throw new InvalidOperationException(
                $"Unknown Scope recursion: {selection.Recursion}"),
        };
    }

    private static ImmutableArray<ScopeSummaryItemViewModel> ProjectSummary(
        ScopePreflightResult? preflight)
    {
        var counts = preflight?.CompleteCounts;
        if (counts is null || preflight is null)
        {
            return [];
        }

        return
        [
            new("Local drive type", ProjectVolume(preflight.Volume)),
            new("Supported images", FormatCount(counts.Supported)),
            new("Unsupported extensions", FormatCount(counts.UnsupportedExtension)),
            new("Unreadable images", FormatCount(counts.Unreadable)),
            new("Codec unavailable", FormatCount(counts.CodecUnavailable)),
            new(
                "Escaping directory links skipped",
                FormatCount(preflight.Restrictions.EscapingReparseTargets)),
            new(
                "Duplicate directories skipped",
                FormatCount(preflight.Restrictions.DuplicateDirectoryIdentities)),
        ];
    }

    private static string ProjectVolume(ScopeVolumeKind volume) =>
        volume switch
        {
            ScopeVolumeKind.Fixed => "Fixed",
            ScopeVolumeKind.Removable => "Removable",
            _ => throw new InvalidOperationException(
                $"Unknown Scope volume: {volume}"),
        };

    private static ImmutableArray<string> ProjectBlockingReasons(
        ScopePreflightResult? preflight) =>
        preflight?.BlockingReasons.Select(ProjectBlockingReason).ToImmutableArray()
            ?? [];

    private static string ProjectBlockingReason(ScopeBlockingReason reason) =>
        reason switch
        {
            ScopeBlockingReason.UnavailableRoot =>
                "The selected folder is unavailable.",
            ScopeBlockingReason.NonLocalRoot =>
                "Choose a folder on a local drive.",
            ScopeBlockingReason.UnsupportedRoot =>
                "The selected drive type is not supported.",
            ScopeBlockingReason.SynchronizedLocation =>
                "Synchronized folders are not supported.",
            ScopeBlockingReason.PlaceholderLocation =>
                "Placeholder-backed folders are not supported.",
            ScopeBlockingReason.RootAccessDenied =>
                "PhotoSift cannot read the selected folder.",
            ScopeBlockingReason.DescendantAccessDenied =>
                "PhotoSift cannot read every included folder.",
            ScopeBlockingReason.UnknownSafety =>
                "PhotoSift could not verify this folder safely.",
            _ => throw new InvalidOperationException(
                $"Unknown Scope blocking reason: {reason}"),
        };

    private static string FormatCount(int? count) =>
        count?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable";

    private static bool RequireBoolean(object? parameter) =>
        parameter is bool value
            ? value
            : throw new ArgumentException(
                "Recursion command requires a Boolean parameter.",
                nameof(parameter));

    private bool IsSameValueRecursion(object? parameter)
    {
        if (parameter is not bool includeSubfolders)
        {
            return false;
        }

        var selection = chooseScopeWorkflow.Current.Selection;
        return selection is not null &&
            ProjectRecursion(selection) == includeSubfolders;
    }

    private async Task ReplaceScopeOperationAsync(
        Func<CancellationToken, Task> operation,
        Func<bool>? isNeutral = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using var owner = new ScopeOperationCancellation();
        try
        {
            ScopeOperationCancellation? preceding;
            lock (scopeOperationGate)
            {
                if (isNeutral?.Invoke() is true)
                {
                    return;
                }

                preceding = currentScopeOperation;
                currentScopeOperation = owner;
            }

            preceding?.Cancel();

            Task execution;
            lock (scopeOperationGate)
            {
                if (!ReferenceEquals(currentScopeOperation, owner))
                {
                    return;
                }

                execution = operation(owner.Token);
            }

            await execution;
        }
        finally
        {
            lock (scopeOperationGate)
            {
                if (ReferenceEquals(currentScopeOperation, owner))
                {
                    currentScopeOperation = null;
                }
            }
        }
    }

    private sealed class ScopeOperationCancellation : IDisposable
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource source = new();
        private bool cancellationInProgress;
        private bool cancellationRequested;
        private bool disposeRequested;
        private bool disposed;

        public CancellationToken Token => source.Token;

        public void Cancel()
        {
            lock (gate)
            {
                if (disposed || cancellationRequested)
                {
                    return;
                }

                cancellationRequested = true;
                cancellationInProgress = true;
            }

            try
            {
                source.Cancel();
            }
            finally
            {
                var dispose = false;
                lock (gate)
                {
                    cancellationInProgress = false;
                    if (disposeRequested && !disposed)
                    {
                        disposed = true;
                        dispose = true;
                    }
                }

                if (dispose)
                {
                    source.Dispose();
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                if (cancellationInProgress)
                {
                    disposeRequested = true;
                    return;
                }

                disposed = true;
            }

            source.Dispose();
        }
    }

    private static ImmutableArray<PhaseItemViewModel> Project(
        ImmutableArray<WorkflowPhaseState> phases) =>
        phases
            .Select(phase => new PhaseItemViewModel(
                phase.Phase,
                phase.DisplayName,
                phase.Availability))
            .ToImmutableArray();

    private void SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
