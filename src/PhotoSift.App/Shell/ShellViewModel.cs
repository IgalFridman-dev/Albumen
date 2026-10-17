using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using PhotoSift.Application.Review;
using PhotoSift.Application.Action;
using PhotoSift.Application.LocalData;
using PhotoSift.Application.Scan;
using PhotoSift.Domain.Action;
using PhotoSift.Application.Shell;
using ScanSessionStatus = PhotoSift.Application.Scan.ScanStatus;
using ReviewLevel = PhotoSift.App.Shell.ReviewNavigationLevel;
using PhotoSift.Domain.Review;
using PhotoSift.Domain.Scan;
using PhotoSift.Domain.Scope;
using PhotoSift.Domain.Workflow;
using PhotoSift.Infrastructure.Windows.Preview;

namespace PhotoSift.App.Shell;

public sealed class ShellViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const string ActiveScanClearStatus =
        "Local Scan data was retained because a Scan is active. Wait for it to finish, or cancel it, then try again.";
    private readonly IChooseScopeWorkflow chooseScopeWorkflow;
    private readonly IPhotoSiftWorkflow? photoWorkflow;
    private readonly IReviewDialogService? reviewDialog;
    private readonly ILocalScanDataControl? localScanDataControl;
    private IReviewPreviewSource? reviewPreviewSource;
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
    private bool reviewOpenFailureAnnouncementActive;
    private ScanPin? reviewOpenFailureScan;
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
    private string actionAnnouncement = string.Empty;
    private string actionRecoveryAnnouncement = string.Empty;
    private bool isClearScanDataConfirmationPending;
    private string localDataClearStatus = string.Empty;
    private ImmutableArray<ReviewCategoryViewModel> reviewCategories = [];
    private ImmutableArray<ExactGroupViewModel>? legacyExactGroups;
    private ImmutableArray<ScanOccurrenceViewModel>? legacyCouldNotAnalyze;
    private ReviewLevel reviewNavigationLevel = ReviewLevel.Categories;
    private ImmutableArray<string> reviewBreadcrumbs = ["Review"];
    private ReviewCategoryViewModel? activeReviewCategory;
    private ReviewGroupViewModel? activeReviewGroup;
    private ScanOccurrenceViewModel? activeReviewMember;
    private PreviewItemViewModel? activeReviewPreview;
    private ReviewComparisonEvidenceViewModel? activeReviewEvidence;
    private CancellationTokenSource? activePreviewCancellation;
    private long activePreviewRequest;
    private readonly ObservableCollection<ActionItemViewModel> actionRows = [];
    private readonly ObservableCollection<ActionRecordViewModel> actionRecoveryRecords = [];
    private readonly object lifetime = new();
    private Task? disposalTask;

    public ShellViewModel(IChooseScopeWorkflow chooseScopeWorkflow)
    {
        ActionRows = new(actionRows);
        ActionRecoveryRecords = new(actionRecoveryRecords);
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
        ChangeNearDuplicateModeCommand = new AsyncCommand(
            parameter => photoWorkflow?.SetScanOptionsAsync(new(
                ExactDuplicateMode.Enabled,
                RequireBoolean(parameter)
                    ? NearDuplicateMode.Conservative
                    : NearDuplicateMode.Disabled)) ?? Task.CompletedTask,
            canExecute: () => CanChangeScanOptions);
        ConfirmExactRulesCommand = new AsyncCommand(() => photoWorkflow?.ConfirmExactRulesAsync() ?? Task.CompletedTask);
        StartScanCommand = new AsyncCommand(() => photoWorkflow?.StartScanAsync() ?? Task.CompletedTask);
        CancelScanCommand = new AsyncCommand(() => photoWorkflow?.CancelScanAsync() ?? Task.CompletedTask);
        OpenReviewCommand = new AsyncCommand(OpenReviewAsync);
        ApproveReviewCommand = new AsyncCommand(ApproveReviewAsync);
        RevalidateUnknownCommand = new AsyncCommand(RevalidateUnknownAsync,
            canExecute: () => CanRevalidateUnknownItems);
        RequestClearScanDataCommand = new AsyncCommand(RequestClearScanDataAsync,
            canExecute: () => localScanDataControl is not null
                && !IsScanBusy && !IsClearScanDataConfirmationPending);
        ConfirmClearScanDataCommand = new AsyncCommand(ConfirmClearScanDataAsync,
            canExecute: () => localScanDataControl is not null
                && !IsScanBusy && IsClearScanDataConfirmationPending);
        CancelClearScanDataCommand = new AsyncCommand(CancelClearScanDataAsync,
            canExecute: () => IsClearScanDataConfirmationPending);
        OpenReviewCategoryCommand = new AsyncCommand(OpenReviewCategoryAsync);
        OpenReviewGroupCommand = new AsyncCommand(OpenReviewGroupAsync);
        OpenReviewMemberCommand = new AsyncCommand(
            OpenReviewMemberAsync, allowConcurrentExecutions: true);
        ReviewBackCommand = new AsyncCommand(ReviewBackAsync,
            canExecute: () => ReviewNavigationLevel != ReviewLevel.Categories);
        NavigateCommand = new AsyncCommand(parameter => photoWorkflow is not null && parameter is WorkflowPhase phase
            ? photoWorkflow.NavigateAsync(phase) : parameter is WorkflowPhase.ChooseScope
                ? this.chooseScopeWorkflow.HandleAsync(new ChooseScopeCommand(), CancellationToken.None) : Task.CompletedTask);
        this.chooseScopeWorkflow.SnapshotChanged += OnSnapshotChanged;
        Apply(this.chooseScopeWorkflow.Current);
    }

    public ShellViewModel(IChooseScopeWorkflow scope, IPhotoSiftWorkflow workflow,
        IReviewDialogService? reviewDialog = null,
        ILocalScanDataControl? localScanDataControl = null,
        IReviewPreviewSource? reviewPreviewSource = null) : this(scope)
    {
        photoWorkflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        this.reviewDialog = reviewDialog;
        this.localScanDataControl = localScanDataControl;
        this.reviewPreviewSource = reviewPreviewSource;
        workflow.SnapshotChanged += OnPhotoSnapshotChanged;
        ApplyCombined(workflow.Current);
    }

    public AsyncCommand ContinueCommand { get; }
    public AsyncCommand ChangeNearDuplicateModeCommand { get; }
    public AsyncCommand ConfirmExactRulesCommand { get; }
    public AsyncCommand StartScanCommand { get; }
    public AsyncCommand CancelScanCommand { get; }
    public AsyncCommand OpenReviewCommand { get; }
    public AsyncCommand ApproveReviewCommand { get; }
    public AsyncCommand RevalidateUnknownCommand { get; }
    public AsyncCommand RequestClearScanDataCommand { get; }
    public AsyncCommand ConfirmClearScanDataCommand { get; }
    public AsyncCommand CancelClearScanDataCommand { get; }
    public AsyncCommand OpenReviewCategoryCommand { get; }
    public AsyncCommand OpenReviewGroupCommand { get; }
    public AsyncCommand OpenReviewMemberCommand { get; }
    public AsyncCommand ReviewBackCommand { get; }
    public AsyncCommand NavigateCommand { get; }
    public bool HasLocalScanDataControl => localScanDataControl is not null;
    public bool IsClearScanDataConfirmationPending
    {
        get => isClearScanDataConfirmationPending;
        private set
        {
            if (isClearScanDataConfirmationPending == value) return;
            isClearScanDataConfirmationPending = value;
            RequestClearScanDataCommand.NotifyCanExecuteChanged();
            ConfirmClearScanDataCommand.NotifyCanExecuteChanged();
            CancelClearScanDataCommand.NotifyCanExecuteChanged();
            OnPropertyChanged();
        }
    }
    public string LocalDataClearStatus
    {
        get => localDataClearStatus;
        private set => SetField(ref localDataClearStatus, value);
    }
    public WorkflowPhase CurrentPhase => photoSnapshot?.CurrentPhase ?? WorkflowPhase.ChooseScope;
    internal ReviewSessionPin? ReviewSession => photoSnapshot?.Review.Pin;
    public bool IsChooseScopePhase => CurrentPhase == WorkflowPhase.ChooseScope;
    public bool IsConfirmRulesPhase => CurrentPhase == WorkflowPhase.ConfirmRules;
    public bool IsScanPhase => CurrentPhase == WorkflowPhase.Scan;
    public bool IsReviewPhase => CurrentPhase == WorkflowPhase.Review;
    public bool IsActionPhase => CurrentPhase == WorkflowPhase.ApproveCleanup;
    public ImmutableArray<ActionItemViewModel> ActionItems { get; private set; } = [];
    public ReadOnlyObservableCollection<ActionItemViewModel> ActionRows { get; }
    private ActionSnapshot CurrentAction => photoSnapshot?.Action ?? ActionSnapshot.Idle;
    public string SourceMutationNotice => CurrentAction.RequestId is null
        ? "No source change has occurred. No account or network connection is required."
        : "Only explicitly approved items are sent one at a time to Windows Recycle Bin after current checks and durable recording. No account or network connection is required.";
    public string ActionStatusText => CurrentAction.FaultReason == ActionSafetyReason.None
        ? $"Action: {CurrentAction.Status}"
        : $"Action: {CurrentAction.Status}; {ActionItemViewModel.DescribeReason(CurrentAction.FaultReason)}";
    public string ActionCountsText => string.Create(CultureInfo.InvariantCulture,
        $"{CurrentAction.Items.Length} approved items; {CurrentAction.DurableRequestedCount} requested records; {CurrentAction.DurableExecutingCount} executing records; {CurrentAction.RecycledCount} recycled; {CurrentAction.NotRemovedCount} not removed; {CurrentAction.UnknownCount} unknown.");
    public string ActionCertaintyText => CurrentAction.Items.Any(item => item.State is ActionItemState.Executing or ActionItemState.UnknownPendingRevalidation)
        ? "Some outcomes are uncertain. Inspect each original path and its recorded reason. A later safe review is required for uncertain items."
        : "Per-item states and reasons show what was recorded; an overall result does not replace them.";
    public string ActionReplayNotice { get; } = "No automatic replay or retry is performed. Interrupted or uncertain items require a later safe review.";
    public string ActionRecoveryNotice { get; } = "Actual recovered space is not measured. A Recycle Bin result does not establish physical-file deletion or freed disk space.";
    public string ActionSelectionText => photoSnapshot?.ActionReview.ApprovedRequest is { } approved
        && approved.RequestId == CurrentAction.RequestId
        ? string.Create(CultureInfo.InvariantCulture,
            $"Selection is sealed: {approved.Disclosure.SelectedOccurrenceCount} selected occurrences; {approved.Disclosure.SelectedOccurrenceCount} selected paths; {approved.Disclosure.DistinctPhysicalKeyCount} distinct physical files. {approved.Disclosure.Coverage} Scan coverage at approval.")
        : string.Create(CultureInfo.InvariantCulture, $"Selection is sealed for this Action: {CurrentAction.Items.Length} explicitly approved occurrences.");
    public string ActionAnnouncement => actionAnnouncement;
    public ReadOnlyObservableCollection<ActionRecordViewModel> ActionRecoveryRecords { get; }
    public bool HasActionRecoveryRecords => ActionRecoveryRecords.Count != 0;
    public bool HasActionRecoveryDisclosure => photoSnapshot?.ActionRecovery.Records.Any(record =>
        record.Summary.Certainty == ActionRecordCertainty.HasUnknown) is true;
    public string ActionRecoveryDisclosure { get; } = "PhotoSift found an unfinished cleanup from a previous session. The outcome of one or more items is unknown. Revalidate their current file state. Revalidation will not remove files.";
    public string ActionRecoveryAnnouncement => actionRecoveryAnnouncement;
    public string ActionRecoveryStatusText => photoSnapshot?.ActionRecovery.Status switch
    {
        ActionRecoveryStatus.Loading => "Loading Action records…",
        ActionRecoveryStatus.Faulted =>
            "Local data could not be safely loaded. PhotoSift will not modify files until the database is available.",
        _ => string.Empty,
    };
    public bool HasActionRecoveryStatus => ActionRecoveryStatusText.Length != 0;
    public bool IsLocalDataFaulted => photoSnapshot?.ActionRecovery.Status == ActionRecoveryStatus.Faulted;
    public bool CanRevalidateUnknownItems => photoSnapshot?.ActionRecovery is
        { Status: ActionRecoveryStatus.Ready, ProbeOutstanding: false }
        && HasActionRecoveryDisclosure;
    public bool CanContinue => photoSnapshot?.Scope.ScopeState == ScopeSessionState.Ready;
    public bool CanChangeScanOptions => CanContinue && IsConfirmRulesPhase && !IsScanBusy;
    public bool CanConfirmExactRules => CanChangeScanOptions
        && photoSnapshot is { Rules: null, Options.IsValid: true };
    public bool CanStartScan => photoSnapshot is { } snapshot && ShellPhasePolicy.CanStart(snapshot.Scope, snapshot.Rules) && !IsScanBusy;
    public bool IsScanBusy => photoSnapshot?.Scan is
        { HasUnsettledWork: true } or
        { Status: ScanSessionStatus.Checking or ScanSessionStatus.Running };
    public bool CanOpenReview => photoSnapshot?.Scan.Pin is not null;
    public bool NearDuplicatesEnabled => photoSnapshot?.Options.Near == NearDuplicateMode.Conservative;
    public string ExactPreset { get; } = "Exact duplicates are always enabled. Files must contain identical bytes.";
    public string ScanOptionsGuidance => photoSnapshot?.Options is { } options
        ? options.IsValid
            ? options.Near == NearDuplicateMode.Conservative
                ? "Exact is always enabled. Near uses the fixed Conservative profile; ambiguous matches remain Uncertain. Selection stays manual."
                : "Exact is always enabled. Near is disabled for the next Scan. Selection stays manual."
            : options.ValidationGuidance
        : "Exact is always enabled. Near defaults to the fixed Conservative profile. Selection stays manual.";
    public string RecursionSummary => IncludeSubfolders ? "Include subfolders" : "Top-level folder only";
    public string ScanStatus => photoSnapshot is { } snapshot
        ? $"{snapshot.Scan.Status}; Enumeration: {snapshot.Scan.Enumeration.State}; Exact: {(snapshot.Scan.Counts.Outstanding > 0 ? "Running" : snapshot.Scan.Status is ScanSessionStatus.Complete or ScanSessionStatus.Incomplete ? "Finished" : "Pending")}" : "Idle";
    public ReviewStatus ReviewState => photoSnapshot?.Review.Status ?? ReviewStatus.Closed;
    public ReviewUnavailableReason ReviewFaultReason => photoSnapshot?.Review.FaultReason ?? ReviewUnavailableReason.None;
    public string ReviewStatusText => ReviewState == ReviewStatus.Faulted
        ? $"Review is faulted ({ReviewFaultReason}); controls are unavailable."
        : $"Review: {ReviewState}";
    public string ScanCountsText => photoSnapshot is { Scan.Counts: var count }
        ? $"Discovered: {count.Discovered}; Analyzed: {count.Analyzed}; Unsupported: {count.Unsupported}; Failed: {count.Failed}; Canceled: {count.Canceled}; Outstanding: {count.Outstanding}" : string.Empty;
    public string CoverageStatus => photoSnapshot?.Scan.Status == ScanSessionStatus.Complete
        ? "Complete scope coverage. Unsupported, failed, and canceled/unprocessed files are reported separately."
        : "Partial results — incomplete scope coverage. Results do not represent every file in the Scope.";
    public string ReviewDisclaimer { get; } = "Analyzed revisions; scan again after files change. Results are memory-only and are not restored after restart.";
    public ImmutableArray<ExactGroupViewModel> ExactGroups =>
        legacyExactGroups ??= ProjectLegacyExactGroups();
    public ImmutableArray<ScanOccurrenceViewModel> CouldNotAnalyze =>
        legacyCouldNotAnalyze ??= (photoSnapshot?.Scan.Occurrences ?? [])
            .Where(IsCouldNotAnalyze)
            .Select(row => new ScanOccurrenceViewModel(row)).ToImmutableArray();
    public ImmutableArray<ScanOutcomeSummaryItemViewModel> ScanOutcomeSummaries { get; private set; } = [];
    public ImmutableArray<ReviewCategoryViewModel> ReviewCategories
    {
        get => reviewCategories;
        private set => reviewCategories = value;
    }
    public ReviewLevel ReviewNavigationLevel
    {
        get => reviewNavigationLevel;
        private set => reviewNavigationLevel = value;
    }
    public ImmutableArray<string> ReviewBreadcrumbs
    {
        get => reviewBreadcrumbs;
        private set => reviewBreadcrumbs = value;
    }
    public string ReviewBreadcrumbText => string.Join(" > ", ReviewBreadcrumbs);
    public bool CanReviewBack => ReviewNavigationLevel != ReviewLevel.Categories;
    public LazyReviewCollection<ReviewGroupViewModel>? ActiveReviewGroups => ActiveReviewCategory?.Groups;
    public LazyReviewCollection<ScanOccurrenceViewModel>? ActiveReviewMembers => ActiveReviewGroup?.Members;
    public ReviewCategoryViewModel? ActiveReviewCategory
    {
        get => activeReviewCategory;
        private set => activeReviewCategory = value;
    }
    public ReviewGroupViewModel? ActiveReviewGroup
    {
        get => activeReviewGroup;
        private set => activeReviewGroup = value;
    }
    public ScanOccurrenceViewModel? ActiveReviewMember
    {
        get => activeReviewMember;
        private set => activeReviewMember = value;
    }
    public PreviewItemViewModel? ActiveReviewPreview
    {
        get => activeReviewPreview;
        private set => activeReviewPreview = value;
    }
    public ReviewComparisonEvidenceViewModel? ActiveReviewEvidence
    {
        get => activeReviewEvidence;
        private set => activeReviewEvidence = value;
    }

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

    private void SetReviewAnnouncement(
        string announcement,
        bool failedOpen = false,
        ScanPin? failedScan = null)
    {
        reviewOpenFailureAnnouncementActive = failedOpen;
        reviewOpenFailureScan = failedOpen ? failedScan : null;
        ReviewAnnouncement = announcement;
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

    public string ApprovalDestinationText { get; } = "Destination: Windows Recycle Bin.";
    public string ApprovalRevalidationText { get; } =
        "Only the listed, revalidated selected occurrences are included. Each original path is sent one at a time after current checks of Scope, identity, content, remaining copies and override evidence, and durable recording.";
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
        var previousAction = photoSnapshot?.Action;
        var previousRecovery = photoSnapshot?.ActionRecovery;
        var previousActionReview = photoSnapshot?.ActionReview;
        var previousReview = photoSnapshot?.Review;
        var previousReviewStatusText = ReviewStatusText;
        var preserveReviewProjection = ReviewProjectionUnchanged(photoSnapshot?.Review, snapshot.Review)
            && (snapshot.Review.Pin is not null || ScanProjectionUnchanged(photoSnapshot?.Scan, snapshot.Scan));
        var previousPhase = CurrentPhase;
        var previousStatus = ScanStatus;
        var previousBusy = IsScanBusy;
        var previousCanConfirm = CanConfirmExactRules;
        var previousCanChangeOptions = CanChangeScanOptions;
        var previousOptions = photoSnapshot?.Options;
        Apply(snapshot.Scope);
        photoSnapshot = snapshot;
        if (IsScanBusy && IsClearScanDataConfirmationPending)
            RetainScanDataForActiveScan();
        ProjectAction(previousAction, previousActionReview, snapshot);
        ProjectActionRecovery(previousRecovery, snapshot.ActionRecovery);
        Phases = Project(snapshot.Phases);
        RuleSetStatus = snapshot.Rules is null
            ? "Scan options not confirmed"
            : snapshot.Rules.Options.Near == NearDuplicateMode.Conservative
                ? "Exact enabled; Near Conservative — confirmed"
                : "Exact enabled; Near disabled — confirmed";
        if (!preserveReviewProjection)
        {
            var previousNavigationLevel = ReviewNavigationLevel;
            var previousCategory = ActiveReviewCategory?.Category;
            var previousGroupId = ActiveReviewGroup?.GroupId;
            var previousMemberId = ActiveReviewMember?.OccurrenceId;
            legacyExactGroups = null;
            ReviewCategories = ProjectReviewCategories(snapshot);
            RestoreReviewNavigation(previousNavigationLevel, previousCategory,
                previousGroupId, previousMemberId);
        }
        legacyCouldNotAnalyze = null;
        ScanOutcomeSummaries = ProjectOutcomeSummaries(snapshot.Scan);
        ProjectReview(snapshot.Review);
        if (snapshot.Review.Pin is not null && snapshot.Review.Status == ReviewStatus.Faulted
            && (previousReview is null || previousReview.Pin != snapshot.Review.Pin || previousReview.Status != ReviewStatus.Faulted
                || previousReview.FaultReason != snapshot.Review.FaultReason))
            SetReviewAnnouncement(ReviewStatusText);
        else if (previousReview?.Pin != snapshot.Review.Pin
            || reviewOpenFailureAnnouncementActive
                && (snapshot.CurrentPhase != WorkflowPhase.Scan
                    || snapshot.Scan.Pin != reviewOpenFailureScan))
            SetReviewAnnouncement(string.Empty);
        if (previousReview?.Status != snapshot.Review.Status) OnPropertyChanged(nameof(ReviewState));
        if (previousReview?.FaultReason != snapshot.Review.FaultReason) OnPropertyChanged(nameof(ReviewFaultReason));
        if (previousReviewStatusText != ReviewStatusText) OnPropertyChanged(nameof(ReviewStatusText));
        foreach (var name in new[] { nameof(IsChooseScopePhase), nameof(IsConfirmRulesPhase), nameof(IsScanPhase), nameof(IsReviewPhase), nameof(IsActionPhase),
            nameof(CanContinue), nameof(CanStartScan), nameof(CanOpenReview),
            nameof(RecursionSummary), nameof(ScanCountsText), nameof(CoverageStatus), nameof(CouldNotAnalyze),
            nameof(ScanOutcomeSummaries) })
            OnPropertyChanged(name);
        if (previousOptions != snapshot.Options)
        {
            OnPropertyChanged(nameof(NearDuplicatesEnabled));
            OnPropertyChanged(nameof(ScanOptionsGuidance));
        }
        if (!preserveReviewProjection)
        {
            OnPropertyChanged(nameof(ExactGroups));
            OnPropertyChanged(nameof(ReviewCategories));
        }
        if (previousBusy != IsScanBusy)
        {
            RequestClearScanDataCommand.NotifyCanExecuteChanged();
            ConfirmClearScanDataCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsScanBusy));
        }
        if (previousCanConfirm != CanConfirmExactRules) OnPropertyChanged(nameof(CanConfirmExactRules));
        if (previousCanChangeOptions != CanChangeScanOptions)
        {
            ChangeNearDuplicateModeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanChangeScanOptions));
        }
        if (previousStatus != ScanStatus) OnPropertyChanged(nameof(ScanStatus));
        if (previousPhase != CurrentPhase) OnPropertyChanged(nameof(CurrentPhase));
    }

    private Task RequestClearScanDataAsync()
    {
        if (localScanDataControl is null) return Task.CompletedTask;
        if (IsScanBusy)
        {
            RetainScanDataForActiveScan();
            return Task.CompletedTask;
        }
        LocalDataClearStatus = string.Empty;
        IsClearScanDataConfirmationPending = true;
        return Task.CompletedTask;
    }

    private Task CancelClearScanDataAsync()
    {
        IsClearScanDataConfirmationPending = false;
        return Task.CompletedTask;
    }

    private async Task ConfirmClearScanDataAsync()
    {
        if (localScanDataControl is null || !IsClearScanDataConfirmationPending) return;
        if (IsScanBusy)
        {
            RetainScanDataForActiveScan();
            return;
        }
        IsClearScanDataConfirmationPending = false;
        LocalDataClearStatus = "Clearing local Scan data…";
        try
        {
            var result = await localScanDataControl.ClearAsync(CancellationToken.None);
            LocalDataClearStatus = result.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LocalDataClearStatus =
                "Local Scan data could not be cleared and was retained. Check the local database, then try again.";
        }
    }

    private void RetainScanDataForActiveScan()
    {
        IsClearScanDataConfirmationPending = false;
        LocalDataClearStatus = ActiveScanClearStatus;
    }

    private static bool IsCouldNotAnalyze(ScanFileOccurrence row) =>
        row.Exact.State switch
        {
            ScanStageState.Unsupported or ScanStageState.FailedRetryable or ScanStageState.FailedTerminal
                or ScanStageState.Canceled or ScanStageState.Pending or ScanStageState.Running => true,
            ScanStageState.NotApplicable => row.Enumerate.State is ScanStageState.Unsupported
                or ScanStageState.FailedRetryable or ScanStageState.FailedTerminal or ScanStageState.Canceled
                or ScanStageState.Pending or ScanStageState.Running,
            _ => IsAnalysisFailure(row.VisualFeatures) || IsAnalysisFailure(row.NearDuplicate),
        };

    private static bool IsAnalysisFailure(StageOutcome outcome) => outcome.State is
        ScanStageState.Unsupported or ScanStageState.FailedRetryable
        or ScanStageState.FailedTerminal or ScanStageState.Canceled;

    private ImmutableArray<ExactGroupViewModel> ProjectLegacyExactGroups()
    {
        if (photoSnapshot is not { } snapshot) return [];
        return snapshot.Review.Pin is null
            ? snapshot.Scan.Groups.Select(group => new ExactGroupViewModel(group)).ToImmutableArray()
            : snapshot.Review.Groups.Select((group, index) => new ExactGroupViewModel(group,
                snapshot.Review.Status == ReviewStatus.Ready && !snapshot.Review.Sealed,
                snapshot.Review.SelectionRevision, index + 1, snapshot.Review.Groups.Length,
                (kind, ids) => ChangeReviewSelectionAsync(group.Candidate.GroupId, kind, ids)))
                .ToImmutableArray();
    }

    private ImmutableArray<ReviewCategoryViewModel> ProjectReviewCategories(PhotoSiftSnapshot snapshot) =>
    [
        ProjectCandidateCategory(snapshot, ReviewCategoryKind.Exact, "Exact", 1),
        ProjectCandidateCategory(snapshot, ReviewCategoryKind.Near, "Near", 2),
        ProjectCandidateCategory(snapshot, ReviewCategoryKind.Uncertain, "Uncertain", 3),
        ProjectCouldNotAnalyzeCategory(snapshot.Scan, 4),
    ];

    private ReviewCategoryViewModel ProjectCandidateCategory(
        PhotoSiftSnapshot snapshot,
        ReviewCategoryKind category,
        string name,
        int positionInSet)
    {
        if (snapshot.Review.Pin is not null)
        {
            var groups = snapshot.Review.Groups
                .Where(group => InCategory(group.Candidate, category))
                .ToImmutableArray();
            return new(category, name, groups.Length, positionInSet, 4, index =>
                new ReviewGroupViewModel(
                    groups[index],
                    category,
                    index + 1,
                    groups.Length,
                    snapshot.Review.Status == ReviewStatus.Ready && !snapshot.Review.Sealed,
                    (kind, ids) => ChangeReviewSelectionAsync(groups[index].Candidate.GroupId, kind, ids)),
                groupId => FindGroupIndex(groups, groupId));
        }

        var candidates = snapshot.Scan.Groups
            .Where(group => InCategory(group, category))
            .ToImmutableArray();
        return new(category, name, candidates.Length, positionInSet, 4,
            index => new ReviewGroupViewModel(
                candidates[index], category, index + 1, candidates.Length),
            groupId => FindGroupIndex(candidates, groupId));
    }

    private static ReviewCategoryViewModel ProjectCouldNotAnalyzeCategory(
        PhotoSift.Application.Scan.ScanSnapshot scan,
        int positionInSet)
    {
        var rows = scan.Occurrences.Where(IsCouldNotAnalyze).ToImmutableArray();
        return new(ReviewCategoryKind.CouldNotAnalyze, "Could Not Analyze", rows.Length,
            positionInSet, 4,
            index => new ReviewGroupViewModel(rows[index], index + 1, rows.Length),
            _ => null);
    }

    private static int? FindGroupIndex(
        ImmutableArray<ReviewGroupSnapshot> groups,
        Guid groupId)
    {
        for (var index = 0; index < groups.Length; index++)
        {
            if (groups[index].Candidate.GroupId == groupId) return index;
        }
        return null;
    }

    private static int? FindGroupIndex(
        ImmutableArray<CandidateGroup> groups,
        Guid groupId)
    {
        for (var index = 0; index < groups.Length; index++)
        {
            if (groups[index].GroupId == groupId) return index;
        }
        return null;
    }

    private static bool InCategory(CandidateGroup group, ReviewCategoryKind category) => category switch
    {
        ReviewCategoryKind.Exact => group.Category == CandidateCategory.Exact && !group.Uncertain,
        ReviewCategoryKind.Near => group.Category == CandidateCategory.NearDuplicate && !group.Uncertain,
        ReviewCategoryKind.Uncertain => group.Category == CandidateCategory.Uncertain || group.Uncertain,
        _ => false,
    };

    private Task OpenReviewCategoryAsync(object? parameter)
    {
        if (parameter is not ReviewCategoryViewModel category
            || !ReviewCategories.Any(row => ReferenceEquals(row, category)))
            throw new ArgumentException("Review category navigation requires a current category row.", nameof(parameter));

        SetReviewNavigation(ReviewLevel.Groups, category, null, null);
        return Task.CompletedTask;
    }

    private Task OpenReviewGroupAsync(object? parameter)
    {
        if (parameter is not ReviewGroupViewModel group
            || ActiveReviewCategory is null
            || group.Category != ActiveReviewCategory.Category)
            throw new ArgumentException("Review group navigation requires a group in the active category.", nameof(parameter));

        SetReviewNavigation(ReviewLevel.Comparisons, ActiveReviewCategory, group, null);
        return Task.CompletedTask;
    }

    private async Task OpenReviewMemberAsync(object? parameter)
    {
        if (parameter is not ScanOccurrenceViewModel member || ActiveReviewGroup is null)
            throw new ArgumentException("Review comparison navigation requires a member in the active group.", nameof(parameter));

        SetReviewNavigation(ReviewLevel.Comparisons, ActiveReviewCategory, ActiveReviewGroup, member);
        await LoadActiveReviewPreviewAsync(member);
    }

    private Task ReviewBackAsync()
    {
        if (ReviewNavigationLevel == ReviewLevel.Comparisons)
            SetReviewNavigation(ReviewLevel.Groups, ActiveReviewCategory, null, null);
        else if (ReviewNavigationLevel == ReviewLevel.Groups)
            ResetReviewNavigation();
        return Task.CompletedTask;
    }

    private void ResetReviewNavigation() =>
        SetReviewNavigation(ReviewLevel.Categories, null, null, null);

    private void RestoreReviewNavigation(
        ReviewLevel level,
        ReviewCategoryKind? categoryKind,
        Guid? groupId,
        long? memberId)
    {
        if (level == ReviewLevel.Categories || categoryKind is null)
        {
            ResetReviewNavigation();
            return;
        }

        var category = ReviewCategories.FirstOrDefault(row => row.Category == categoryKind);
        if (category is null)
        {
            ResetReviewNavigation();
            return;
        }

        if (level == ReviewLevel.Groups || groupId is null)
        {
            SetReviewNavigation(ReviewLevel.Groups, category, null, null);
            return;
        }

        var group = category.FindGroup(groupId.Value);
        if (group is null)
        {
            SetReviewNavigation(ReviewLevel.Groups, category, null, null);
            return;
        }

        var member = memberId is { } id ? group.FindMember(id) : null;
        var reloadPreview = member is not null && ActiveReviewMember is not null;
        SetReviewNavigation(ReviewLevel.Comparisons, category, group, member,
            preservePreview: member is not null && !reloadPreview);
        if (reloadPreview)
            _ = LoadActiveReviewPreviewAsync(member!);
    }

    private void SetReviewNavigation(
        ReviewLevel level,
        ReviewCategoryViewModel? category,
        ReviewGroupViewModel? group,
        ScanOccurrenceViewModel? member,
        bool preservePreview = false)
    {
        if (!preservePreview)
            ClearActiveReviewPreview();
        var navigationLevelChanged = ReviewNavigationLevel != level;
        ReviewNavigationLevel = level;
        ActiveReviewCategory = category;
        ActiveReviewGroup = group;
        ActiveReviewMember = member;
        ReviewBreadcrumbs = level switch
        {
            ReviewLevel.Categories => ["Review"],
            ReviewLevel.Groups => ["Review", category!.Name],
            ReviewLevel.Comparisons => ["Review", category!.Name, group!.Summary],
            _ => throw new InvalidOperationException($"Unknown Review navigation level: {level}"),
        };
        if (navigationLevelChanged) OnPropertyChanged(nameof(ReviewNavigationLevel));
        OnPropertyChanged(nameof(ActiveReviewCategory));
        OnPropertyChanged(nameof(ActiveReviewGroup));
        OnPropertyChanged(nameof(ActiveReviewMember));
        OnPropertyChanged(nameof(ReviewBreadcrumbs));
        OnPropertyChanged(nameof(ReviewBreadcrumbText));
        if (navigationLevelChanged) OnPropertyChanged(nameof(CanReviewBack));
        OnPropertyChanged(nameof(ActiveReviewGroups));
        OnPropertyChanged(nameof(ActiveReviewMembers));
        ReviewBackCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadActiveReviewPreviewAsync(ScanOccurrenceViewModel member)
    {
        var source = reviewPreviewSource;
        var snapshot = photoSnapshot;
        var scope = snapshot?.Scope.Preflight?.Scope;
        var pin = snapshot?.Scan.Pin;
        var occurrence = snapshot?.Scan.Occurrences.FirstOrDefault(
            row => row.Token.OccurrenceId == member.OccurrenceId);
        if (source is null || scope is null || pin is null || occurrence is null)
            return;

        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        activePreviewCancellation = cancellation;
        var request = Interlocked.Increment(ref activePreviewRequest);
        ReviewPreviewResult? result = null;
        try
        {
            result = await source.LoadAsync(
                new ScanRunContext(pin, scope), occurrence, cancellationToken);
            if (request != Volatile.Read(ref activePreviewRequest)
                || !ReferenceEquals(ActiveReviewMember, member))
                return;

            ActiveReviewPreview = new PreviewItemViewModel(result);
            ActiveReviewEvidence = ProjectReviewEvidence(occurrence, result);
            OnPropertyChanged(nameof(ActiveReviewPreview));
            OnPropertyChanged(nameof(ActiveReviewEvidence));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (request != Volatile.Read(ref activePreviewRequest)
                || !ReferenceEquals(ActiveReviewMember, member))
                return;
            result = ReviewPreviewResult.Failure(
                occurrence.ResolvedPath,
                PreviewAvailability.Unavailable,
                "Preview unavailable: the file could not be read.");
            ActiveReviewPreview = new PreviewItemViewModel(result);
            ActiveReviewEvidence = ProjectReviewEvidence(occurrence, result);
            OnPropertyChanged(nameof(ActiveReviewPreview));
            OnPropertyChanged(nameof(ActiveReviewEvidence));
        }
        finally
        {
            result?.Dispose();
            if (ReferenceEquals(activePreviewCancellation, cancellation))
                activePreviewCancellation = null;
            cancellation.Dispose();
        }
    }

    private ReviewComparisonEvidenceViewModel ProjectReviewEvidence(
        ScanFileOccurrence occurrence,
        ReviewPreviewResult result)
    {
        const string unavailable = "Unavailable";
        var category = ActiveReviewGroup?.Category ?? ReviewCategoryKind.CouldNotAnalyze;
        var candidate = photoSnapshot?.Scan.Groups.FirstOrDefault(
            group => group.GroupId == ActiveReviewGroup?.GroupId);
        var similarity = candidate?.Similarity.FirstOrDefault(edge =>
            edge.Left.OccurrenceId == occurrence.Token.OccurrenceId
            || edge.Right.OccurrenceId == occurrence.Token.OccurrenceId);
        var confidence = category switch
        {
            ReviewCategoryKind.Exact => "Byte-identical content.",
            ReviewCategoryKind.Near or ReviewCategoryKind.Uncertain when similarity is not null =>
                $"Hamming distance: {similarity.HammingDistance.ToString(CultureInfo.InvariantCulture)}; color distance: {similarity.ColorDistance.ToString(CultureInfo.InvariantCulture)}",
            _ => unavailable,
        };
        var uncertainty = category == ReviewCategoryKind.Uncertain
            ? "Uncertain relationship."
            : unavailable;
        var reason = new[]
            {
                occurrence.NearDuplicate.Reason,
                occurrence.VisualFeatures.Reason,
                occurrence.Exact.Reason,
                occurrence.Enumerate.Reason,
            }
            .FirstOrDefault(value => value != ScanReason.None);
        return new(
            occurrence.ResolvedPath,
            CategoryName(category),
            result.SourcePixelWidth is { } width && result.SourcePixelHeight is { } height
                ? $"{width.ToString(CultureInfo.InvariantCulture)} × {height.ToString(CultureInfo.InvariantCulture)} pixels"
                : unavailable,
            (result.ByteLength ?? occurrence.Content?.ByteLength) is { } bytes
                ? $"{bytes.ToString(CultureInfo.InvariantCulture)} bytes"
                : unavailable,
            result.RelevantDate?.ToString("O", CultureInfo.InvariantCulture) ?? unavailable,
            confidence,
            uncertainty,
            unavailable,
            reason == ScanReason.None ? unavailable : reason.ToString());
    }

    private static string CategoryName(ReviewCategoryKind category) => category switch
    {
        ReviewCategoryKind.Exact => "Exact",
        ReviewCategoryKind.Near => "Near",
        ReviewCategoryKind.Uncertain => "Uncertain",
        ReviewCategoryKind.CouldNotAnalyze => "Could Not Analyze",
        _ => throw new InvalidOperationException($"Unknown Review category: {category}"),
    };

    private void ClearActiveReviewPreview()
    {
        Interlocked.Increment(ref activePreviewRequest);
        var cancellation = Interlocked.Exchange(ref activePreviewCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        ActiveReviewPreview = null;
        ActiveReviewEvidence = null;
        OnPropertyChanged(nameof(ActiveReviewPreview));
        OnPropertyChanged(nameof(ActiveReviewEvidence));
    }

    private static ImmutableArray<ScanOutcomeSummaryItemViewModel> ProjectOutcomeSummaries(
        PhotoSift.Application.Scan.ScanSnapshot scan)
    {
        var uncertainIds = scan.Groups.Where(group => group.Uncertain)
            .SelectMany(group => group.Members)
            .Select(member => member.Token.OccurrenceId)
            .ToHashSet();
        var candidateIds = scan.Groups.Where(group => !group.Uncertain)
            .SelectMany(group => group.Members)
            .Select(member => member.Token.OccurrenceId)
            .Where(id => !uncertainIds.Contains(id))
            .ToHashSet();
        var acceptable = scan.Occurrences.LongCount(row =>
            row.Exact.State == ScanStageState.Succeeded
            && !IsCouldNotAnalyze(row)
            && !candidateIds.Contains(row.Token.OccurrenceId)
            && !uncertainIds.Contains(row.Token.OccurrenceId));
        return
        [
            new("Candidate", candidateIds.Count, "Review the group and choose manually."),
            new("Analyzed acceptable", acceptable,
                "No duplicate candidate was found. Keep it, or scan again after the file changes."),
            new("Uncertain", uncertainIds.Count,
                "Review it before selecting; PhotoSift could not classify it confidently."),
            new("Unsupported", scan.Counts.Unsupported,
                "Use a supported JPEG or PNG file, then start a fresh Scan."),
            new("Canceled/unprocessed", scan.Counts.Canceled,
                "Start a fresh Scan to analyze this file."),
            new("Failed", scan.Counts.Failed,
                "Check file access and health, then start a fresh Scan."),
            new("Outstanding", scan.Counts.Outstanding,
                "Wait for this Scan to finish, or cancel it and start a fresh Scan."),
        ];
    }

    private void ProjectAction(ActionSnapshot? previous, ReviewSnapshot? previousReview, PhotoSiftSnapshot snapshot)
    {
        if (ReferenceEquals(previous, snapshot.Action) || previous == snapshot.Action)
        {
            if (previousReview != snapshot.ActionReview) OnPropertyChanged(nameof(ActionSelectionText));
            return;
        }
        var previousRows = ActionItems;
        var retainRows = previous is { RequestId: not null } && previous.RequestId == snapshot.Action.RequestId
            && previous.Items.Length == snapshot.Action.Items.Length
            && previousRows.Length == snapshot.Action.Items.Length;
        ActionItems = snapshot.Action.Items.Select((item, index) =>
            retainRows && previous!.Items[index] == item ? previousRows[index]
                : new ActionItemViewModel(item, index + 1, snapshot.Action.Items.Length)).ToImmutableArray();
        // Preserve unchanged WPF containers (and path focus/selection) during
        // progress. Both the public snapshot and each displayed row stay immutable;
        // this read-only rendering collection never carries Action authority.
        if (retainRows)
        {
            for (var index = 0; index < ActionItems.Length; index++)
                if (!ReferenceEquals(actionRows[index], ActionItems[index])) actionRows[index] = ActionItems[index];
        }
        else
        {
            actionRows.Clear();
            foreach (var row in ActionItems) actionRows.Add(row);
        }
        foreach (var property in new[] { nameof(ActionItems), nameof(ActionStatusText), nameof(ActionCountsText),
            nameof(ActionCertaintyText), nameof(ActionSelectionText), nameof(SourceMutationNotice) })
            OnPropertyChanged(property);
        if (snapshot.Action.RequestId is null) return;
        var changedIndex = -1;
        for (var index = 0; index < snapshot.Action.Items.Length; index++)
        {
            if (previous is null || index >= previous.Items.Length || previous.Items[index] != snapshot.Action.Items[index])
                changedIndex = index;
        }
        actionAnnouncement = $"{ActionStatusText}. {ActionCountsText}"
            + (changedIndex < 0 ? string.Empty : $" Item {changedIndex + 1}: {ActionItems[changedIndex].ItemStatus}");
        OnPropertyChanged(nameof(ActionAnnouncement));
    }

    private void ProjectActionRecovery(ActionRecoverySnapshot? previous, ActionRecoverySnapshot recovery)
    {
        if (ReferenceEquals(previous, recovery) || previous == recovery) return;
        var projected = recovery.Records.Select(record =>
        {
            var oldRecord = actionRecoveryRecords.FirstOrDefault(candidate => candidate.RequestId == record.RequestId);
            if (oldRecord is null) return new ActionRecordViewModel(record,
                recovery.ProbeOutstanding ? recovery.ActiveActionItemId : null);
            oldRecord.Update(record, recovery.ProbeOutstanding ? recovery.ActiveActionItemId : null);
            return oldRecord;
        }).ToImmutableArray();
        if (projected.Length == actionRecoveryRecords.Count)
        {
            for (var index = 0; index < projected.Length; index++)
                if (!ReferenceEquals(actionRecoveryRecords[index], projected[index]))
                    actionRecoveryRecords[index] = projected[index];
        }
        else
        {
            actionRecoveryRecords.Clear();
            foreach (var record in projected) actionRecoveryRecords.Add(record);
        }
        foreach (var property in new[] { nameof(ActionRecoveryRecords), nameof(HasActionRecoveryRecords),
            nameof(HasActionRecoveryDisclosure), nameof(ActionRecoveryStatusText), nameof(HasActionRecoveryStatus),
            nameof(IsLocalDataFaulted), nameof(CanRevalidateUnknownItems) })
            OnPropertyChanged(property);
        var active = ActionRecoveryRecords.SelectMany(record => record.Items).FirstOrDefault(item => item.IsVerifying);
        actionRecoveryAnnouncement = active is not null
            ? $"{active.OriginalPath}. {active.StatusText}"
            : HasActionRecoveryDisclosure
                ? "Action record updated. One or more outcomes remain unknown pending revalidation."
                : HasActionRecoveryRecords ? "Action record updated." : string.Empty;
        OnPropertyChanged(nameof(ActionRecoveryAnnouncement));
    }

    private Task RevalidateUnknownAsync(object? parameter)
    {
        if (photoWorkflow is null || !CanRevalidateUnknownItems) return Task.CompletedTask;
        var requestId = parameter is Guid requested && requested != Guid.Empty
            ? requested
            : ActionRecoveryRecords.FirstOrDefault(record => record.HasUnknown)?.RequestId;
        return requestId is { } value
            ? photoWorkflow.RevalidateUnknownAsync(value, CancellationToken.None)
            : Task.CompletedTask;
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
            SetReviewAnnouncement("Final-survivor override requires an explicitly focused invoking control.");
            return;
        }

        var dialogResult = await reviewDialog.ShowSurvivorOverrideAsync(
            proposal, invokingFocus, CancellationToken.None);
        if (dialogResult != SurvivorOverrideDialogResult.Affirm) return;
        var affirmation = new OverrideAffirmationCommand(
            proposal, new OverrideEvidenceId(Guid.NewGuid()), DateTimeOffset.UtcNow);
        ApplyReviewResult(await workflow.AffirmReviewOverrideAsync(affirmation, CancellationToken.None));
    }

    private async Task OpenReviewAsync()
    {
        if (photoWorkflow is not { } workflow) return;
        await workflow.OpenReviewAsync();
        if (workflow.Current.CurrentPhase != WorkflowPhase.Review)
            SetReviewAnnouncement(workflow.Current.Review.Status == ReviewStatus.Faulted
                    ? ReviewStatusText
                    : "Review could not open because the current Scan evidence is invalid, stale, or faulted.",
                failedOpen: true,
                failedScan: workflow.Current.Scan.Pin);
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
            ReviewCommandOutcome.Sealed => "Selection is sealed by explicit approval.",
            _ => null,
        };
        if (announcement is not null) SetReviewAnnouncement(announcement);
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
            && photoSnapshot?.ActionRecovery.Status != ActionRecoveryStatus.Faulted
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
        ClearActiveReviewPreview();
        chooseScopeWorkflow.SnapshotChanged -= OnSnapshotChanged;
        if (photoWorkflow is not null)
            photoWorkflow.SnapshotChanged -= OnPhotoSnapshotChanged;
        var previewSource = Interlocked.Exchange(ref reviewPreviewSource, null);
        try
        {
            if (photoWorkflow is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (previewSource is not null)
                await previewSource.DisposeAsync().ConfigureAwait(false);
        }
    }
}
