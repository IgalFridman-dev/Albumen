using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Albumen.App.Shell;

public enum ViewerSide
{
    Left,
    Right,
}

// Placing a photo names the viewer, the photo's set and the photo. A click on a tile places
// only while that viewer is empty; the menu, a drop and a likely match replace its photo.
public sealed record PlaceInViewer(
    ViewerSide Side,
    Guid GroupId,
    ScanOccurrenceViewModel Member,
    bool OnlyIfEmpty = false);

// One of the two Review viewers. It shows a photo of a set large; its Remove button runs that
// set's own selection command, so the survivor rule, the override dialog and the reasons a
// photo can't be changed are exactly the set's. The viewer never marks anything by itself.
public sealed class ReviewViewerSlot : INotifyPropertyChanged
{
    public const string EmptyText = "You can drag a photo to this box to view it in detail";
    private static readonly string[] ShownProperties =
    [
        nameof(Member), nameof(GroupId), nameof(Preview), nameof(HasPhoto), nameof(IsMarked),
        nameof(CanRemove), nameof(IsReadOnly), nameof(HasRemove), nameof(ReadOnlyText),
        nameof(RemoveButtonText), nameof(RemoveAutomationName), nameof(AutomationName), nameof(ItemStatus),
    ];
    private CancellationTokenSource? loading;
    private long loadRequest;

    internal ReviewViewerSlot(ViewerSide side)
    {
        Side = side;
        Title = side == ViewerSide.Left ? "Left Viewer" : "Right Viewer";
        // The set's toggle guards itself against a second click while it runs (and while the
        // override dialog is open), so this button stays enabled and keeps keyboard focus.
        ToggleRemoveCommand = new AsyncCommand(
            () => Member?.ToggleSelectionCommand?.ExecuteAsync() ?? Task.CompletedTask,
            allowConcurrentExecutions: true,
            canExecute: () => CanRemove);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ViewerSide Side { get; }
    public string Title { get; }
    public ScanOccurrenceViewModel? Member { get; private set; }
    public Guid? GroupId { get; private set; }
    public PreviewItemViewModel? Preview { get; private set; }
    public bool HasPhoto => Member is not null;
    public bool IsMarked => Member?.IsSelected is true;
    public bool CanRemove => Member?.CanSelect is true;
    // A photo in no set (a search's other match or a Couldn't-check file) has nothing to mark:
    // it shows without a Remove button, with the reason under its name.
    public bool IsReadOnly => Member is { ToggleSelectionCommand: null };
    public bool HasRemove => HasPhoto && !IsReadOnly;
    public string ReadOnlyText => Member is { ToggleSelectionCommand: null } member ? member.EligibilityText : string.Empty;
    public string RemoveButtonText => IsMarked ? "Unmark this photo" : "Remove this photo";
    public string RemoveAutomationName => $"{(IsMarked ? "Unmark" : "Remove")} the photo in the {Title}";
    public AsyncCommand ToggleRemoveCommand { get; }
    public string AutomationName => Member is { } member ? $"{Title}: {member.FileName}" : $"{Title}: empty";
    public string ItemStatus => Member?.ItemStatus ?? EmptyText;

    internal bool Shows(Guid groupId, long occurrenceId) =>
        GroupId == groupId && Member?.OccurrenceId == occurrenceId;

    // Shows the photo with the preview already loaded for it, if any. A pending load is left
    // alone: it still applies when it ends, as long as the viewer shows the same photo.
    internal void Show(Guid groupId, ScanOccurrenceViewModel member, PreviewItemViewModel? preview)
    {
        Member = member;
        GroupId = groupId;
        Preview = preview;
        RaiseShown();
    }

    internal void Clear()
    {
        CancelLoad();
        if (Member is null && Preview is null) return;
        Member = null;
        GroupId = null;
        Preview = null;
        RaiseShown();
    }

    internal void SetPreview(PreviewItemViewModel preview)
    {
        Preview = preview;
        OnPropertyChanged(nameof(Preview));
    }

    // One load at a time: a newer one, or clearing the viewer, cancels the older one, and a
    // load that ends after that is dropped.
    internal CancellationTokenSource BeginLoad(out long request)
    {
        CancelLoad();
        var cancellation = new CancellationTokenSource();
        loading = cancellation;
        request = Interlocked.Increment(ref loadRequest);
        return cancellation;
    }

    internal bool IsCurrentLoad(long request) => request == Volatile.Read(ref loadRequest);

    internal void EndLoad(CancellationTokenSource cancellation)
    {
        Interlocked.CompareExchange(ref loading, null, cancellation);
        cancellation.Dispose();
    }

    internal void CancelLoad()
    {
        Interlocked.Increment(ref loadRequest);
        var cancellation = Interlocked.Exchange(ref loading, null);
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void RaiseShown()
    {
        foreach (var name in ShownProperties) OnPropertyChanged(name);
        ToggleRemoveCommand.NotifyCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
