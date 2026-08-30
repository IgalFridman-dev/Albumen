using PhotoSift.Application.Scope;
using PhotoSift.Domain.Scope;
using PhotoSift.Domain.Workflow;

namespace PhotoSift.Application.Shell;

public sealed class ChooseScopeWorkflow : IChooseScopeWorkflow
{
    private const string RuleSetNotConfigured = "Not configured";
    private readonly object gate = new();
    private readonly ILocalFolderPicker folderPicker;
    private readonly ILocalScopePreflight preflight;
    private ShellSnapshot current;

    public ChooseScopeWorkflow(
        ILocalFolderPicker folderPicker,
        ILocalScopePreflight preflight)
    {
        this.folderPicker = folderPicker
            ?? throw new ArgumentNullException(nameof(folderPicker));
        this.preflight = preflight
            ?? throw new ArgumentNullException(nameof(preflight));
        current = new ShellSnapshot(
            InitialWorkflow.Create(),
            "Choose one local folder to scan.",
            ScopeSessionState.Cold,
            null,
            null,
            RuleSetNotConfigured,
            0);
    }

    public event EventHandler<ShellSnapshot>? SnapshotChanged;

    public ShellSnapshot Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public async Task HandleAsync(
        ChooseScopeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var selectedPath = await folderPicker
            .PickSingleAsync(cancellationToken)
            .ConfigureAwait(false);
        if (selectedPath is null)
        {
            return;
        }

        await RunPreflightAsync(
                new ScopeSelection(selectedPath, ScopeRecursion.TopLevelOnly),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task HandleAsync(
        ChangeScopeRecursionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ScopeSelection? selection;
        lock (gate)
        {
            selection = current.Selection;
        }

        if (selection is null)
        {
            return;
        }

        var recursion = command.IncludeSubfolders
            ? ScopeRecursion.IncludeSubfolders
            : ScopeRecursion.TopLevelOnly;
        if (selection.Recursion == recursion)
        {
            return;
        }

        await RunPreflightAsync(
                new ScopeSelection(selection.SelectedPath, recursion),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task HandleAsync(
        RetryScopePreflightCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        ScopeSelection selection;
        lock (gate)
        {
            selection = current.Selection
                ?? throw new InvalidOperationException(
                    "Scope preflight cannot be retried before a Scope is selected.");
        }

        await RunPreflightAsync(selection, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunPreflightAsync(
        ScopeSelection selection,
        CancellationToken cancellationToken)
    {
        var revision = BeginPreflight(selection);

        try
        {
            var evidence = await preflight
                .InspectAsync(selection, cancellationToken)
                .ConfigureAwait(false);
            var result = ScopePreflightPolicy.Evaluate(evidence);
            PublishIfCurrent(revision, CreateCompleted(selection, result, revision));
        }
        catch (OperationCanceledException)
        {
            PublishIfCurrent(revision, CreateCanceled(selection, revision));
        }
    }

    private long BeginPreflight(ScopeSelection selection)
    {
        ShellSnapshot checking;
        lock (gate)
        {
            var revision = current.Revision + 1;
            checking = CreateChecking(selection, revision);
            current = checking;
        }

        SnapshotChanged?.Invoke(this, checking);
        return checking.Revision;
    }

    private void PublishIfCurrent(long revision, ShellSnapshot completed)
    {
        lock (gate)
        {
            if (current.Revision != revision)
            {
                return;
            }

            current = completed;
        }

        SnapshotChanged?.Invoke(this, completed);
    }

    private static ShellSnapshot CreateChecking(
        ScopeSelection selection,
        long revision) =>
        new(
            InitialWorkflow.Create(),
            "Checking location and access.",
            ScopeSessionState.Checking,
            selection,
            null,
            RuleSetNotConfigured,
            revision);

    private static ShellSnapshot CreateCompleted(
        ScopeSelection selection,
        ScopePreflightResult result,
        long revision) =>
        new(
            InitialWorkflow.Create(),
            result.Decision is ScopePreflightDecision.Ready
                ? "Scope is ready."
                : "Scope is blocked.",
            result.Decision is ScopePreflightDecision.Ready
                ? ScopeSessionState.Ready
                : ScopeSessionState.Blocked,
            selection,
            result,
            RuleSetNotConfigured,
            revision);

    private static ShellSnapshot CreateCanceled(
        ScopeSelection selection,
        long revision) =>
        new(
            InitialWorkflow.Create(),
            "Scope was not verified.",
            ScopeSessionState.Canceled,
            selection,
            null,
            RuleSetNotConfigured,
            revision);

}
