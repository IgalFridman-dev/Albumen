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
    private IReviewPreviewSource? reviewThumbnailSource;
    private (ImmutableArray<ScanFileOccurrence> Source, Dictionary<long, ScanFileOccurrence>? ById) occurrenceIndex;
    private SuggestedBest? activeReviewSuggestion;
    private Guid? suggestionGroupId;
    private CancellationTokenSource? suggestionCancellation;
    private ScanOccurrenceViewModel? comparedReviewMember;
    private PreviewItemViewModel? comparedReviewPreview;
    private Guid? comparedGroupId;
    private CancellationTokenSource? comparePreviewCancellation;
    private long comparePreviewRequest;
    private PhotoSiftSnapshot? photoSnapshot;
    private ShellSnapshot? currentSnapshot;
    private ImmutableArray<PhaseItemViewModel> phases = [];
    private ImmutableArray<ScopeSummaryItemViewModel> scopeSummary = [];
    private ImmutableArray<string> blockingReasons = [];
    private string blockingNoticeAutomationName = "Folder can't be used";
    private string instruction = string.Empty;
    private string scopeStatus = string.Empty;
    private string folderStatusSentence = string.Empty;
    private string folderName = string.Empty;
    private string subfoldersCaption = string.Empty;
    private string selectedPath = string.Empty;
    private string resolvedPath = string.Empty;
    private string supportedCount = string.Empty;
    private string scanFolderText = "Scan folder";
    private string ruleSetStatus = string.Empty;
    private bool includeSubfolders;
    private bool canChangeRecursion;
    private bool isChecking;
    private bool isReady;
    private bool isBlocked;
    private bool isCanceled;
    private string selectionSummary = "0 selected occurrences.";
    private string reviewSelectionBarText = "Nothing marked for removal yet";
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
    private string approvalTitle = "No photos marked for removal yet";
    private ImmutableArray<ScanOccurrenceViewModel> approvalMarkedPhotos = [];
    private string approvalMoreMarkedText = string.Empty;
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
        ScanFolderCommand = new AsyncCommand(ScanFolderAsync);
        CancelScanCommand = new AsyncCommand(() => photoWorkflow?.CancelScanAsync() ?? Task.CompletedTask);
        OpenReviewCommand = new AsyncCommand(OpenReviewAsync);
        ContinueReviewCommand = new AsyncCommand(
            () => photoWorkflow?.ContinueReviewAsync() ?? Task.CompletedTask,
            canExecute: () => CanContinueReview);
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
        ShowPreviousReviewMemberCommand = new AsyncCommand(() => ShowAdjacentReviewMemberAsync(-1),
            allowConcurrentExecutions: true, canExecute: () => AdjacentReviewMember(-1) is not null);
        ShowNextReviewMemberCommand = new AsyncCommand(() => ShowAdjacentReviewMemberAsync(1),
            allowConcurrentExecutions: true, canExecute: () => AdjacentReviewMember(1) is not null);
        CompareReviewMemberCommand = new AsyncCommand(CompareReviewMemberAsync,
            allowConcurrentExecutions: true);
        CloseCompareCommand = new AsyncCommand(() =>
        {
            ClearComparedReviewPreview();
            return Task.CompletedTask;
        }, canExecute: () => ComparedReviewMember is not null);
        NavigateCommand = new AsyncCommand(parameter => photoWorkflow is not null && parameter is WorkflowPhase phase
            ? photoWorkflow.NavigateAsync(phase) : parameter is WorkflowPhase.ChooseScope
                ? this.chooseScopeWorkflow.HandleAsync(new ChooseScopeCommand(), CancellationToken.None) : Task.CompletedTask);
        this.chooseScopeWorkflow.SnapshotChanged += OnSnapshotChanged;
        Apply(this.chooseScopeWorkflow.Current);
    }

    public ShellViewModel(IChooseScopeWorkflow scope, IPhotoSiftWorkflow workflow,
        IReviewDialogService? reviewDialog = null,
        ILocalScanDataControl? localScanDataControl = null,
        IReviewPreviewSource? reviewPreviewSource = null,
        IReviewPreviewSource? reviewThumbnailSource = null) : this(scope)
    {
        photoWorkflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
        this.reviewDialog = reviewDialog;
        this.localScanDataControl = localScanDataControl;
        this.reviewPreviewSource = reviewPreviewSource;
        this.reviewThumbnailSource = reviewThumbnailSource;
        if (reviewThumbnailSource is not null)
            ReviewThumbnails = new ReviewThumbnailLoader(reviewThumbnailSource, ResolveOccurrence);
        workflow.SnapshotChanged += OnPhotoSnapshotChanged;
        ApplyCombined(workflow.Current);
    }

    public AsyncCommand ContinueCommand { get; }
    public AsyncCommand ChangeNearDuplicateModeCommand { get; }
    public AsyncCommand ConfirmExactRulesCommand { get; }
    public AsyncCommand StartScanCommand { get; }
    public AsyncCommand ScanFolderCommand { get; }
    public AsyncCommand CancelScanCommand { get; }
    public AsyncCommand OpenReviewCommand { get; }
    public AsyncCommand ContinueReviewCommand { get; }
    public AsyncCommand ApproveReviewCommand { get; }
    public AsyncCommand RevalidateUnknownCommand { get; }
    public AsyncCommand RequestClearScanDataCommand { get; }
    public AsyncCommand ConfirmClearScanDataCommand { get; }
    public AsyncCommand CancelClearScanDataCommand { get; }
    public AsyncCommand OpenReviewCategoryCommand { get; }
    public AsyncCommand OpenReviewGroupCommand { get; }
    public AsyncCommand OpenReviewMemberCommand { get; }
    public AsyncCommand ReviewBackCommand { get; }
    public AsyncCommand ShowPreviousReviewMemberCommand { get; }
    public AsyncCommand ShowNextReviewMemberCommand { get; }
    public AsyncCommand CompareReviewMemberCommand { get; }
    public AsyncCommand CloseCompareCommand { get; }
    public ReviewThumbnailLoader? ReviewThumbnails { get; }
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
    // One screen covers the folder and the scan options (PM-009 PR 3c).
    public bool IsChooseFolderPhase => IsChooseScopePhase || IsConfirmRulesPhase;
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
    public int ActionTotalCount => CurrentAction.Items.Length;
    public int ActionDoneCount => CurrentAction.Items.Count(item => item.State is not
        (ActionItemState.Waiting or ActionItemState.Requested or ActionItemState.Executing));
    public bool HasActionItems => ActionTotalCount != 0;
    private bool IsActionSettled => CurrentAction.Status is ActionStatus.Complete or ActionStatus.Incomplete
        or ActionStatus.Faulted;
    public string ActionHeadline
    {
        get
        {
            var action = CurrentAction;
            if (action.RequestId is null) return "Recycle Bin cleanup";
            if (!IsActionSettled) return $"Moving {PhotoCount(action.Items.Length)} to the Recycle Bin";
            if (action.Status == ActionStatus.Faulted) return "Cleanup stopped";
            var moved = action.Items.Count(item => item.State == ActionItemState.Recycled);
            return moved == action.Items.Length
                ? "Cleanup finished"
                : string.Create(CultureInfo.InvariantCulture, $"Cleanup finished: {moved} of {action.Items.Length} moved");
        }
    }
    public string ActionProgressText
    {
        get
        {
            var items = CurrentAction.Items;
            var moved = items.Count(item => item.State == ActionItemState.Recycled);
            var skipped = items.Count(item => item.State is ActionItemState.NotRemoved or ActionItemState.NotRequested
                or ActionItemState.InterruptedBeforeExecution);
            var unknown = items.Count(item => item.State == ActionItemState.UnknownPendingRevalidation);
            var text = string.Create(CultureInfo.InvariantCulture,
                $"{ActionDoneCount} of {items.Length} done · {moved} moved · {skipped} skipped");
            return unknown == 0 ? text : string.Create(CultureInfo.InvariantCulture, $"{text} · {unknown} unknown");
        }
    }
    // Shown once the cleanup has settled: what happened, in plain words.
    public string ActionOutcomeText
    {
        get
        {
            var action = CurrentAction;
            if (action.RequestId is null || !IsActionSettled) return string.Empty;
            var moved = action.Items.Count(item => item.State == ActionItemState.Recycled);
            var unknown = action.Items.Count(item => item.State is ActionItemState.UnknownPendingRevalidation
                or ActionItemState.Executing or ActionItemState.Requested);
            var notMoved = action.Items.Length - moved - unknown;
            if (unknown > 0)
                return $"PhotoSift can't tell whether {PhotoCount(unknown)} moved. {(unknown == 1 ? "It needs" : "They need")} a later check, which PhotoSift offers in History the next time it starts. Nothing is retried automatically.";
            if (notMoved == 0)
                return $"{PhotoCount(moved)} {(moved == 1 ? "is" : "are")} in the Windows Recycle Bin. If you need one back, restore it from there.";
            return moved == 0
                ? "Nothing was moved. Each photo below says why."
                : $"{PhotoCount(moved)} moved to the Windows Recycle Bin and {PhotoCount(notMoved)} left where {(notMoved == 1 ? "it is" : "they are")}. Each photo below says why.";
        }
    }
    public bool HasActionOutcome => ActionOutcomeText.Length != 0;
    public ActionResultTone ActionOutcomeTone => CurrentAction.Items.Any(item => item.State is
            ActionItemState.UnknownPendingRevalidation or ActionItemState.Executing or ActionItemState.Requested)
        ? ActionResultTone.Warning
        : CurrentAction.Items.All(item => item.State == ActionItemState.Recycled)
            ? ActionResultTone.Done : ActionResultTone.Skipped;
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
    public bool HasNoActionRecords => photoSnapshot?.ActionRecovery.Status == ActionRecoveryStatus.Ready
        && ActionRecoveryRecords.Count == 0;
    public bool HasActionRecoveryDisclosure => photoSnapshot?.ActionRecovery.Records.Any(record =>
        record.Summary.Certainty == ActionRecordCertainty.HasUnknown) is true;
    public string ActionRecoveryDisclosure { get; } = "PhotoSift found an unfinished cleanup from a previous session. The outcome of one or more items is unknown. Revalidate their current file state. Revalidation will not remove files.";
    public string ActionRecoveryAnnouncement => actionRecoveryAnnouncement;
    public string ActionRecoveryStatusText => photoSnapshot?.ActionRecovery.Status switch
    {
        ActionRecoveryStatus.Loading => "Loading History…",
        ActionRecoveryStatus.Faulted =>
            "Local data could not be safely loaded. PhotoSift will not modify files until the database is available.",
        _ => string.Empty,
    };
    public bool HasActionRecoveryStatus => ActionRecoveryStatusText.Length != 0;
    public bool IsLocalDataFaulted => photoSnapshot?.ActionRecovery.Status == ActionRecoveryStatus.Faulted;
    public bool CanRevalidateUnknownItems => photoSnapshot?.ActionRecovery is
        { Status: ActionRecoveryStatus.Ready, ProbeOutstanding: false }
        && HasActionRecoveryDisclosure;
    // PM-013: a finished cleanup is not the end of the session. Its review can
    // continue while it is still the live review of the current scan; a cleanup
    // that stopped with an error offers only a fresh scan.
    public bool CanContinueReview => photoSnapshot is { } snapshot
        && snapshot.Action.Status is ActionStatus.Complete or ActionStatus.Incomplete
        && snapshot.Action.RequestId is { } requestId
        && snapshot.Review is { Sealed: true, Pin: { } pin, ApprovedRequest: { } approved }
        && approved.RequestId == requestId
        && snapshot.Review.Status != ReviewStatus.Faulted
        && pin.Scan == snapshot.Scan.Pin
        && !IsScanBusy;
    public string ContinueReviewHint => photoSnapshot?.Action.Status == ActionStatus.Faulted
        ? "The cleanup stopped with an error, so reviewing can't continue from here. Scan again to start over."
        : string.Empty;
    public bool HasContinueReviewHint => ContinueReviewHint.Length != 0;
    public bool CanContinue => photoSnapshot?.Scope.ScopeState == ScopeSessionState.Ready;
    public bool CanChangeScanOptions => CanContinue && IsChooseFolderPhase && !IsScanBusy;
    public bool CanScanFolder => CanChangeScanOptions && photoSnapshot is { Options.IsValid: true };
    public bool CanConfirmExactRules => CanChangeScanOptions && IsConfirmRulesPhase
        && photoSnapshot is { Rules: null, Options.IsValid: true };
    public bool CanStartScan => photoSnapshot is { } snapshot && ShellPhasePolicy.CanStart(snapshot.Scope, snapshot.Rules) && !IsScanBusy;
    public bool IsScanBusy => photoSnapshot?.Scan is
        { HasUnsettledWork: true } or
        { Status: ScanSessionStatus.Checking or ScanSessionStatus.Running };
    public bool CanOpenReview => photoSnapshot?.Scan.Pin is not null;
    public bool NearDuplicatesEnabled => photoSnapshot?.Options.Near == NearDuplicateMode.Conservative;
    public string ExactPreset { get; } = "Always included. Copies must match byte for byte.";
    public string ScanOptionsGuidance => photoSnapshot?.Options is { } options
        ? options.IsValid
            ? (options.Near == NearDuplicateMode.Conservative
                ? "Identical copies are always included. Look-alikes use one careful, fixed setting, and close calls are shown as Maybe similar. Nothing is marked for you."
                : "Identical copies are always included. Look-alikes are off for the next scan. Nothing is marked for you.")
              + (photoSnapshot.Scan.Pin is not null
                ? " Changing an option clears the current results and your marks, and you scan again."
                : string.Empty)
            : options.ValidationGuidance
        : "Identical copies are always included. Look-alikes are on unless you turn them off. Nothing is marked for you.";
    // The guidance is a live region, so it is shown only when it has something to say:
    // invalid options, or the warning that changing an option clears the current results.
    public bool HasScanOptionsGuidance => photoSnapshot is { } snapshot
        && (!snapshot.Options.IsValid || snapshot.Scan.Pin is not null);
    public string RecursionSummary => IncludeSubfolders ? "Include subfolders" : "Top-level folder only";
    // Announced as a live region, so it changes only when the stage changes, never per photo.
    public string ScanStatus => photoSnapshot is { Scan: var scan } snapshot
        ? scan.Status switch
        {
            ScanSessionStatus.Idle => "Not started",
            ScanSessionStatus.Checking => "Getting ready: checking the folder",
            ScanSessionStatus.Running => scan.Enumeration.State is ScanStageState.Pending or ScanStageState.Running
                ? "Scanning: finding photos"
                : scan.Counts.Outstanding > 0 ? "Scanning: reading photos"
                : IsComparingLookAlikes(snapshot) ? "Scanning: comparing look-alikes" : "Scanning: finishing up",
            ScanSessionStatus.Complete => "Finished: every file in the folder was included",
            ScanSessionStatus.Incomplete => "Stopped early: some files weren't checked",
            ScanSessionStatus.Faulted => "Stopped by an error: results may be incomplete",
            _ => throw new InvalidOperationException($"Unknown Scan status: {scan.Status}"),
        }
        : "Not started";
    public ReviewStatus ReviewState => photoSnapshot?.Review.Status ?? ReviewStatus.Closed;
    public ReviewUnavailableReason ReviewFaultReason => photoSnapshot?.Review.FaultReason ?? ReviewUnavailableReason.None;
    public string ReviewStatusText => ReviewState == ReviewStatus.Faulted
        ? $"Review stopped after a fault ({ReviewFaultReason}), so its controls are unavailable."
        : ReviewState switch
        {
            ReviewStatus.Ready => "Ready to review",
            ReviewStatus.Revalidating => "Checking the photos again…",
            _ => "Review is closed",
        };
    // PM-013: what a continued review leaves out. The removed photos are the scan's
    // set members that the review no longer holds; closed sets are hidden below.
    public string ReviewContinuationText
    {
        get
        {
            if (photoSnapshot is not { } snapshot || snapshot.Review.Pin is not { } pin
                || pin.Scan != snapshot.Scan.Pin) return string.Empty;
            var reviewed = snapshot.Review.Groups.SelectMany(group => group.Members)
                .Select(member => member.Occurrence.Token.OccurrenceId).ToHashSet();
            var removed = snapshot.Review.Groups.SelectMany(group => group.Candidate.Members)
                .Select(member => member.Token.OccurrenceId)
                .Where(id => !reviewed.Contains(id)).Distinct().LongCount();
            var closed = snapshot.Review.Groups.LongCount(group => group.Closed);
            if (removed == 0 && closed == 0) return string.Empty;
            return $"Continued after the cleanup: {PhotoCount(removed)} removed, {SetCount(closed)} closed until the next scan.";
        }
    }
    public bool HasReviewContinuation => ReviewContinuationText.Length != 0;
    public string ScanCountsText => photoSnapshot is { Scan.Counts: var count }
        ? FormatScanCounts(count) : string.Empty;
    public string CoverageStatus => photoSnapshot?.Scan.Status == ScanSessionStatus.Complete
        ? "Scan complete: every file in the folder was included. Unsupported, failed, and canceled files are listed separately."
        : "Partial results: the scan is incomplete, so these results don't cover every file in the folder.";
    public string ReviewDisclaimer { get; } = "Results show your photos as they were when scanned, so scan again after files change. Results aren't restored after PhotoSift closes.";
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
    public SuggestedBest? ActiveReviewSuggestion
    {
        get => activeReviewSuggestion;
        private set => SetField(ref activeReviewSuggestion, value);
    }
    public ScanOccurrenceViewModel? ComparedReviewMember
    {
        get => comparedReviewMember;
        private set => comparedReviewMember = value;
    }
    public PreviewItemViewModel? ComparedReviewPreview
    {
        get => comparedReviewPreview;
        private set => comparedReviewPreview = value;
    }
    public bool IsComparingReviewMembers => ComparedReviewMember is not null;
    public string ReviewResultsSummary
    {
        get
        {
            var sets = ReviewCategories.Where(category => category.Category != ReviewCategoryKind.CouldNotAnalyze)
                .Sum(category => category.GroupCount);
            var unreadable = ReviewCategories.Where(category => category.Category == ReviewCategoryKind.CouldNotAnalyze)
                .Sum(category => category.GroupCount);
            var summary = sets switch
            {
                0 => "No copies or look-alikes found",
                1 => "1 set to review",
                _ => $"{sets.ToString(CultureInfo.InvariantCulture)} sets to review",
            };
            return unreadable switch
            {
                0 => summary,
                1 => $"{summary} · 1 file couldn't be checked",
                _ => $"{summary} · {unreadable.ToString(CultureInfo.InvariantCulture)} files couldn't be checked",
            };
        }
    }

    public string SelectionSummary
    {
        get => selectionSummary;
        private set => SetField(ref selectionSummary, value);
    }

    public string ReviewSelectionBarText
    {
        get => reviewSelectionBarText;
        private set => SetField(ref reviewSelectionBarText, value);
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

    public string ApprovalTitle
    {
        get => approvalTitle;
        private set => SetField(ref approvalTitle, value);
    }

    // The first few marked photos, shown with thumbnails above the approval button.
    public ImmutableArray<ScanOccurrenceViewModel> ApprovalMarkedPhotos
    {
        get => approvalMarkedPhotos;
        private set
        {
            approvalMarkedPhotos = value;
            OnPropertyChanged();
        }
    }

    public string ApprovalMoreMarkedText
    {
        get => approvalMoreMarkedText;
        private set
        {
            SetField(ref approvalMoreMarkedText, value);
            OnPropertyChanged(nameof(HasMoreMarkedPhotos));
        }
    }

    public bool HasMoreMarkedPhotos => ApprovalMoreMarkedText.Length != 0;

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
        private set
        {
            SetField(ref phases, value);
            OnPropertyChanged(nameof(RailSteps));
        }
    }

    // Scan options share the folder screen, so the step list shows one "Choose folder" step for
    // both phases and marks it current while either one is. Phases keeps all five.
    public ImmutableArray<PhaseItemViewModel> RailSteps
    {
        get
        {
            var optionsCurrent = phases.Any(phase => phase.Phase == WorkflowPhase.ConfirmRules && phase.IsCurrent);
            return phases
                .Where(phase => phase.Phase != WorkflowPhase.ConfirmRules)
                .Select((phase, index) => phase with
                {
                    Availability = phase.Phase == WorkflowPhase.ChooseScope && optionsCurrent
                        ? PhaseAvailability.Current
                        : phase.Availability,
                    StepNumber = index + 1,
                })
                .ToImmutableArray();
        }
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

    // The one sentence in the folder card's status strip. It is set before ScopeStatus so
    // the live-region announcement queued on ScopeStatus already reads the new sentence.
    public string FolderStatusSentence
    {
        get => folderStatusSentence;
        private set => SetField(ref folderStatusSentence, value);
    }

    public string FolderName
    {
        get => folderName;
        private set => SetField(ref folderName, value);
    }

    public string SubfoldersCaption
    {
        get => subfoldersCaption;
        private set => SetField(ref subfoldersCaption, value);
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

    public string ScanFolderText
    {
        get => scanFolderText;
        private set => SetField(ref scanFolderText, value);
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
        var previousGuidance = ScanOptionsGuidance;
        var previousHasGuidance = HasScanOptionsGuidance;
        Apply(snapshot.Scope);
        if (snapshot.IsRecheckingScopeAfterCleanup && IsChecking)
        {
            FolderStatusSentence = "Checking the folder again after the cleanup.";
            ScopeStatus = "Checking the folder again after the cleanup";
        }
        photoSnapshot = snapshot;
        if (IsScanBusy && IsClearScanDataConfirmationPending)
            RetainScanDataForActiveScan();
        ProjectAction(previousAction, previousActionReview, snapshot);
        ProjectActionRecovery(previousRecovery, snapshot.ActionRecovery);
        Phases = Project(snapshot.Phases);
        RuleSetStatus = snapshot.Rules is null
            ? "not confirmed yet (Scan confirms them)"
            : snapshot.Rules.Options.Near == NearDuplicateMode.Conservative
                ? "identical copies and look-alikes, confirmed"
                : "identical copies only, confirmed";
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
        foreach (var name in new[] { nameof(IsChooseScopePhase), nameof(IsConfirmRulesPhase), nameof(IsChooseFolderPhase), nameof(IsScanPhase), nameof(IsReviewPhase), nameof(IsActionPhase),
            nameof(CanContinue), nameof(CanStartScan), nameof(CanScanFolder), nameof(CanOpenReview),
            nameof(RecursionSummary), nameof(ScanCountsText), nameof(CoverageStatus), nameof(CouldNotAnalyze),
            nameof(ScanOutcomeSummaries), nameof(CanContinueReview), nameof(ContinueReviewHint),
            nameof(HasContinueReviewHint), nameof(ReviewContinuationText), nameof(HasReviewContinuation) })
            OnPropertyChanged(name);
        ContinueReviewCommand.NotifyCanExecuteChanged();
        if (previousOptions != snapshot.Options) OnPropertyChanged(nameof(NearDuplicatesEnabled));
        if (previousGuidance != ScanOptionsGuidance) OnPropertyChanged(nameof(ScanOptionsGuidance));
        if (previousHasGuidance != HasScanOptionsGuidance) OnPropertyChanged(nameof(HasScanOptionsGuidance));
        if (!preserveReviewProjection)
        {
            OnPropertyChanged(nameof(ExactGroups));
            OnPropertyChanged(nameof(ReviewCategories));
            OnPropertyChanged(nameof(ReviewResultsSummary));
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
            : snapshot.Review.Groups.Where(group => !group.Closed).Select((group, index) => new ExactGroupViewModel(group,
                snapshot.Review.Status == ReviewStatus.Ready && !snapshot.Review.Sealed,
                snapshot.Review.SelectionRevision, index + 1, snapshot.Review.Groups.Count(open => !open.Closed),
                (kind, ids) => ChangeReviewSelectionAsync(group.Candidate.GroupId, kind, ids)))
                .ToImmutableArray();
    }

    private ImmutableArray<ReviewCategoryViewModel> ProjectReviewCategories(PhotoSiftSnapshot snapshot)
    {
        var setNames = ProjectSetNames(snapshot.Review);
        string DescribeSet(Guid groupId) => setNames.TryGetValue(groupId, out var name) ? name : "another set";
        return
        [
            ProjectCandidateCategory(snapshot, ReviewCategoryKind.Exact, 1, DescribeSet),
            ProjectCandidateCategory(snapshot, ReviewCategoryKind.Near, 2, DescribeSet),
            ProjectCandidateCategory(snapshot, ReviewCategoryKind.Uncertain, 3, DescribeSet),
            ProjectCouldNotAnalyzeCategory(snapshot.Scan, 4),
        ];
    }

    // "Look-alike set 2": the name a photo's mark is reported under in its other sets.
    private static ImmutableDictionary<Guid, string> ProjectSetNames(ReviewSnapshot review)
    {
        var names = ImmutableDictionary.CreateBuilder<Guid, string>();
        foreach (var category in new[] { ReviewCategoryKind.Exact, ReviewCategoryKind.Near, ReviewCategoryKind.Uncertain })
        {
            var position = 0;
            foreach (var group in review.Groups.Where(group => !group.Closed && InCategory(group.Candidate, category)))
                names[group.Candidate.GroupId] =
                    $"{ReviewCategoryNames.For(category)} set {(++position).ToString(CultureInfo.InvariantCulture)}";
        }
        return names.ToImmutable();
    }

    private ReviewCategoryViewModel ProjectCandidateCategory(
        PhotoSiftSnapshot snapshot,
        ReviewCategoryKind category,
        int positionInSet,
        Func<Guid, string> describeSet)
    {
        var name = ReviewCategoryNames.For(category);
        if (snapshot.Review.Pin is not null)
        {
            // A set closed after a cleanup (PM-013) is left out; ReviewContinuationText counts it.
            var groups = snapshot.Review.Groups
                .Where(group => !group.Closed && InCategory(group.Candidate, category))
                .ToImmutableArray();
            return new(category, name, groups.Length, positionInSet, 4, index =>
                new ReviewGroupViewModel(
                    groups[index],
                    category,
                    index + 1,
                    groups.Length,
                    snapshot.Review.Status == ReviewStatus.Ready && !snapshot.Review.Sealed,
                    (kind, ids) => ChangeReviewSelectionAsync(groups[index].Candidate.GroupId, kind, ids),
                    describeSet),
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
        return new(ReviewCategoryKind.CouldNotAnalyze, ReviewCategoryNames.For(ReviewCategoryKind.CouldNotAnalyze), rows.Length,
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
        if (level != ReviewLevel.Comparisons || group?.GroupId != comparedGroupId
            || member is not null && member.OccurrenceId == ComparedReviewMember?.OccurrenceId)
            ClearComparedReviewPreview();
        else if (ComparedReviewMember is { } compared && group?.FindMember(compared.OccurrenceId) is { } current)
            ComparedReviewMember = current;
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
        OnPropertyChanged(nameof(ComparedReviewMember));
        ReviewBackCommand.NotifyCanExecuteChanged();
        ShowPreviousReviewMemberCommand.NotifyCanExecuteChanged();
        ShowNextReviewMemberCommand.NotifyCanExecuteChanged();
        RefreshActiveSuggestion(group);
    }

    private ScanOccurrenceViewModel? AdjacentReviewMember(int offset)
    {
        if (ActiveReviewGroup is not { } group || ActiveReviewMember is not { } member) return null;
        var index = member.PositionInSet - 1 + offset;
        return index >= 0 && index < group.MemberCount ? group.Members[index] : null;
    }

    private Task ShowAdjacentReviewMemberAsync(int offset) =>
        AdjacentReviewMember(offset) is { } next ? OpenReviewMemberAsync(next) : Task.CompletedTask;

    private async Task CompareReviewMemberAsync(object? parameter)
    {
        if (parameter is not ScanOccurrenceViewModel member || ActiveReviewGroup is not { } group
            || group.FindMember(member.OccurrenceId) is null)
            throw new ArgumentException("Comparison requires a member of the active group.", nameof(parameter));
        if (ActiveReviewMember is null)
        {
            await OpenReviewMemberAsync(member);
            return;
        }
        if (ActiveReviewMember.OccurrenceId == member.OccurrenceId) return;

        ClearComparedReviewPreview();
        ComparedReviewMember = member;
        comparedGroupId = group.GroupId;
        OnPropertyChanged(nameof(ComparedReviewMember));
        OnPropertyChanged(nameof(IsComparingReviewMembers));
        CloseCompareCommand.NotifyCanExecuteChanged();
        var source = reviewPreviewSource;
        if (source is null || ResolveOccurrence(member.OccurrenceId) is not { } target) return;

        var cancellation = new CancellationTokenSource();
        var cancellationToken = cancellation.Token;
        comparePreviewCancellation = cancellation;
        var request = Interlocked.Increment(ref comparePreviewRequest);
        ReviewPreviewResult? result = null;
        try
        {
            try
            {
                result = await source.LoadAsync(target.Context, target.Occurrence, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                result = ReviewPreviewResult.Failure(target.Occurrence.ResolvedPath,
                    PreviewAvailability.Unavailable, "Preview unavailable: the file could not be read.");
            }
            if (request != Volatile.Read(ref comparePreviewRequest)
                || ComparedReviewMember?.OccurrenceId != member.OccurrenceId)
                return;
            ComparedReviewPreview = new PreviewItemViewModel(result);
            OnPropertyChanged(nameof(ComparedReviewPreview));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            result?.Dispose();
            if (ReferenceEquals(comparePreviewCancellation, cancellation))
                comparePreviewCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ClearComparedReviewPreview()
    {
        Interlocked.Increment(ref comparePreviewRequest);
        var cancellation = Interlocked.Exchange(ref comparePreviewCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        var wasComparing = ComparedReviewMember is not null || ComparedReviewPreview is not null;
        ComparedReviewMember = null;
        ComparedReviewPreview = null;
        comparedGroupId = null;
        if (!wasComparing) return;
        OnPropertyChanged(nameof(ComparedReviewMember));
        OnPropertyChanged(nameof(ComparedReviewPreview));
        OnPropertyChanged(nameof(IsComparingReviewMembers));
        CloseCompareCommand.NotifyCanExecuteChanged();
    }

    // The "Suggested best" badge needs every photo's pixel size, which only a decode gives,
    // so it appears once the set's thumbnails have loaded. It is a hint, never a Selection.
    private void RefreshActiveSuggestion(ReviewGroupViewModel? group)
    {
        if (group?.GroupId == suggestionGroupId && group is not null) return;
        var cancellation = Interlocked.Exchange(ref suggestionCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        suggestionGroupId = group?.GroupId;
        ActiveReviewSuggestion = null;
        if (group is null || ReviewThumbnails is not { } thumbnails
            || group.Category is not (ReviewCategoryKind.Near or ReviewCategoryKind.Uncertain)
            || group.MemberCount is < 2 or > SuggestedBestPhoto.MaximumSetSize)
            return;

        var next = new CancellationTokenSource();
        suggestionCancellation = next;
        _ = LoadSuggestionAsync(group, thumbnails, next.Token);
    }

    private async Task LoadSuggestionAsync(
        ReviewGroupViewModel group,
        ReviewThumbnailLoader thumbnails,
        CancellationToken cancellationToken)
    {
        var facts = new List<PhotoFacts>(group.MemberCount);
        for (var index = 0; index < group.MemberCount; index++)
        {
            var member = group.Members[index];
            var thumbnail = await thumbnails.LoadAsync(member, cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || thumbnail is not { PixelWidth: { } width, PixelHeight: { } height })
                return;
            facts.Add(new(member.OccurrenceId, width, height, member.ByteCount));
        }
        if (!cancellationToken.IsCancellationRequested && suggestionGroupId == group.GroupId)
            ActiveReviewSuggestion = SuggestedBestPhoto.Pick(facts);
    }

    private (ScanRunContext Context, ScanFileOccurrence Occurrence)? ResolveOccurrence(long occurrenceId)
    {
        var snapshot = photoSnapshot;
        if (snapshot?.Scope.Preflight?.Scope is not { } scope || snapshot.Scan.Pin is not { } pin)
            return null;
        var occurrences = snapshot.Scan.Occurrences;
        if (occurrences.IsDefault) return null;
        var byId = occurrenceIndex.ById;
        if (byId is null || occurrenceIndex.Source != occurrences)
        {
            byId = new Dictionary<long, ScanFileOccurrence>(occurrences.Length);
            foreach (var row in occurrences) byId.TryAdd(row.Token.OccurrenceId, row);
            occurrenceIndex = (occurrences, byId);
        }
        return byId.TryGetValue(occurrenceId, out var occurrence)
            ? (new ScanRunContext(pin, scope), occurrence)
            : null;
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
            ReviewCategoryKind.Exact => "The files are identical, byte for byte.",
            ReviewCategoryKind.Near or ReviewCategoryKind.Uncertain when similarity is not null =>
                $"Picture difference {similarity.HammingDistance.ToString(CultureInfo.InvariantCulture)}, color difference {similarity.ColorDistance.ToString(CultureInfo.InvariantCulture)} (lower is closer)",
            _ => unavailable,
        };
        var uncertainty = category == ReviewCategoryKind.Uncertain
            ? "A close call. Check these photos yourself."
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
            ReviewCategoryNames.For(category),
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
            nameof(ActionCertaintyText), nameof(ActionSelectionText), nameof(SourceMutationNotice),
            nameof(ActionTotalCount), nameof(ActionDoneCount), nameof(HasActionItems), nameof(ActionHeadline),
            nameof(ActionProgressText), nameof(ActionOutcomeText), nameof(HasActionOutcome), nameof(ActionOutcomeTone) })
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
        foreach (var property in new[] { nameof(ActionRecoveryRecords), nameof(HasActionRecoveryRecords), nameof(HasNoActionRecords),
            nameof(HasActionRecoveryDisclosure), nameof(ActionRecoveryStatusText), nameof(HasActionRecoveryStatus),
            nameof(IsLocalDataFaulted), nameof(CanRevalidateUnknownItems) })
            OnPropertyChanged(property);
        var active = ActionRecoveryRecords.SelectMany(record => record.Items).FirstOrDefault(item => item.IsVerifying);
        actionRecoveryAnnouncement = active is not null
            ? $"{active.OriginalPath}. {active.StatusText}"
            : HasActionRecoveryDisclosure
                ? "History updated. Some outcomes are unknown pending revalidation."
                : HasActionRecoveryRecords ? "History updated." : string.Empty;
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
            SetReviewAnnouncement("Removing the last kept photo of a set needs your confirmation, which couldn't be shown, so nothing changed.");
            return;
        }

        var dialogResult = await reviewDialog.ShowSurvivorOverrideAsync(
            proposal, invokingFocus, CancellationToken.None);
        if (dialogResult != SurvivorOverrideDialogResult.Affirm) return;
        var affirmation = new OverrideAffirmationCommand(
            proposal, new OverrideEvidenceId(Guid.NewGuid()), DateTimeOffset.UtcNow);
        ApplyReviewResult(await workflow.AffirmReviewOverrideAsync(affirmation, CancellationToken.None));
    }

    // The one Scan button on the folder screen: the same three owner-checked steps the
    // separate buttons ran (continue, confirm the options for this folder, start), pinned to
    // the folder revision and options on screen when it was clicked.
    private async Task ScanFolderAsync()
    {
        if (photoWorkflow is not { } workflow || !CanScanFolder || photoSnapshot is not { } seen) return;
        var seenRevision = seen.Scope.Revision;
        var seenOptions = seen.Options;
        bool StillSeen() => workflow.Current.Scope.Revision == seenRevision && workflow.Current.Options == seenOptions;
        if (workflow.Current.CurrentPhase == WorkflowPhase.ChooseScope) await workflow.ContinueAsync();
        if (workflow.Current.CurrentPhase != WorkflowPhase.ConfirmRules || !StillSeen()) return;
        if (workflow.Current.Rules is null) await workflow.ConfirmExactRulesAsync();
        if (!StillSeen() || workflow.Current.Rules is not { } rules
            || rules.ScopeRevision != seenRevision || rules.Options != seenOptions) return;
        await workflow.StartScanAsync();
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
            ReviewCommandOutcome.Stale => "The review changed in the meantime, so nothing was changed. Try again.",
            ReviewCommandOutcome.PersistenceFailed => "PhotoSift couldn't save your confirmation, so your marks are unchanged.",
            ReviewCommandOutcome.EvidenceUnavailable => "PhotoSift can't read your saved confirmation, so the cleanup can't be approved.",
            ReviewCommandOutcome.Sealed => "Your marks are locked because the cleanup was approved.",
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
        ApprovalTitle = selectedCount switch
        {
            0 => "No photos marked for removal yet",
            1 => "Move 1 photo to the Recycle Bin?",
            _ => $"Move {selectedCount.ToString(CultureInfo.InvariantCulture)} photos to the Recycle Bin?",
        };
        ProjectMarkedPhotos(selectedMembers);
        ApprovalAutomationStatus = $"{ApprovalSelectedOccurrenceText}; {ApprovalSelectedPathText}; {ApprovalSelectedPhysicalText}; {ApprovalRecoveryText} {ApprovalCoverageText}; {ApprovalUnresolvedText}; {ApprovalUnresolvedSubtotals}; {ApprovalUnselectedSurvivorsText}; {ApprovalDestinationText} {ApprovalRevalidationText}";
        SelectionSummary = $"{ApprovalSelectedOccurrenceText}; {ApprovalRecoveryText} {ApprovalCoverageText}; unresolved total {review.Unresolved.Total.ToString(CultureInfo.InvariantCulture)}.";
        ReviewSelectionBarText = FormatSelectionBar(review, selectedCount);
        CanApproveReview = review.Status == ReviewStatus.Ready
            && review.Coverage is ScanSessionStatus.Complete or ScanSessionStatus.Incomplete
            && selectedCount > 0 && !review.Sealed
            && photoSnapshot?.ActionRecovery.Status != ActionRecoveryStatus.Faulted
            && selectedMembers.All(member => member.Eligibility == ReviewEligibilityState.Eligible);
    }

    private static string FormatScanCounts(PhotoSift.Application.Scan.ScanCounts count)
    {
        var done = count.Discovered - count.Outstanding - count.Canceled;
        var text = $"{done.ToString("N0", CultureInfo.InvariantCulture)} of {count.Discovered.ToString("N0", CultureInfo.InvariantCulture)} files checked";
        if (count.Unsupported > 0) text += $" · {count.Unsupported.ToString("N0", CultureInfo.InvariantCulture)} unsupported";
        if (count.Failed > 0) text += $" · {count.Failed.ToString("N0", CultureInfo.InvariantCulture)} failed";
        if (count.Canceled > 0) text += $" · {count.Canceled.ToString("N0", CultureInfo.InvariantCulture)} canceled";
        return text;
    }

    // Exact work is counted as done before the look-alike pass, which runs afterwards
    // under the same Running status and leaves visual features pending until it reaches a photo.
    private static bool IsComparingLookAlikes(PhotoSiftSnapshot snapshot) =>
        snapshot.Rules?.Options.Near == NearDuplicateMode.Conservative
        && snapshot.Scan.Occurrences.Any(row => row.Exact.State == ScanStageState.Succeeded
            && (row.VisualFeatures.State is ScanStageState.Pending or ScanStageState.Running
                || row.NearDuplicate.State is ScanStageState.Pending or ScanStageState.Running));

    private static string PhotoCount(long count) => count == 1
        ? "1 photo"
        : $"{count.ToString(CultureInfo.InvariantCulture)} photos";

    private static string SetCount(long count) => count == 1
        ? "1 set"
        : $"{count.ToString(CultureInfo.InvariantCulture)} sets";

    private void ProjectMarkedPhotos(ReviewMemberSnapshot[] selectedMembers)
    {
        const int Shown = 6;
        var shown = selectedMembers.Take(Shown).ToArray();
        if (shown.Length != ApprovalMarkedPhotos.Length
            || shown.Where((member, index) => member.Occurrence.Token.OccurrenceId != ApprovalMarkedPhotos[index].OccurrenceId
                || member.Occurrence.DiscoveredPath != ApprovalMarkedPhotos[index].DiscoveredPath).Any())
            ApprovalMarkedPhotos = shown.Select(member => new ScanOccurrenceViewModel(member.Occurrence)).ToImmutableArray();
        var more = selectedMembers.LongLength - shown.Length;
        ApprovalMoreMarkedText = more switch
        {
            <= 0 => string.Empty,
            1 => "and 1 more photo",
            _ => $"and {more.ToString(CultureInfo.InvariantCulture)} more photos",
        };
    }

    private static string FormatSelectionBar(ReviewSnapshot review, long selectedCount)
    {
        if (selectedCount == 0) return "Nothing marked for removal yet";
        var marked = selectedCount == 1
            ? "1 photo marked for removal"
            : $"{selectedCount.ToString(CultureInfo.InvariantCulture)} photos marked for removal";
        var selectedGroups = review.Groups.Where(group => group.SelectedOccurrenceIds.Count > 0).ToArray();
        if (selectedGroups.Any(group => group.Recovery.Bytes is null)) return marked;
        try
        {
            var bytes = selectedGroups.Aggregate(0L,
                (sum, group) => checked(sum + group.Recovery.Bytes.GetValueOrDefault()));
            return bytes > 0 ? $"{marked} · about {ScanOccurrenceViewModel.FormatSize(bytes)} moves to the Recycle Bin" : marked;
        }
        catch (OverflowException)
        {
            return marked;
        }
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
        FolderName = ProjectFolderName(snapshot.Selection?.SelectedPath);
        SubfoldersCaption = snapshot.Selection is null
            ? "Also check folders inside the chosen folder."
            : $"Also check folders inside {FolderName}.";
        FolderStatusSentence = ProjectStatusSentence(
            snapshot.ScopeState, snapshot.Preflight?.CompleteCounts?.Supported);
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
        ScanFolderText = snapshot.Preflight?.CompleteCounts?.Supported switch
        {
            null => "Scan folder",
            0 => "Scan folder (no photos found yet)",
            1 => "Scan 1 photo",
            int count => $"Scan {count.ToString("N0", CultureInfo.InvariantCulture)} photos",
        };
        RuleSetStatus = snapshot.RuleSetStatus;
        ScopeSummary = ProjectSummary(snapshot.Preflight);
        BlockingReasons = ProjectBlockingReasons(snapshot.Preflight);
        BlockingNoticeAutomationName = BlockingReasons.IsDefaultOrEmpty
            ? "Folder can't be used"
            : BlockingReasons[0];
        IsChecking = snapshot.ScopeState is ScopeSessionState.Checking;
        IsReady = snapshot.ScopeState is ScopeSessionState.Ready;
        IsBlocked = snapshot.ScopeState is ScopeSessionState.Blocked;
        IsCanceled = snapshot.ScopeState is ScopeSessionState.Canceled;
        currentSnapshot = snapshot;
    }

    private static string ProjectFolderName(string? selectedPath)
    {
        if (string.IsNullOrEmpty(selectedPath)) return "No folder chosen yet";
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(selectedPath));
        return string.IsNullOrEmpty(name) ? selectedPath : name;
    }

    private static string ProjectStatusSentence(ScopeSessionState state, int? supported) =>
        state switch
        {
            ScopeSessionState.Cold => "Choose a folder to start.",
            ScopeSessionState.Checking => "Checking location and access.",
            ScopeSessionState.Ready => supported switch
            {
                null => "Ready to scan.",
                0 => "No JPEG or PNG photos were found in this folder.",
                1 => "Ready to scan. 1 JPEG or PNG photo found.",
                int count => $"Ready to scan. {count.ToString("N0", CultureInfo.InvariantCulture)} JPEG and PNG photos found.",
            },
            ScopeSessionState.Blocked => "This folder can't be scanned yet.",
            ScopeSessionState.Canceled => "The folder wasn't checked. Check it again or choose another folder.",
            _ => throw new InvalidOperationException($"Unknown Scope state: {state}"),
        };

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
            .Select((phase, index) => new PhaseItemViewModel(
                phase.Phase,
                phase.DisplayName,
                phase.Availability,
                index + 1))
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
        ClearComparedReviewPreview();
        RefreshActiveSuggestion(null);
        chooseScopeWorkflow.SnapshotChanged -= OnSnapshotChanged;
        if (photoWorkflow is not null)
            photoWorkflow.SnapshotChanged -= OnPhotoSnapshotChanged;
        var previewSource = Interlocked.Exchange(ref reviewPreviewSource, null);
        var thumbnailSource = Interlocked.Exchange(ref reviewThumbnailSource, null);
        try
        {
            if (photoWorkflow is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (previewSource is not null)
                    await previewSource.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (thumbnailSource is not null)
                    await thumbnailSource.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
