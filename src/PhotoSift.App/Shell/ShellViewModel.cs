using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PhotoSift.Application.Review;
using PhotoSift.Application.Shell;
using ScanSessionStatus = PhotoSift.Application.Scan.ScanStatus;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.App.Shell;

public sealed class ShellViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IChooseScopeWorkflow chooseScopeWorkflow;
    private readonly IPhotoSiftWorkflow? photoWorkflow;
    private readonly IReviewDialogService? reviewDialog;
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
    private string selectionSummary = "0 selected occurrences.";
    private string reviewAnnouncement = string.Empty;
    private string approvalSelectedOccurrenceText = "0 selected occurrences";
    private string approvalSelectedPathText = "0 selected paths";
    private string approvalSelectedPhysicalText = "0 distinct physical files";
    private string approvalRecoveryText = "Estimated recovery for this Selection: 0 bytes.";
    private string approvalCoverageText = "No terminal Scan coverage.";
    private string approvalUnresolvedText = "Unresolved total: 0";
    private string approvalUnresolvedSubtotals = string.Empty;
    private string approvalUnselectedSurvivorsText = "Ordinary unselected survivors: 0";
    private bool canApproveReview;
    private ReviewSessionPin? approvalIdentitySession;
    private long approvalIdentityRevision;
    private Guid approvalIdentityKey;
    private string approvalAutomationStatus = string.Empty;
    private readonly object lifetime = new();
    private Task? disposalTask;

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
        OpenReviewCommand = new AsyncCommand(() => photoWorkflow?.OpenReviewAsync() ?? Task.CompletedTask);
        ApproveReviewCommand = new AsyncCommand(ApproveReviewAsync);
        NavigateCommand = new AsyncCommand(parameter => photoWorkflow is not null && parameter is WorkflowPhase phase
            ? photoWorkflow.NavigateAsync(phase) : parameter is WorkflowPhase.ChooseScope
                ? this.chooseScopeWorkflow.HandleAsync(new ChooseScopeCommand(), CancellationToken.None) : Task.CompletedTask);
        this.chooseScopeWorkflow.SnapshotChanged += OnSnapshotChanged;
        Apply(this.chooseScopeWorkflow.Current);
    }

    public ShellViewModel(IChooseScopeWorkflow scope, IPhotoSiftWorkflow workflow,
        IReviewDialogService? reviewDialog = null) : this(scope)
    {
        photoWorkflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        this.reviewDialog = reviewDialog;
        workflow.SnapshotChanged += OnPhotoSnapshotChanged;
        ApplyCombined(workflow.Current);
    }

    public AsyncCommand ContinueCommand { get; }
    public AsyncCommand ConfirmExactRulesCommand { get; }
    public AsyncCommand StartScanCommand { get; }
    public AsyncCommand CancelScanCommand { get; }
    public AsyncCommand OpenReviewCommand { get; }
    public AsyncCommand ApproveReviewCommand { get; }
    public AsyncCommand NavigateCommand { get; }
    public WorkflowPhase CurrentPhase => photoSnapshot?.CurrentPhase ?? WorkflowPhase.ChooseScope;
    internal ReviewSessionPin? ReviewSession => photoSnapshot?.Review.Pin;
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

    public string SelectionSummary
    {
        get => selectionSummary;
        private set => SetField(ref selectionSummary, value);
    }

    public string ReviewAnnouncement
    {
        get => reviewAnnouncement;
        private set
        {
            reviewAnnouncement = value;
            OnPropertyChanged();
        }
    }

    public string ApprovalSelectedOccurrenceText
    {
        get => approvalSelectedOccurrenceText;
        private set => SetField(ref approvalSelectedOccurrenceText, value);
    }

    public string ApprovalSelectedPathText
    {
        get => approvalSelectedPathText;
        private set => SetField(ref approvalSelectedPathText, value);
    }

    public string ApprovalSelectedPhysicalText
    {
        get => approvalSelectedPhysicalText;
        private set => SetField(ref approvalSelectedPhysicalText, value);
    }

    public string ApprovalRecoveryText
    {
        get => approvalRecoveryText;
        private set => SetField(ref approvalRecoveryText, value);
    }

    public string ApprovalCoverageText
    {
        get => approvalCoverageText;
        private set => SetField(ref approvalCoverageText, value);
    }

    public string ApprovalUnresolvedText
    {
        get => approvalUnresolvedText;
        private set => SetField(ref approvalUnresolvedText, value);
    }

    public string ApprovalUnresolvedSubtotals
    {
        get => approvalUnresolvedSubtotals;
        private set => SetField(ref approvalUnresolvedSubtotals, value);
    }

    public string ApprovalUnselectedSurvivorsText
    {
        get => approvalUnselectedSurvivorsText;
        private set => SetField(ref approvalUnselectedSurvivorsText, value);
    }

    public string ApprovalDestinationText { get; } = "Future destination: Windows Recycle Bin.";
    public string ApprovalRevalidationText { get; } =
        "Only the listed, revalidated selected occurrences are included. E1-S5 will revalidate Scope, identity, content, and override evidence again before any Action.";
    public string ApprovalAutomationStatus
    {
        get => approvalAutomationStatus;
        private set => SetField(ref approvalAutomationStatus, value);
    }
    public bool CanApproveReview
    {
        get => canApproveReview;
        private set => SetField(ref canApproveReview, value);
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

    // The composition root schedules the workflow's ordered publications on the
    // UI Dispatcher. Workers never call the ViewModel or synchronously Send here.
    private void OnSnapshotChanged(object? sender, ShellSnapshot snapshot)
    {
        if (photoWorkflow is null) Apply(snapshot);
    }

    private void OnPhotoSnapshotChanged(object? sender, PhotoSiftSnapshot snapshot) =>
        ApplyCombined(snapshot);

    private void ApplyCombined(PhotoSiftSnapshot snapshot)
    {
        var preserveReviewProjection = ReviewProjectionUnchanged(photoSnapshot?.Review, snapshot.Review)
            && (snapshot.Review.Pin is not null || ScanProjectionUnchanged(photoSnapshot?.Scan, snapshot.Scan));
        var previousPhase = CurrentPhase;
        var previousStatus = ScanStatus;
        var previousBusy = IsScanBusy;
        var previousCanConfirm = CanConfirmExactRules;
        Apply(snapshot.Scope);
        photoSnapshot = snapshot;
        Phases = Project(snapshot.Phases);
        RuleSetStatus = snapshot.Rules is null ? "Not configured" : "Exact duplicates only — confirmed";
        if (!preserveReviewProjection)
        {
            ExactGroups = snapshot.Review.Pin is null
                ? snapshot.Scan.Groups.Select(group => new ExactGroupViewModel(group)).ToImmutableArray()
                : snapshot.Review.Groups.Select((group, index) => new ExactGroupViewModel(group,
                    snapshot.Review.SelectionRevision, index + 1, snapshot.Review.Groups.Length,
                    (kind, ids) => ChangeReviewSelectionAsync(group.Candidate.GroupId, kind, ids))).ToImmutableArray();
        }
        CouldNotAnalyze = snapshot.Scan.Occurrences
            .Where(row => row.Exact.State is ScanStageState.FailedRetryable or ScanStageState.FailedTerminal or ScanStageState.Canceled)
            .Select(row => new ScanOccurrenceViewModel(row)).ToImmutableArray();
        ProjectReview(snapshot.Review);
        foreach (var name in new[] { nameof(IsChooseScopePhase), nameof(IsConfirmRulesPhase), nameof(IsScanPhase), nameof(IsReviewPhase),
            nameof(CanContinue), nameof(CanStartScan), nameof(CanOpenReview),
            nameof(RecursionSummary), nameof(ScanCountsText), nameof(CoverageStatus), nameof(CouldNotAnalyze) })
            OnPropertyChanged(name);
        if (!preserveReviewProjection) OnPropertyChanged(nameof(ExactGroups));
        if (previousBusy != IsScanBusy) OnPropertyChanged(nameof(IsScanBusy));
        if (previousCanConfirm != CanConfirmExactRules) OnPropertyChanged(nameof(CanConfirmExactRules));
        if (previousStatus != ScanStatus) OnPropertyChanged(nameof(ScanStatus));
        if (previousPhase != CurrentPhase) OnPropertyChanged(nameof(CurrentPhase));
    }

    private async Task ChangeReviewSelectionAsync(
        Guid groupId,
        SelectionCommandKind kind,
        ImmutableArray<long> occurrenceIds)
    {
        var workflow = photoWorkflow;
        var snapshot = photoSnapshot?.Review;
        var group = snapshot?.Groups.FirstOrDefault(candidate => candidate.Candidate.GroupId == groupId);
        if (workflow is null || snapshot?.Pin is null || group is null) return;
        var command = new SelectionCommand(
            new(snapshot.Pin, groupId, group.Candidate.GroupRevision, snapshot.SelectionRevision),
            kind, occurrenceIds, Guid.NewGuid());
        var invokingFocus = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA
            ? Keyboard.FocusedElement
            : null;
        var result = await workflow.ChangeReviewSelectionAsync(command, CancellationToken.None);
        if (result.Outcome != ReviewCommandOutcome.OverrideRequired || result.OverrideProposal is not { } proposal)
        {
            ApplyReviewResult(result);
            return;
        }

        if (reviewDialog is null || invokingFocus is null)
        {
            ReviewAnnouncement = "Final-survivor override requires an explicitly focused invoking control.";
            return;
        }

        var dialogResult = await reviewDialog.ShowSurvivorOverrideAsync(
            proposal, invokingFocus, CancellationToken.None);
        if (dialogResult != SurvivorOverrideDialogResult.Affirm) return;
        var affirmation = new OverrideAffirmationCommand(
            proposal, new OverrideEvidenceId(Guid.NewGuid()), DateTimeOffset.UtcNow);
        ApplyReviewResult(await workflow.AffirmReviewOverrideAsync(affirmation, CancellationToken.None));
    }

    private async Task ApproveReviewAsync()
    {
        var workflow = photoWorkflow;
        var review = photoSnapshot?.Review;
        if (workflow is null || review?.Pin is null || !CanApproveReview) return;
        if (approvalIdentitySession != review.Pin || approvalIdentityRevision != review.SelectionRevision)
        {
            approvalIdentitySession = review.Pin;
            approvalIdentityRevision = review.SelectionRevision;
            approvalIdentityKey = Guid.NewGuid();
        }
        var command = new ApproveSelectionCommand(
            review.Pin, review.SelectionRevision, approvalIdentityKey, DateTimeOffset.UtcNow);
        ApplyReviewResult(await workflow.ApproveReviewAsync(command, CancellationToken.None));
    }

    private void ApplyReviewResult(ReviewCommandResult result)
    {
        if (photoWorkflow is { } workflow && !ReferenceEquals(photoSnapshot, workflow.Current))
            ApplyCombined(workflow.Current);
        var announcement = result.Outcome switch
        {
            ReviewCommandOutcome.Stale => "The Review changed; the stale command made no change.",
            ReviewCommandOutcome.PersistenceFailed => "Override evidence persistence failed; Selection is unchanged.",
            ReviewCommandOutcome.EvidenceUnavailable => "Override evidence is unavailable; approval is blocked.",
            ReviewCommandOutcome.Faulted => "Review is faulted; controls are unavailable.",
            ReviewCommandOutcome.Sealed => "Selection is sealed by explicit approval.",
            _ => null,
        };
        if (announcement is not null) ReviewAnnouncement = announcement;
    }

    private static bool ReviewProjectionUnchanged(ReviewSnapshot? current, ReviewSnapshot next) =>
        ReferenceEquals(current, next) || current == next;

    private static bool ScanProjectionUnchanged(
        PhotoSift.Application.Scan.ScanSnapshot? current,
        PhotoSift.Application.Scan.ScanSnapshot next) =>
        ReferenceEquals(current, next) || current == next;

    private void ProjectReview(ReviewSnapshot review)
    {
        var selectedMembers = review.Groups.SelectMany(group => group.Members)
            .Where(member => member.Selected).ToArray();
        var selectedCount = selectedMembers.LongLength;
        var distinctPhysical = selectedMembers.Where(member => member.Occurrence.PhysicalFileKey is not null)
            .Select(member => member.Occurrence.PhysicalFileKey!.Value).Distinct().LongCount();
        ApprovalSelectedOccurrenceText = $"{selectedCount.ToString(CultureInfo.InvariantCulture)} selected occurrences";
        ApprovalSelectedPathText = $"{selectedCount.ToString(CultureInfo.InvariantCulture)} selected paths";
        ApprovalSelectedPhysicalText = $"{distinctPhysical.ToString(CultureInfo.InvariantCulture)} distinct physical files";
        ApprovalRecoveryText = FormatReviewRecovery(review);
        ApprovalCoverageText = review.Coverage == ScanSessionStatus.Complete
            ? "Complete Scan coverage"
            : review.Coverage == ScanSessionStatus.Incomplete ? "Incomplete Scan coverage" : "No terminal Scan coverage";
        ApprovalUnresolvedText = $"Unresolved total: {review.Unresolved.Total.ToString(CultureInfo.InvariantCulture)}";
        ApprovalUnresolvedSubtotals = $"Incomplete enumeration: {review.Unresolved.IncompleteEnumeration.ToString(CultureInfo.InvariantCulture)}; failed Scan rows: {review.Unresolved.FailedScanRows.ToString(CultureInfo.InvariantCulture)}; canceled Scan rows: {review.Unresolved.CanceledScanRows.ToString(CultureInfo.InvariantCulture)}; unprocessed Scan rows: {review.Unresolved.UnprocessedScanRows.ToString(CultureInfo.InvariantCulture)}; pending Review members: {review.Unresolved.PendingReviewMembers.ToString(CultureInfo.InvariantCulture)}; unavailable Review members: {review.Unresolved.UnavailableReviewMembers.ToString(CultureInfo.InvariantCulture)}";
        ApprovalUnselectedSurvivorsText = $"Ordinary unselected survivors: {review.Unresolved.UnselectedSurvivorMembers.ToString(CultureInfo.InvariantCulture)}";
        ApprovalAutomationStatus = $"{ApprovalSelectedOccurrenceText}; {ApprovalSelectedPathText}; {ApprovalSelectedPhysicalText}; {ApprovalRecoveryText} {ApprovalCoverageText}; {ApprovalUnresolvedText}; {ApprovalUnresolvedSubtotals}; {ApprovalUnselectedSurvivorsText}; {ApprovalDestinationText} {ApprovalRevalidationText}";
        SelectionSummary = $"{ApprovalSelectedOccurrenceText}; {ApprovalRecoveryText} {ApprovalCoverageText}; unresolved total {review.Unresolved.Total.ToString(CultureInfo.InvariantCulture)}.";
        CanApproveReview = review.Status == ReviewStatus.Ready
            && review.Coverage is ScanSessionStatus.Complete or ScanSessionStatus.Incomplete
            && selectedCount > 0 && !review.Sealed
            && selectedMembers.All(member => member.Eligibility == ReviewEligibilityState.Eligible);
    }

    private static string FormatReviewRecovery(ReviewSnapshot review)
    {
        var selectedGroups = review.Groups.Where(group => group.SelectedOccurrenceIds.Count > 0).ToArray();
        var unavailable = selectedGroups.FirstOrDefault(group => group.Recovery.Bytes is null)?.Recovery;
        if (unavailable is not null)
            return $"Estimated recovery for this Selection: unavailable ({unavailable.Reason}).";
        try
        {
            var bytes = selectedGroups.Aggregate(0L,
                (sum, group) => checked(sum + group.Recovery.Bytes.GetValueOrDefault()));
            return $"Estimated recovery for this Selection: {bytes.ToString(CultureInfo.InvariantCulture)} bytes.";
        }
        catch (OverflowException)
        {
            return $"Estimated recovery for this Selection: unavailable ({SelectionRecoveryUnavailableReason.Overflow}).";
        }
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

    public ValueTask DisposeAsync()
    {
        lock (lifetime)
            return new(disposalTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        chooseScopeWorkflow.SnapshotChanged -= OnSnapshotChanged;
        if (photoWorkflow is not null)
            photoWorkflow.SnapshotChanged -= OnPhotoSnapshotChanged;
        if (photoWorkflow is IAsyncDisposable disposable)
            await disposable.DisposeAsync().ConfigureAwait(false);
    }
}





