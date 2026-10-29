using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Albumen.App.Shell;

namespace Albumen.App.Review;

// One photo of a set in the gallery. A click, Enter or Space shows it in the Left Viewer while
// that viewer is empty. Its menu (right-click, the Menu key or Shift+F10) marks or unmarks it and
// places it in either viewer, and UI Automation's Toggle marks it. Marking always runs the set's
// own selection command, so the survivor rule and the override dialog are exactly the set's. A
// photo in no set has nothing to mark: its menu only places it.
public sealed class ReviewPhotoTile : Button
{
    public static readonly DependencyProperty MemberProperty = DependencyProperty.Register(
        nameof(Member), typeof(ScanOccurrenceViewModel), typeof(ReviewPhotoTile),
        new PropertyMetadata(null, OnMemberChanged));

    public static readonly DependencyProperty GroupIdProperty = DependencyProperty.Register(
        nameof(GroupId), typeof(Guid), typeof(ReviewPhotoTile), new PropertyMetadata(Guid.Empty));

    public static readonly DependencyProperty PlaceCommandProperty = DependencyProperty.Register(
        nameof(PlaceCommand), typeof(ICommand), typeof(ReviewPhotoTile), new PropertyMetadata(null));

    // "Left Viewer" or "Right Viewer" while a viewer shows this photo of this set, else empty.
    public static readonly DependencyProperty ShownInProperty = DependencyProperty.Register(
        nameof(ShownIn), typeof(string), typeof(ReviewPhotoTile), new PropertyMetadata(string.Empty, OnShownInChanged));

    private static readonly DependencyPropertyKey IsShownInViewerPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsShownInViewer), typeof(bool), typeof(ReviewPhotoTile), new PropertyMetadata(false));

    public static readonly DependencyProperty IsShownInViewerProperty = IsShownInViewerPropertyKey.DependencyProperty;

    // The photo's pixel size once its thumbnail has loaded, for the facts shown on hover.
    public static readonly DependencyProperty DimensionsTextProperty = DependencyProperty.Register(
        nameof(DimensionsText), typeof(string), typeof(ReviewPhotoTile), new PropertyMetadata(string.Empty));

    private readonly ContextMenu menu = new();
    private readonly MenuItem removeItem = new();
    private readonly Separator removeSeparator = new();
    private readonly ToolTip facts = new();

    public ReviewPhotoTile()
    {
        AutomationProperties.SetAutomationId(removeItem, "selection-control");
        removeItem.Click += (_, _) => ToggleMark();
        var placeLeft = new MenuItem { Header = "Place in Left Viewer" };
        placeLeft.Click += (_, _) => Place(ViewerSide.Left, onlyIfEmpty: false);
        var placeRight = new MenuItem { Header = "Place in Right Viewer" };
        placeRight.Click += (_, _) => Place(ViewerSide.Right, onlyIfEmpty: false);
        menu.Items.Add(removeItem);
        menu.Items.Add(removeSeparator);
        menu.Items.Add(placeLeft);
        menu.Items.Add(placeRight);
        ContextMenu = menu;
        ToolTip = facts;
        UpdateMenu();
    }

    public ScanOccurrenceViewModel? Member
    {
        get => (ScanOccurrenceViewModel?)GetValue(MemberProperty);
        set => SetValue(MemberProperty, value);
    }

    public Guid GroupId
    {
        get => (Guid)GetValue(GroupIdProperty);
        set => SetValue(GroupIdProperty, value);
    }

    public ICommand? PlaceCommand
    {
        get => (ICommand?)GetValue(PlaceCommandProperty);
        set => SetValue(PlaceCommandProperty, value);
    }

    public string ShownIn
    {
        get => (string)GetValue(ShownInProperty);
        set => SetValue(ShownInProperty, value);
    }

    public bool IsShownInViewer => (bool)GetValue(IsShownInViewerProperty);

    public string DimensionsText
    {
        get => (string)GetValue(DimensionsTextProperty);
        set => SetValue(DimensionsTextProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        facts.ContentTemplate ??= TryFindResource("ReviewPhotoFactsTemplate") as DataTemplate;
    }

    protected override void OnClick()
    {
        base.OnClick();
        Place(ViewerSide.Left, onlyIfEmpty: true);
    }

    // The tile takes keyboard focus first, so the override dialog for the last kept photo of
    // a set and the projection that follows a mark give focus back to it.
    internal void ToggleMark()
    {
        Focus();
        if (Member?.ToggleSelectionCommand is { } toggle && toggle.CanExecute(null)) toggle.Execute(null);
    }

    private void Place(ViewerSide side, bool onlyIfEmpty)
    {
        if (Member is { } member && PlaceCommand is { } place)
            place.Execute(new PlaceInViewer(side, GroupId, member, onlyIfEmpty));
    }

    private void UpdateMenu()
    {
        var member = Member;
        removeItem.Header = member?.IsSelected is true ? "Unmark for removal" : "Remove";
        removeItem.IsEnabled = member?.CanSelect is true;
        var marks = member is not { ToggleSelectionCommand: null };
        if (marks != menu.Items.Contains(removeItem))
        {
            if (marks)
            {
                menu.Items.Insert(0, removeSeparator);
                menu.Items.Insert(0, removeItem);
            }
            else
            {
                menu.Items.Remove(removeItem);
                menu.Items.Remove(removeSeparator);
            }
        }
        facts.Content = member;
    }

    private static void OnMemberChanged(DependencyObject target, DependencyPropertyChangedEventArgs eventArgs)
    {
        var tile = (ReviewPhotoTile)target;
        tile.UpdateMenu();
        // A recycled tile that now shows another photo has not toggled anything.
        if (eventArgs.OldValue is not ScanOccurrenceViewModel old
            || eventArgs.NewValue is not ScanOccurrenceViewModel current
            || old.OccurrenceId != current.OccurrenceId)
            return;
        if (old.IsSelected != current.IsSelected && UIElementAutomationPeer.FromElement(tile) is ReviewPhotoTilePeer peer)
            peer.RaiseToggleStateChanged(old.IsSelected, current.IsSelected);
    }

    private static void OnShownInChanged(DependencyObject target, DependencyPropertyChangedEventArgs eventArgs) =>
        target.SetValue(IsShownInViewerPropertyKey, eventArgs.NewValue is string { Length: > 0 });

    protected override AutomationPeer OnCreateAutomationPeer() => new ReviewPhotoTilePeer(this);
}

// Invoke shows the photo in the Left Viewer (while it is empty); Toggle marks or unmarks it
// through the set's selection command. A photo that cannot be marked here, such as one in a
// Couldn't-check row, offers no Toggle.
internal sealed class ReviewPhotoTilePeer(ReviewPhotoTile owner) : ButtonAutomationPeer(owner), IToggleProvider
{
    private ReviewPhotoTile Tile => (ReviewPhotoTile)Owner;

    public override object GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Toggle && Tile.Member?.ToggleSelectionCommand is not null
            ? this
            : base.GetPattern(patternInterface);

    protected override string GetClassNameCore() => nameof(ReviewPhotoTile);

    void IToggleProvider.Toggle()
    {
        if (!IsEnabled() || Tile.Member is not { CanSelect: true }) throw new ElementNotEnabledException();
        Tile.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Tile.ToggleMark));
    }

    ToggleState IToggleProvider.ToggleState => State(Tile.Member?.IsSelected is true);

    internal void RaiseToggleStateChanged(bool before, bool after) =>
        RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty, State(before), State(after));

    private static ToggleState State(bool marked) => marked ? ToggleState.On : ToggleState.Off;
}
