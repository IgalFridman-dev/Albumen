using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using PhotoSift.Application.Shell;
using ScanSessionStatus = PhotoSift.Application.Scan.ScanStatus;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.App.Shell;

public sealed class ShellViewModel : INotifyPropertyChanged
{
    private readonly IChooseScopeWorkflow chooseScopeWorkflow;
    private readonly IPhotoSiftWorkflow? photoWorkflow;
    private PhotoSiftSnapshot? photoSnapshot;
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
        ChooseScopeCommand = new AsyncCommand(
            () => this.chooseScopeWorkflow.HandleAsync(
                new ChooseScopeCommand(), CancellationToken.None),
            allowConcurrentExecutions: true);
        RetryScopeCommand = new AsyncCommand(
            () => this.chooseScopeWorkflow.HandleAsync(
                new RetryScopePreflightCommand(), CancellationToken.None),
            allowConcurrentExecutions: true);
        ChangeRecursionCommand = new AsyncCommand(
            parameter => this.chooseScopeWorkflow.HandleAsync(
                new ChangeScopeRecursionCommand(RequireBoolean(parameter)), CancellationToken.None),
            allowConcurrentExecutions: true);
        ContinueCommand = new AsyncCommand(() => photoWorkflow?.ContinueAsync() ?? Task.CompletedTask);
        ConfirmExactRulesCommand = new AsyncCommand(() => photoWorkflow?.ConfirmExactRulesAsync() ?? Task.CompletedTask);
        StartScanCommand = new AsyncCommand(() => photoWorkflow?.StartScanAsync() ?? Task.CompletedTask);
        CancelScanCommand = new AsyncCommand(() => photoWorkflow?.CancelScanAsync() ?? Task.CompletedTask);
        OpenReviewCommand = new AsyncCommand(() => photoWorkflow?.NavigateAsync(WorkflowPhase.Review) ?? Task.CompletedTask);
        NavigateCommand = new AsyncCommand(parameter => photoWorkflow is not null && parameter is WorkflowPhase phase
            ? photoWorkflow.NavigateAsync(phase) : parameter is WorkflowPhase.ChooseScope
                ? this.chooseScopeWorkflow.HandleAsync(new ChooseScopeCommand(), CancellationToken.None) : Task.CompletedTask);
        this.chooseScopeWorkflow.SnapshotChanged += OnSnapshotChanged;
        Apply(this.chooseScopeWorkflow.Current);
    }

    public ShellViewModel(IChooseScopeWorkflow scope, IPhotoSiftWorkflow workflow) : this(scope)
    {
        photoWorkflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        workflow.SnapshotChanged += (_, snapshot) => ApplyCombined(snapshot);
        ApplyCombined(workflow.Current);
    }

    public AsyncCommand ContinueCommand { get; }
    public AsyncCommand ConfirmExactRulesCommand { get; }
    public AsyncCommand StartScanCommand { get; }
    public AsyncCommand CancelScanCommand { get; }
    public AsyncCommand OpenReviewCommand { get; }
    public AsyncCommand NavigateCommand { get; }
    public WorkflowPhase CurrentPhase => photoSnapshot?.CurrentPhase ?? WorkflowPhase.ChooseScope;
    public bool IsChooseScopePhase => CurrentPhase == WorkflowPhase.ChooseScope;
    public bool IsConfirmRulesPhase => CurrentPhase == WorkflowPhase.ConfirmRules;
    public bool IsScanPhase => CurrentPhase == WorkflowPhase.Scan;
    public bool IsReviewPhase => CurrentPhase == WorkflowPhase.Review;
    public bool CanContinue => photoSnapshot?.Scope.ScopeState == ScopeSessionState.Ready;
    public bool CanConfirmExactRules => CanContinue && IsConfirmRulesPhase && photoSnapshot?.Rules is null;
    public bool CanStartScan => photoSnapshot is { } snapshot && ShellPhasePolicy.CanStart(snapshot.Scope, snapshot.Rules) && !IsScanBusy;
    public bool IsScanBusy => photoSnapshot?.Scan.Status is ScanSessionStatus.Checking or ScanSessionStatus.Running;
    public bool CanOpenReview => photoSnapshot?.Scan.Pin is not null;
    public string ExactPreset { get; } = "Exact duplicates only. Files must contain identical bytes. Rules are fixed for this scan.";
    public string RecursionSummary => IncludeSubfolders ? "Include subfolders" : "Top-level folder only";
    public string ScanStatus => photoSnapshot is { } snapshot
        ? $"{snapshot.Scan.Status}; Enumeration: {snapshot.Scan.Enumeration.State}; Exact: {(snapshot.Scan.Counts.Outstanding > 0 ? "Running" : snapshot.Scan.Status is ScanSessionStatus.Complete or ScanSessionStatus.Incomplete ? "Finished" : "Pending")}" : "Idle";
    public string ScanCountsText => photoSnapshot is { Scan.Counts: var count }
        ? $"Discovered: {count.Discovered}; Analyzed: {count.Analyzed}; Excluded: {count.Excluded}; Failed: {count.Failed}; Canceled: {count.Canceled}; Outstanding: {count.Outstanding}" : string.Empty;
    public string CoverageStatus => photoSnapshot?.Scan.Status == ScanSessionStatus.Complete
        ? "Complete scope coverage. Read failures and exclusions are reported separately."
        : "Partial results — incomplete scope coverage. Results do not represent every file in the Scope.";
    public string ReviewDisclaimer { get; } = "Analyzed revisions; scan again after files change. Results are memory-only and are not restored after restart.";
    public ImmutableArray<ExactGroupViewModel> ExactGroups { get; private set; } = [];
    public ImmutableArray<ScanOccurrenceViewModel> CouldNotAnalyze { get; private set; } = [];

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

    // The composition root schedules the workflow's ordered publications on the
    // UI Dispatcher. Workers never call the ViewModel or synchronously Send here.
    private void OnSnapshotChanged(object? sender, ShellSnapshot snapshot)
    {
        if (photoWorkflow is null) Apply(snapshot);
    }

    private void ApplyCombined(PhotoSiftSnapshot snapshot)
    {
        var previousPhase = CurrentPhase;
        var previousStatus = ScanStatus;
        var previousBusy = IsScanBusy;
        var previousCanConfirm = CanConfirmExactRules;
        Apply(snapshot.Scope);
        photoSnapshot = snapshot;
        Phases = Project(snapshot.Phases);
        RuleSetStatus = snapshot.Rules is null ? "Not configured" : "Exact duplicates only — confirmed";
        ExactGroups = snapshot.Scan.Groups.Select(group => new ExactGroupViewModel(group)).ToImmutableArray();
        CouldNotAnalyze = snapshot.Scan.Occurrences
            .Where(row => row.Exact.State is ScanStageState.FailedRetryable or ScanStageState.FailedTerminal or ScanStageState.Canceled)
            .Select(row => new ScanOccurrenceViewModel(row)).ToImmutableArray();
        foreach (var name in new[] { nameof(IsChooseScopePhase), nameof(IsConfirmRulesPhase), nameof(IsScanPhase), nameof(IsReviewPhase),
            nameof(CanContinue), nameof(CanStartScan), nameof(CanOpenReview),
            nameof(RecursionSummary), nameof(ScanCountsText), nameof(CoverageStatus), nameof(ExactGroups), nameof(CouldNotAnalyze) })
            OnPropertyChanged(name);
        if (previousBusy != IsScanBusy) OnPropertyChanged(nameof(IsScanBusy));
        if (previousCanConfirm != CanConfirmExactRules) OnPropertyChanged(nameof(CanConfirmExactRules));
        if (previousStatus != ScanStatus) OnPropertyChanged(nameof(ScanStatus));
        if (previousPhase != CurrentPhase) OnPropertyChanged(nameof(CurrentPhase));
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





