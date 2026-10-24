using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Albumen.App.Review;
using Albumen.App.Shell;
using Albumen.App.Themes;
using Albumen.Application.Review;
using Albumen.Domain.Review;
using Albumen.Domain.Workflow;

namespace Albumen.App;

public partial class MainWindow : Window
{
    private readonly ResourceDictionary palette = new();
    private ShellViewModel? shellViewModel;
    private int dataContextGeneration;
    private bool isClosed;
    private int selectionSummaryAnnouncementGeneration;
    private int actionAnnouncementGeneration;
    private int actionRecoveryAnnouncementGeneration;
    private string announcedLocalDataStatus = string.Empty;
    private long reviewFailureAnnouncementSequence;
    private ReviewFocusIdentity? reviewFocusIdentity;
    private long reviewFocusGeneration;
    private bool isRestoringReviewFocus;
    private int reviewDialogFocusTransitionDepth;
    private long reviewDialogFocusTransitionGeneration;
    private bool closeAfterLifetime;
    private Task? closeCompletionTask;
    private Task? exitLifetimeObservationTask;

    public MainWindow()
    {
        InitializeComponent();
        // The window applies the palette itself because tests construct it without App.
        Resources.MergedDictionaries.Add(palette);
        ApplyPalette();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        ReviewPageScrollViewer.AddHandler(RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler(OnReviewListItemBringIntoView), true);
        ActionPageScrollViewer.AddHandler(RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler(OnReviewListItemBringIntoView), true);
        LeftPreviewStatusControl.PreviewImage.SetValue(AutomationProperties.NameProperty, "Photo in the Left Viewer");
        RightPreviewStatusControl.PreviewImage.SetValue(AutomationProperties.NameProperty, "Photo in the Right Viewer");
        LinkViewerPanning(LeftPreviewStatusControl.PreviewImageScroller, RightPreviewStatusControl.PreviewImageScroller);
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        SizeChanged += OnReviewSizeChanged;
        Closing += OnClosing;
        Closed += OnClosed;
        AddHandler(Keyboard.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnReviewKeyboardFocusChanged), true);
        if (System.Windows.Application.Current is { } application)
            application.Exit += OnApplicationExit;
    }

    private void ApplyPalette() => AlbumenPalette.Apply(palette, SystemParameters.HighContrast);

    // Raised on the thread that saw the setting change, so the window re-reads it on its own. Every
    // change re-applies: the High Contrast palette holds the system brushes of the moment, so a
    // theme change under High Contrast (Black to White) must refresh them too.
    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!isClosed) ApplyPalette();
        });
    }

    private void OnDataContextChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        dataContextGeneration++;
        announcedLocalDataStatus = string.Empty;
        reviewFocusIdentity = null;
        reviewFocusGeneration++;
        if (shellViewModel is not null)
        {
            shellViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        shellViewModel = eventArgs.NewValue as ShellViewModel;
        if (shellViewModel is not null)
        {
            shellViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        UpdateReviewLayout();
    }

    private void OnLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (shellViewModel?.IsBlocked is true)
        {
            FocusBlockingNotice();
        }
        if (shellViewModel?.IsActionPhase is true) QueuePhaseFocus();
        QueueLocalDataStatusAnnouncement();
        UpdateReviewLayout();
    }

    private void OnViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ShellViewModel.IsScanBusy)
            && shellViewModel is { IsScanPhase: true, IsScanBusy: true })
        {
            QueuePhaseFocus();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.CurrentPhase))
        {
            QueuePhaseFocus();
        }
        if (eventArgs.PropertyName is nameof(ShellViewModel.ScanStatus) or nameof(ShellViewModel.ReviewStatusText))
        {
            QueueScanStatusAnnouncement();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.ScopeStatus))
        {
            QueueScopeStatusAnnouncement();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.SelectionSummary))
        {
            QueueSelectionSummaryAnnouncement();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.ActionAnnouncement))
        {
            QueueActionAnnouncement();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.ActionRecoveryAnnouncement))
        {
            QueueActionRecoveryAnnouncement();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.LocalDataClearStatus))
        {
            QueueLocalDataClearStatusAnnouncement();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.ActionRecoveryStatusText))
        {
            QueueLocalDataStatusAnnouncement();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.ReviewAnnouncement))
        {
            QueueReviewFailureAnnouncement();
            QueueReviewFocusRestoration();
        }
        if (eventArgs.PropertyName is nameof(ShellViewModel.ExactGroups)
            or nameof(ShellViewModel.ReviewCategories))
        {
            QueueReviewFocusRestoration();
        }
        if (eventArgs.PropertyName == nameof(ShellViewModel.ReviewNavigationLevel))
        {
            UpdateReviewLayout();
            QueueReviewNavigationFocus();
        }

        if (eventArgs.PropertyName == nameof(ShellViewModel.IsBlocked) &&
            shellViewModel?.IsBlocked is true)
        {
            FocusBlockingNotice();
        }
    }

    private void OnReviewKeyboardFocusChanged(object sender, KeyboardFocusChangedEventArgs eventArgs)
    {
        if (isRestoringReviewFocus
            || reviewDialogFocusTransitionDepth != 0
            || shellViewModel is not { IsReviewPhase: true, ReviewSession: { } session }
            || eventArgs.NewFocus is not FrameworkElement element
            || !ReferenceEquals(Keyboard.FocusedElement, element)
            || !CanReceiveFocus(element))
            return;
        if (element.DataContext is ScanOccurrenceViewModel member
            && FindReviewGroupId(element) is { } groupId)
        {
            reviewFocusGeneration++;
            reviewFocusIdentity = new(session, groupId, member.OccurrenceId,
                IsWithin(element, ReviewGalleryList));
            return;
        }
        if (reviewFocusIdentity?.Session != session || !IsDeliberateReviewFocusTarget(element)) return;
        reviewFocusGeneration++;
        reviewFocusIdentity = null;
    }

    private bool IsDeliberateReviewFocusTarget(FrameworkElement element) =>
        ReferenceEquals(element, ReviewHeading)
        || ReferenceEquals(element, ApprovalPanel)
        || element is ButtonBase;

    internal long BeginReviewDialogFocusTransition()
    {
        reviewDialogFocusTransitionDepth++;
        return ++reviewDialogFocusTransitionGeneration;
    }

    internal void EndReviewDialogFocusTransition() => reviewDialogFocusTransitionDepth--;

    private void OnCanSelectFocusedReviewGroup(object sender, CanExecuteRoutedEventArgs eventArgs)
    {
        var command = FindSelectAllCommand(eventArgs.OriginalSource as DependencyObject);
        eventArgs.CanExecute = command?.CanExecute(null) is true;
        eventArgs.Handled = command is not null;
    }

    private void OnSelectFocusedReviewGroup(object sender, ExecutedRoutedEventArgs eventArgs)
    {
        var command = FindSelectAllCommand(eventArgs.OriginalSource as DependencyObject);
        if (command is null || !command.CanExecute(null)) return;
        command.Execute(null);
        eventArgs.Handled = true;
    }

    private static AsyncCommand? FindSelectAllCommand(DependencyObject? source) =>
        FindDataContext<ReviewGroupViewModel>(source)?.SelectAllCommand
        ?? FindDataContext<ExactGroupViewModel>(source)?.SelectAllCommand;

    private static Guid? FindReviewGroupId(DependencyObject? source) =>
        FindDataContext<ReviewGroupViewModel>(source)?.GroupId
        ?? FindDataContext<ExactGroupViewModel>(source)?.GroupId;

    private static bool IsWithin(DependencyObject? source, DependencyObject ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor)) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void OnReviewListPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter || sender is not ListBox list || shellViewModel is null)
            return;

        var source = eventArgs.OriginalSource as DependencyObject
            ?? Keyboard.FocusedElement as DependencyObject;
        // A card's own buttons (such as Compare) act on Enter themselves.
        if (FindAncestor<Button>(source) is { } button && IsWithin(button, list)
            && !AutomationProperties.GetAutomationId(button).StartsWith("open-review-", StringComparison.Ordinal))
            return;
        var item = list.SelectedItem
            ?? FindDataContext<ReviewCategoryViewModel>(source)
            ?? (object?)FindDataContext<ReviewGroupViewModel>(source)
            ?? (object?)FindDataContext<ScanOccurrenceViewModel>(source)
            ?? FindDataContext<LikelyMatchItemViewModel>(source);
        if (ReferenceEquals(list, ReviewCategoriesList) && item is ReviewCategoryViewModel category)
            shellViewModel.OpenReviewCategoryCommand.Execute(category);
        else if (ReferenceEquals(list, LikelyMatchesList) && item is LikelyMatchItemViewModel match)
            shellViewModel.OpenLikelyMatchCommand.Execute(match);
        else
            return;

        eventArgs.Handled = true;
    }

    private void OnOpenReviewCategoryClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Button { DataContext: ReviewCategoryViewModel category })
            shellViewModel?.OpenReviewCategoryCommand.Execute(category);
    }

    // A click (or Enter or Space) on a photo shows it in the Left Viewer while that viewer is
    // empty, and does nothing otherwise; drag and drop place it anywhere.
    private void OnReviewTileClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: ScanOccurrenceViewModel member } tile
            || FindReviewGroupId(tile) is not { } groupId || shellViewModel is null) return;
        shellViewModel.PlaceInViewerCommand.Execute(
            new PlaceInViewer(ViewerSide.Left, groupId, member, OnlyIfEmpty: true));
    }

    // A shown set works out its Suggested best from its thumbnails; a set that scrolls away, or
    // whose container is recycled for another set, stops.
    private readonly Dictionary<FrameworkElement, CancellationTokenSource> suggestionLoads = [];

    private void OnReviewSetLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement set) return;
        set.DataContextChanged -= OnReviewSetDataContextChanged;
        set.DataContextChanged += OnReviewSetDataContextChanged;
        StartSuggestion(set);
    }

    private void OnReviewSetUnloaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement set) return;
        set.DataContextChanged -= OnReviewSetDataContextChanged;
        StopSuggestion(set);
    }

    private void OnReviewSetDataContextChanged(object sender, DependencyPropertyChangedEventArgs eventArgs)
    {
        if (sender is not FrameworkElement set) return;
        StopSuggestion(set);
        if (set.IsLoaded) StartSuggestion(set);
    }

    private void StartSuggestion(FrameworkElement set)
    {
        if (set.DataContext is not ReviewGroupViewModel group
            || shellViewModel?.ReviewThumbnails is not { } thumbnails
            || suggestionLoads.ContainsKey(set))
            return;
        var cancellation = new CancellationTokenSource();
        suggestionLoads[set] = cancellation;
        _ = group.LoadSuggestionAsync(thumbnails, cancellation.Token);
    }

    private void StopSuggestion(FrameworkElement set)
    {
        if (!suggestionLoads.Remove(set, out var cancellation)) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void OnOpenLikelyMatchClick(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Button { DataContext: LikelyMatchItemViewModel match }) return;
        shellViewModel?.OpenLikelyMatchCommand.Execute(match);
        // Show the top of the viewers so the match visibly opens, scrolling the page no
        // further than that.
        ReviewViewersPanel.BringIntoView(new Rect(0, 0, ReviewViewersPanel.ActualWidth,
            Math.Min(ReviewViewersPanel.ActualHeight, 160)));
    }

    // Show all clears the search; the prompt keeps focus so the next search starts there.
    private void OnLikelyMatchesShowAllClick(object sender, RoutedEventArgs eventArgs) =>
        Dispatcher.BeginInvoke(() => LikelyMatchesPromptBox.Focus(), DispatcherPriority.Input);

    private void OnReviewBackClick(object sender, RoutedEventArgs eventArgs) =>
        shellViewModel?.ReviewBackCommand.Execute(null);

    private void OnReviewSizeChanged(object sender, SizeChangedEventArgs eventArgs) =>
        UpdateReviewLayout();

    // The kinds and the gallery keep a bounded height so they stay virtualized inside the
    // scrolling Review page, leaving room for the viewers below them to come into view. The
    // kinds scroll a whole card at a time, so they stay taller than one card: a shorter list
    // would cut off the bottom of a card for good.
    private void OnReviewPageSizeChanged(object sender, SizeChangedEventArgs eventArgs)
    {
        if (!eventArgs.HeightChanged || eventArgs.NewSize.Height <= 0) return;
        var viewport = eventArgs.NewSize.Height;
        ReviewNavigationGrid.Height = Math.Min(viewport, Math.Clamp(viewport * 0.6, 300, 720));
        // Scrolled to the viewers, a photo fills most of the page instead of a fixed thumbnail.
        LeftPreviewStatusControl.FitHeight = Math.Max(240, viewport * 0.8);
        RightPreviewStatusControl.FitHeight = LeftPreviewStatusControl.FitHeight;
    }

    // The two viewers pan together; photos of different widths by the same share of their own
    // width. A new or re-zoomed photo takes the Left Viewer's position.
    private static void LinkViewerPanning(ScrollViewer left, ScrollViewer right)
    {
        var syncing = false;
        void Follow(ScrollViewer leader, ScrollViewer follower)
        {
            if (syncing) return;
            syncing = true;
            try
            {
                var share = leader.ScrollableWidth > 0 ? leader.HorizontalOffset / leader.ScrollableWidth : 0;
                follower.ScrollToHorizontalOffset(share * follower.ScrollableWidth);
            }
            finally
            {
                syncing = false;
            }
        }
        left.ScrollChanged += (_, change) =>
        {
            if (change.HorizontalChange != 0 || change.ExtentWidthChange != 0) Follow(left, right);
        };
        right.ScrollChanged += (_, change) =>
        {
            if (change.HorizontalChange != 0) Follow(right, left);
            else if (change.ExtentWidthChange != 0) Follow(left, right);
        };
    }

    // Drag and drop between the gallery and the viewers. A photo dragged from a set onto a viewer
    // is placed there. A viewer's photo dropped on the other viewer swaps the two, dropped on its
    // own viewer stays, and dropped anywhere else leaves its viewer; Esc cancels.
    private (UIElement Source, Point Start)? pressed;
    private bool viewerDropped;
    private bool viewerDragCanceled;

    private void OnTilePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is UIElement tile) pressed = (tile, eventArgs.GetPosition(tile));
    }

    private void OnTileMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (sender is not FrameworkElement { DataContext: ScanOccurrenceViewModel member } tile
            || !DragStarts(tile, eventArgs) || FindReviewGroupId(tile) is not { } groupId)
            return;
        // The button holds the mouse while pressed; the drag takes it over, so no click follows.
        tile.ReleaseMouseCapture();
        DragDrop.DoDragDrop(tile, ReviewDragData.Create(member.OccurrenceId, groupId), DragDropEffects.Copy);
    }

    // A viewer's photo is dragged by the photo itself, not by its button or path.
    private void OnViewerPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs eventArgs)
    {
        pressed = sender is UIElement viewer && ViewerSideOfPanel(viewer) is { } side
            && IsWithin(eventArgs.OriginalSource as DependencyObject, PreviewOf(side).PreviewImage)
                ? (viewer, eventArgs.GetPosition(viewer))
                : null;
    }

    private void OnViewerPreviewMouseMove(object sender, MouseEventArgs eventArgs)
    {
        if (sender is not UIElement viewer || ViewerSideOfPanel(viewer) is not { } side
            || !DragStarts(viewer, eventArgs) || shellViewModel is not { } viewModel)
            return;
        var slot = side == ViewerSide.Left ? viewModel.LeftViewer : viewModel.RightViewer;
        if (slot is not { Member: { } member, GroupId: { } groupId }) return;
        viewerDropped = false;
        viewerDragCanceled = false;
        DragDrop.DoDragDrop(viewer, ReviewDragData.Create(member.OccurrenceId, groupId, side), DragDropEffects.Move);
        FinishViewerDrag(side, viewerDropped, viewerDragCanceled);
    }

    private void OnViewerQueryContinueDrag(object sender, QueryContinueDragEventArgs eventArgs)
    {
        if (eventArgs.EscapePressed) viewerDragCanceled = true;
    }

    // Dragged out of its box and dropped anywhere but a viewer, a photo leaves its viewer and
    // nothing else changes.
    internal void FinishViewerDrag(ViewerSide side, bool droppedOnViewer, bool canceled)
    {
        if (!droppedOnViewer && !canceled) shellViewModel?.ClearViewerCommand.Execute(side);
    }

    private bool DragStarts(UIElement source, MouseEventArgs eventArgs)
    {
        if (eventArgs.LeftButton != MouseButtonState.Pressed)
        {
            pressed = null;
            return false;
        }
        if (pressed is not { } press || !ReferenceEquals(press.Source, source)) return false;
        var moved = eventArgs.GetPosition(source) - press.Start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return false;
        pressed = null;
        return true;
    }

    // Only Albumen's own photos can be dropped on a viewer. The preview events run before the
    // path box inside the viewer, which would refuse them as text.
    private void OnViewerDragOver(object sender, DragEventArgs eventArgs)
    {
        eventArgs.Effects = ReviewDragData.TryRead(eventArgs.Data, out _, out _, out var fromViewer)
            ? fromViewer is null ? DragDropEffects.Copy : DragDropEffects.Move
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void OnViewerDrop(object sender, DragEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        if (sender is UIElement viewer && ViewerSideOfPanel(viewer) is { } side
            && TryPlaceFromData(eventArgs.Data, side))
            viewerDropped = true;
    }

    internal bool TryPlaceFromData(IDataObject data, ViewerSide side)
    {
        if (shellViewModel is not { } viewModel
            || !ReviewDragData.TryRead(data, out var occurrenceId, out var groupId, out var fromViewer))
            return false;
        if (fromViewer is { } source)
        {
            if (source != side) viewModel.SwapViewersCommand.Execute(null);
            return true;
        }
        if (viewModel.FindReviewMember(groupId, occurrenceId) is not { } member) return false;
        viewModel.PlaceInViewerCommand.Execute(new PlaceInViewer(side, groupId, member));
        return true;
    }

    private ViewerSide? ViewerSideOfPanel(UIElement element) =>
        ReferenceEquals(element, LeftViewerPanel) ? ViewerSide.Left
        : ReferenceEquals(element, RightViewerPanel) ? ViewerSide.Right
        : null;

    private PreviewStatusControl PreviewOf(ViewerSide side) =>
        side == ViewerSide.Left ? LeftPreviewStatusControl : RightPreviewStatusControl;

    // The selection bar stays under the page; its button brings the approval into view.
    private void OnReviewCleanupClick(object sender, RoutedEventArgs eventArgs)
    {
        ApprovalPanel.BringIntoView();
        ApprovalPanel.Focus();
    }

    // The Recycle Bin item list gets the same bounded height so the progress summary
    // and the earlier cleanups below it stay reachable at small window sizes.
    private void OnActionPageSizeChanged(object sender, SizeChangedEventArgs eventArgs)
    {
        if (!eventArgs.HeightChanged || eventArgs.NewSize.Height <= 0) return;
        var viewport = eventArgs.NewSize.Height;
        ActionItemsList.Height = Math.Min(viewport, Math.Clamp(viewport * 0.5, 180, 520));
    }

    private ScrollViewer? PageScrollerFor(DependencyObject element) =>
        IsWithin(element, ReviewCategoriesList) || IsWithin(element, ReviewGalleryList) ? ReviewPageScrollViewer
        : IsWithin(element, ActionItemsList) ? ActionPageScrollViewer
        : null;

    // A list scrolls its own item into view and stops the request, so the page then
    // scrolls just enough to show that item too (for example at large text sizes).
    private void OnReviewListItemBringIntoView(object sender, RequestBringIntoViewEventArgs eventArgs)
    {
        if (eventArgs.TargetObject is not FrameworkElement target
            || PageScrollerFor(target) is not { } page)
            return;
        Dispatcher.InvokeAsync(() =>
        {
            if (isClosed || !target.IsVisible || !IsWithin(target, page)) return;
            var bounds = target.TransformToAncestor(page)
                .TransformBounds(new Rect(target.RenderSize));
            var viewport = page.ViewportHeight;
            var offset = page.VerticalOffset;
            if (bounds.Top < 0 || bounds.Height > viewport)
                page.ScrollToVerticalOffset(offset + bounds.Top);
            else if (bounds.Bottom > viewport)
                page.ScrollToVerticalOffset(offset + bounds.Bottom - viewport);
        }, DispatcherPriority.Loaded);
    }

    // One notch of a mouse wheel reports a delta of 120 (WM_MOUSEWHEEL's WHEEL_DELTA). A precision
    // touchpad reports many smaller deltas, which add up here until they make a notch.
    private const int WheelDeltaPerNotch = 120;
    private readonly Dictionary<DependencyObject, int> wheelResiduals = new();

    // A list at its scroll limit hands the wheel to its page, so the page can still be
    // scrolled while the pointer rests over a list.
    private void OnReviewListPreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if (sender is not DependencyObject list
            || Descendants<ScrollViewer>(list).FirstOrDefault() is not { } listScroller) return;
        var atLimit = eventArgs.Delta > 0
            ? listScroller.VerticalOffset <= 0
            : listScroller.VerticalOffset >= listScroller.ScrollableHeight;
        if (!atLimit)
        {
            // The lists scroll by item (a 10,000-photo set must not realize every card), and a
            // wheel notch would move three items and skip the middle of a short list. One card
            // per notch keeps every category and set reachable at the default window size.
            eventArgs.Handled = true;
            var residual = wheelResiduals.GetValueOrDefault(list) + eventArgs.Delta;
            for (; residual >= WheelDeltaPerNotch; residual -= WheelDeltaPerNotch) listScroller.LineUp();
            for (; residual <= -WheelDeltaPerNotch; residual += WheelDeltaPerNotch) listScroller.LineDown();
            wheelResiduals[list] = residual;
            return;
        }
        wheelResiduals.Remove(list);
        if (PageScrollerFor(list) is not { } page) return;
        eventArgs.Handled = true;
        page.ScrollToVerticalOffset(page.VerticalOffset - eventArgs.Delta / 3.0);
    }

    // The gallery scrolls by pixel, so a wheel notch moves it by the wheel's delta (a notch is
    // 120 px, a touchpad's small deltas move it smoothly). At either end the page takes the wheel.
    private void OnGalleryPreviewMouseWheel(object sender, MouseWheelEventArgs eventArgs)
    {
        if (sender is not DependencyObject gallery
            || Descendants<ScrollViewer>(gallery).FirstOrDefault() is not { } scroller) return;
        eventArgs.Handled = true;
        var atLimit = eventArgs.Delta > 0
            ? scroller.VerticalOffset <= 0
            : scroller.VerticalOffset >= scroller.ScrollableHeight;
        if (!atLimit)
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - eventArgs.Delta);
        else
            ReviewPageScrollViewer.ScrollToVerticalOffset(ReviewPageScrollViewer.VerticalOffset - eventArgs.Delta / 3.0);
    }

    // Arrow keys move between the photos as they are laid out. The gallery's sets take no focus,
    // so the list's own item navigation is not used.
    private void OnGalleryPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        FocusNavigationDirection? direction = eventArgs.Key switch
        {
            Key.Left => FocusNavigationDirection.Left,
            Key.Right => FocusNavigationDirection.Right,
            Key.Up => FocusNavigationDirection.Up,
            Key.Down => FocusNavigationDirection.Down,
            _ => null,
        };
        if (direction is not { } move || Keyboard.FocusedElement is not UIElement focused
            || !IsWithin(focused, ReviewGalleryList))
            return;
        focused.MoveFocus(new TraversalRequest(move));
        eventArgs.Handled = true;
    }

    private void UpdateReviewLayout()
    {
        if (!IsInitialized) return;
        // In a short window the page keeps the height; Approval stays reachable by scrolling.
        var height = double.IsNaN(Height) ? ActualHeight : Height;
        ReviewSelectionBar.Visibility = height >= 640 ? Visibility.Visible : Visibility.Collapsed;
        var width = double.IsNaN(Width) ? ActualWidth : Width;
        var galleryOpen = shellViewModel?.ReviewNavigationLevel == ReviewNavigationLevel.Gallery;
        // A wide window shows the kinds beside the open kind's sets; a narrow one shows the
        // open level only, and Back returns to the kinds.
        var sideBySide = width >= 800;
        ReviewCategoriesPanel.Visibility = sideBySide || !galleryOpen ? Visibility.Visible : Visibility.Collapsed;
        ReviewGroupsPanel.Visibility = sideBySide || galleryOpen ? Visibility.Visible : Visibility.Collapsed;
        UpdateReviewColumnWidths();
    }

    // The gallery gets twice the room of the kinds beside it.
    private void UpdateReviewColumnWidths()
    {
        ReviewCategoriesColumn.Width = ReviewCategoriesPanel.Visibility == Visibility.Visible
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        ReviewGroupsColumn.Width = ReviewGroupsPanel.Visibility == Visibility.Visible
            ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
    }

    private void QueueReviewNavigationFocus()
    {
        var expected = shellViewModel;
        var level = expected?.ReviewNavigationLevel;
        Dispatcher.InvokeAsync(() =>
        {
            if (isClosed || !ReferenceEquals(expected, shellViewModel) || !IsVisible) return;
            UpdateLayout();
            var target = level switch
            {
                ReviewNavigationLevel.Categories => ReviewCategoriesList,
                ReviewNavigationLevel.Gallery => ReviewGalleryList,
                _ => null,
            };
            if (target is null || !CanReceiveFocus(target)) return;
            target.Focus();
            Keyboard.Focus(target);
        }, DispatcherPriority.Input);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindDataContext<T>(DependencyObject? current) where T : class
    {
        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: T match }) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void FocusBlockingNotice() =>
        Dispatcher.InvokeAsync(
            () =>
            {
                Activate();
                BlockingNotice.Focus();
                Keyboard.Focus(BlockingNotice);
            },
            DispatcherPriority.Input);

    // Leaving History cancels a pending clear, so coming back never lands keyboard focus
    // on the destructive confirmation the user did not just ask for.
    private void OnHistoryUnselected(object sender, RoutedEventArgs eventArgs)
    {
        if (!ReferenceEquals(eventArgs.OriginalSource, ActionRecordsTab)
            || shellViewModel?.IsClearScanDataConfirmationPending is not true) return;
        shellViewModel.CancelClearScanDataCommand.Execute(null);
    }

    private void OnClearScanDataConfirmationVisibilityChanged(
        object sender,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(sender, ConfirmClearScanDataButton)) return;
            if (eventArgs.NewValue is true)
            {
                if (shellViewModel?.IsClearScanDataConfirmationPending is not true
                    || !ConfirmClearScanDataButton.IsVisible
                    || !ConfirmClearScanDataButton.IsEnabled)
                    return;
                Activate();
                ConfirmClearScanDataButton.Focus();
                Keyboard.Focus(ConfirmClearScanDataButton);
                return;
            }
            if (shellViewModel?.IsClearScanDataConfirmationPending is true
                || !string.IsNullOrEmpty(shellViewModel?.LocalDataClearStatus)
                || !ClearScanDataButton.IsVisible
                || !ClearScanDataButton.IsEnabled)
                return;
            Activate();
            ClearScanDataButton.Focus();
            Keyboard.Focus(ClearScanDataButton);
        }, DispatcherPriority.Input);
    }

    private void QueueLocalDataClearStatusAnnouncement()
    {
        var status = shellViewModel?.LocalDataClearStatus;
        if (string.IsNullOrWhiteSpace(status)) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (isClosed || shellViewModel?.LocalDataClearStatus != status
                || LocalDataClearStatusText.Text != status
                || AutomationProperties.GetItemStatus(LocalDataClearStatusText) != status)
                return;
            var peer = UIElementAutomationPeer.FromElement(LocalDataClearStatusText)
                ?? UIElementAutomationPeer.CreatePeerForElement(LocalDataClearStatusText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            Activate();
            LocalDataClearStatusText.Focus();
            Keyboard.Focus(LocalDataClearStatusText);
        }, DispatcherPriority.Input);
    }

    // The local-data failure can be projected before the window is loaded, so it is
    // announced on load as well as on change, once per distinct failure message.
    private void QueueLocalDataStatusAnnouncement()
    {
        var expected = shellViewModel;
        if (expected is not { IsLocalDataFaulted: true })
        {
            announcedLocalDataStatus = string.Empty;
            return;
        }
        var status = expected.ActionRecoveryStatusText;
        var generation = dataContextGeneration;
        Dispatcher.InvokeAsync(() =>
        {
            if (isClosed || !IsLoaded || generation != dataContextGeneration
                || !ReferenceEquals(expected, shellViewModel)
                || announcedLocalDataStatus == status
                || !LocalDataStatusText.IsVisible || LocalDataStatusText.Text != status)
                return;
            var peer = UIElementAutomationPeer.FromElement(LocalDataStatusText)
                ?? UIElementAutomationPeer.CreatePeerForElement(LocalDataStatusText);
            if (peer is null) return;
            announcedLocalDataStatus = status;
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }, DispatcherPriority.Background);
    }

    private void QueueScopeStatusAnnouncement()
    {
        if (shellViewModel is not { } expectedViewModel) return;
        var expectedStatus = expectedViewModel.ScopeStatus;
        if (expectedStatus is not "Checking location and access" and
            not "Ready" and
            not "Blocked")
        {
            return;
        }

        // The strip shows and announces the one-sentence form of that status.
        var expectedSentence = expectedViewModel.FolderStatusSentence;
        var expectedGeneration = dataContextGeneration;
        Dispatcher.InvokeAsync(
            () =>
            {
                if (isClosed ||
                    expectedGeneration != dataContextGeneration ||
                    !ReferenceEquals(expectedViewModel, shellViewModel) ||
                    expectedViewModel.ScopeStatus != expectedStatus ||
                    ScopeStatusText.Text != expectedSentence ||
                    AutomationProperties.GetItemStatus(ScopeStatusText) !=
                        expectedSentence)
                {
                    return;
                }

                var peer = UIElementAutomationPeer.FromElement(ScopeStatusText) ??
                    UIElementAutomationPeer.CreatePeerForElement(ScopeStatusText);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            },
            DispatcherPriority.Background);
    }

    private void QueuePhaseFocus()
    {
        var generation = dataContextGeneration;
        var expected = shellViewModel;
        if (expected is null) return;
        var phase = expected.CurrentPhase;
        Dispatcher.InvokeAsync(() =>
        {
            if (isClosed || generation != dataContextGeneration || !ReferenceEquals(expected, shellViewModel)
                || phase != shellViewModel?.CurrentPhase) return;
            UIElement? target = phase switch
            {
                WorkflowPhase.ChooseScope => expected.IsBlocked ? BlockingNotice : ChooseAnotherScopeButton,
                // Folder and options share one screen; its Scan button is the next step.
                WorkflowPhase.ConfirmRules => expected.CanScanFolder ? ScanFolderButton : ChooseAnotherScopeButton,
                WorkflowPhase.Scan => expected.IsScanBusy ? CancelScanButton : null,
                WorkflowPhase.Review => ReviewHeading,
                WorkflowPhase.ApproveCleanup => ActionHeading,
                _ => null,
            };
            if (target is not null && target.IsVisible && target.IsEnabled)
            {
                Activate();
                target.Focus();
                Keyboard.Focus(target);
            }
        }, DispatcherPriority.Input);
    }

    private void QueueScanStatusAnnouncement()
    {
        var expected = shellViewModel;
        // A Review fault uses its dedicated once-per-transition announcement.
        if (expected is { IsReviewPhase: true, ReviewState: ReviewStatus.Faulted }) return;
        var status = expected?.IsReviewPhase is true ? expected.ReviewStatusText : expected?.ScanStatus;
        var generation = dataContextGeneration;
        Dispatcher.InvokeAsync(() =>
        {
            var statusElement = expected?.IsReviewPhase is true ? ReviewStatusText : ScanStatusText;
            if (isClosed || generation != dataContextGeneration || !ReferenceEquals(expected, shellViewModel)
                || !statusElement.IsVisible || statusElement.Text != status || AutomationProperties.GetItemStatus(statusElement) != status) return;
            var peer = UIElementAutomationPeer.FromElement(statusElement)
                ?? UIElementAutomationPeer.CreatePeerForElement(statusElement);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }, DispatcherPriority.Background);
    }

    private void QueueSelectionSummaryAnnouncement()
    {
        var generation = dataContextGeneration;
        var expected = shellViewModel;
        var announcement = Interlocked.Increment(ref selectionSummaryAnnouncementGeneration);
        Dispatcher.InvokeAsync(() =>
        {
            if (isClosed || generation != dataContextGeneration
                || announcement != selectionSummaryAnnouncementGeneration
                || !ReferenceEquals(expected, shellViewModel) || expected?.IsReviewPhase is not true) return;
            if (!SelectionSummaryText.IsVisible) return;
            var peer = UIElementAutomationPeer.FromElement(SelectionSummaryText)
                ?? UIElementAutomationPeer.CreatePeerForElement(SelectionSummaryText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }, DispatcherPriority.Background);
    }

    private void QueueActionAnnouncement()
    {
        var generation = dataContextGeneration;
        var expected = shellViewModel;
        var sequence = ++actionAnnouncementGeneration;
        Dispatcher.InvokeAsync(() =>
        {
            if (isClosed || generation != dataContextGeneration || sequence != actionAnnouncementGeneration
                || !ReferenceEquals(expected, shellViewModel) || expected?.IsActionPhase is not true
                || !ActionAnnouncementText.IsVisible) return;
            // Coalesce transient progress; all item reasons remain inspectable.
            ActionAnnouncementText.Announce(sequence, expected.ActionAnnouncement);
        }, DispatcherPriority.Background);
    }

    private void QueueActionRecoveryAnnouncement()
    {
        var generation = dataContextGeneration;
        var expected = shellViewModel;
        var sequence = ++actionRecoveryAnnouncementGeneration;
        Dispatcher.InvokeAsync(() =>
        {
            if (isClosed || generation != dataContextGeneration || sequence != actionRecoveryAnnouncementGeneration
                || !ReferenceEquals(expected, shellViewModel) || expected?.IsActionPhase is not true
                || !ActionRecoveryAnnouncementText.IsVisible) return;
            ActionRecoveryAnnouncementText.Announce(sequence, expected.ActionRecoveryAnnouncement);
        }, DispatcherPriority.Background);
    }

    private void QueueReviewFailureAnnouncement()
    {
        var generation = dataContextGeneration;
        var expected = shellViewModel;
        var session = expected?.ReviewSession;
        var payload = expected?.ReviewAnnouncement;
        if (string.IsNullOrEmpty(payload)) return;
        var sequence = ++reviewFailureAnnouncementSequence;
        ReviewAnnouncementText.CaptureDiagnostic(
            ReviewAnnouncementDiagnosticStage.Queue, sequence, generation);
        Dispatcher.InvokeAsync(() =>
        {
            string? skipReason = null;
            if (isClosed) skipReason = "closed";
            else if (generation != dataContextGeneration) skipReason = "data-context-generation";
            else if (!ReferenceEquals(expected, shellViewModel)) skipReason = "data-context-identity";
            else if (expected?.ReviewSession != session) skipReason = "review-session";
            else if (!ReviewAnnouncementText.IsVisible) skipReason = "not-visible";
            if (skipReason is not null)
            {
                ReviewAnnouncementText.CaptureDiagnostic(
                    ReviewAnnouncementDiagnosticStage.Skip, sequence, generation,
                    skipReason: skipReason);
                return;
            }
            ReviewAnnouncementText.CaptureDiagnostic(
                ReviewAnnouncementDiagnosticStage.Execute, sequence, generation);
            ReviewAnnouncementText.Announce(sequence, payload);
        }, DispatcherPriority.Background);
    }

    private void QueueReviewFocusRestoration()
    {
        var token = CaptureReviewFocusRestorationToken(reviewFocusIdentity);
        if (token is not null)
            QueueReviewFocusRestoration(token, reviewDialogFocusTransitionGeneration);
    }

    internal ReviewFocusRestorationToken? CaptureReviewFocusRestorationToken(IInputElement invokingFocus)
    {
        if (invokingFocus is not FrameworkElement { DataContext: ScanOccurrenceViewModel member } element
            || shellViewModel?.ReviewSession is not { } session
            || FindReviewGroupId(element) is not { } groupId)
            return null;
        var identity = new ReviewFocusIdentity(session, groupId, member.OccurrenceId,
            IsWithin(element, ReviewGalleryList));
        return identity == reviewFocusIdentity ? CaptureReviewFocusRestorationToken(identity) : null;
    }

    private ReviewFocusRestorationToken? CaptureReviewFocusRestorationToken(ReviewFocusIdentity? identity)
    {
        var expected = shellViewModel;
        if (identity is null || expected?.IsReviewPhase is not true
            || expected.ReviewSession != identity.Session)
            return null;
        return new(expected, dataContextGeneration, identity.Session, identity.GroupId,
            identity.OccurrenceId, identity.GroupedSurface, reviewFocusGeneration);
    }

    internal void QueueReviewFocusRestoration(
        ReviewFocusRestorationToken token,
        long expectedDialogFocusTransitionGeneration)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsReviewFocusRestorationRelevant(token, expectedDialogFocusTransitionGeneration)) return;
            UpdateLayout();
            if (!IsReviewFocusRestorationRelevant(token, expectedDialogFocusTransitionGeneration)) return;
            var identity = new ReviewFocusIdentity(token.Session, token.GroupId,
                token.OccurrenceId, token.GroupedSurface);
            var target = FindCurrentSelectionControl(identity);
            if (target is null
                || !IsReviewFocusRestorationRelevant(token, expectedDialogFocusTransitionGeneration))
                return;
            isRestoringReviewFocus = true;
            try
            {
                Activate();
                if (IsReviewFocusRestorationRelevant(token, expectedDialogFocusTransitionGeneration)
                    && CanReceiveFocus(target) && target.Focus())
                    Keyboard.Focus(target);
            }
            finally
            {
                isRestoringReviewFocus = false;
            }
        }, DispatcherPriority.Input);
    }

    private bool IsReviewFocusRestorationRelevant(
        ReviewFocusRestorationToken token,
        long expectedDialogFocusTransitionGeneration) =>
        !isClosed
        && token.DataContextGeneration == dataContextGeneration
        && token.FocusGeneration == reviewFocusGeneration
        && reviewDialogFocusTransitionDepth == 0
        && expectedDialogFocusTransitionGeneration == reviewDialogFocusTransitionGeneration
        && IsVisible
        && !Dispatcher.HasShutdownStarted
        && ReferenceEquals(token.ExpectedViewModel, shellViewModel)
        && token.ExpectedViewModel.IsReviewPhase
        && token.ExpectedViewModel.ReviewSession == token.Session
        && reviewFocusIdentity == new ReviewFocusIdentity(token.Session, token.GroupId,
            token.OccurrenceId, token.GroupedSurface);

    private ReviewSelectionCheckBox? FindCurrentSelectionControl(
        ReviewFocusIdentity identity)
    {
        foreach (var control in Descendants<ReviewSelectionCheckBox>(ReviewGalleryList))
        {
            if (control.DataContext is not ScanOccurrenceViewModel member
                || member.OccurrenceId != identity.OccurrenceId)
                continue;
            if (FindReviewGroupId(control) == identity.GroupId && CanReceiveFocus(control)) return control;
        }
        return null;
    }

    private static bool CanReceiveFocus(UIElement element) =>
        element.IsVisible && element.IsEnabled && element.Focusable
        && PresentationSource.FromVisual(element) is not null;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        CancelBlockingReviewLayer();
        if (closeAfterLifetime || shellViewModel is null) return;
        var disposal = shellViewModel.DisposeAsync();
        if (disposal.IsCompletedSuccessfully)
        {
            closeAfterLifetime = true;
            return;
        }

        eventArgs.Cancel = true;
        closeCompletionTask ??= CompleteLifetimeAndCloseAsync(disposal.AsTask());
    }

    public void OnApplicationExit(object? sender, EventArgs eventArgs)
    {
        CancelBlockingReviewLayer();
        if (shellViewModel is null) return;
        var disposal = shellViewModel.DisposeAsync();
        if (!disposal.IsCompletedSuccessfully)
            exitLifetimeObservationTask ??= ObserveLifetimeAsync(disposal.AsTask());
    }

    private async Task CompleteLifetimeAndCloseAsync(Task disposal)
    {
        try
        {
            await disposal;
        }
        catch (Exception)
        {
            System.Diagnostics.Trace.WriteLine(
                "ALBUMEN_DIAGNOSTIC stage=shutdown code=lifetime_dispose_failed");
        }
        finally
        {
            if (!isClosed && !Dispatcher.HasShutdownStarted)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (isClosed) return;
                    closeAfterLifetime = true;
                    Close();
                });
            }
        }
    }

    private static async Task ObserveLifetimeAsync(Task disposal)
    {
        try
        {
            await disposal.ConfigureAwait(false);
        }
        catch (Exception)
        {
            System.Diagnostics.Trace.WriteLine(
                "ALBUMEN_DIAGNOSTIC stage=shutdown code=lifetime_dispose_failed");
        }
    }

    private void CancelBlockingReviewLayer()
    {
        foreach (var dialog in OwnedWindows.OfType<SurvivorOverrideDialog>().ToArray())
            dialog.Close();
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        isClosed = true;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        dataContextGeneration++;
        if (shellViewModel is not null)
        {
            shellViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        if (System.Windows.Application.Current is { } application)
            application.Exit -= OnApplicationExit;
        reviewFocusIdentity = null;
        reviewFocusGeneration++;
    }

    internal sealed record ReviewFocusRestorationToken(
        ShellViewModel ExpectedViewModel,
        int DataContextGeneration,
        ReviewSessionPin Session,
        Guid GroupId,
        long OccurrenceId,
        bool GroupedSurface,
        long FocusGeneration);

    private sealed record ReviewFocusIdentity(
        ReviewSessionPin Session,
        Guid GroupId,
        long OccurrenceId,
        bool GroupedSurface);
}

public class ReviewAutomationBorder : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ReviewAutomationBorderPeer(this);
}

internal sealed class ReviewAutomationBorderPeer(ReviewAutomationBorder owner)
    : FrameworkElementAutomationPeer(owner)
{
    protected override string GetClassNameCore() => nameof(ReviewAutomationBorder);
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
}

public class ReviewAnnouncementTextBlock : TextBlock
{
    private string itemStatus = string.Empty;
    private ReviewAnnouncementDiagnosticBuffer? diagnosticBuffer;
    private AutomationPeer? expectedDiagnosticPeer;

    internal string ItemStatus => itemStatus;

    internal void EnableDiagnosticCapture()
    {
        diagnosticBuffer = new ReviewAnnouncementDiagnosticBuffer(Environment.CurrentManagedThreadId);
        expectedDiagnosticPeer = null;
    }

    internal ReviewAnnouncementDiagnosticSnapshot StopDiagnosticCapture()
    {
        var buffer = diagnosticBuffer;
        diagnosticBuffer = null;
        expectedDiagnosticPeer = null;
        return buffer?.Snapshot() ?? new([], false, false, 0);
    }

    internal void CaptureDiagnostic(
        ReviewAnnouncementDiagnosticStage stage,
        long sequence,
        int dataContextGeneration = 0,
        string? skipReason = null,
        AutomationPeer? peer = null)
    {
        diagnosticBuffer?.Append(new(
            stage,
            sequence,
            System.Diagnostics.Stopwatch.GetTimestamp(),
            Environment.CurrentManagedThreadId,
            dataContextGeneration,
            skipReason,
            peer is null ? 0 : RuntimeHelpers.GetHashCode(peer),
            expectedDiagnosticPeer is null ? 0 : RuntimeHelpers.GetHashCode(expectedDiagnosticPeer)));
    }

    internal void Announce(long sequence, string payload)
    {
        var previous = itemStatus;
        itemStatus = payload;
        CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.OwnerStatusSet,
            sequence);
        var peer = UIElementAutomationPeer.FromElement(this)
            ?? UIElementAutomationPeer.CreatePeerForElement(this);
        CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.PeerResolved,
            sequence, peer: peer);
        if (peer is ReviewAnnouncementTextBlockAutomationPeer announcementPeer)
            announcementPeer.RaiseAnnouncement(sequence, previous, payload);
    }

    protected override AutomationPeer OnCreateAutomationPeer()
    {
        var peer = new ReviewAnnouncementTextBlockAutomationPeer(this);
        if (diagnosticBuffer is not null) expectedDiagnosticPeer = peer;
        _ = peer.GetItemStatus();
        return peer;
    }
}

internal sealed class ReviewAnnouncementTextBlockAutomationPeer(ReviewAnnouncementTextBlock owner)
    : TextBlockAutomationPeer(owner)
{
    protected override string GetItemStatusCore()
    {
        var current = owner.ItemStatus;
        owner.CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.GetItemStatusCore,
            sequence: 0, peer: this);
        return current;
    }

    internal void RaiseAnnouncement(long sequence, string previous, string current)
    {
        owner.CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.PropertyRaiseBegin,
            sequence, peer: this);
        _ = GetItemStatus();
        RaisePropertyChangedEvent(AutomationElementIdentifiers.ItemStatusProperty, previous, current);
        owner.CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.PropertyRaiseReturn,
            sequence, peer: this);
        owner.CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.LiveRaiseBegin,
            sequence, peer: this);
        RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        owner.CaptureDiagnostic(ReviewAnnouncementDiagnosticStage.LiveRaiseReturn,
            sequence, peer: this);
    }
}

internal enum ReviewAnnouncementDiagnosticStage
{
    Queue,
    Execute,
    Skip,
    OwnerStatusSet,
    PeerResolved,
    PropertyRaiseBegin,
    PropertyRaiseReturn,
    LiveRaiseBegin,
    LiveRaiseReturn,
    GetItemStatusCore,
}

internal readonly record struct ReviewAnnouncementDiagnosticRecord(
    ReviewAnnouncementDiagnosticStage Stage,
    long NotificationSequence,
    long Timestamp,
    int ThreadId,
    int DataContextGeneration,
    string? SkipReason,
    int PeerIdentity,
    int ExpectedPeerIdentity);

internal sealed record ReviewAnnouncementDiagnosticSnapshot(
    ReviewAnnouncementDiagnosticRecord[] Records,
    bool CapacityOverflow,
    bool WrongThread,
    int OwnerThreadId);

internal sealed class ReviewAnnouncementDiagnosticBuffer(int ownerThreadId)
{
    private const int Capacity = 256;
    private readonly ReviewAnnouncementDiagnosticRecord[] records = new ReviewAnnouncementDiagnosticRecord[Capacity];
    private int writeIndex;
    private bool capacityOverflow;
    private bool wrongThread;

    internal void Append(ReviewAnnouncementDiagnosticRecord record)
    {
        if (record.ThreadId != ownerThreadId)
        {
            wrongThread = true;
            return;
        }
        if (writeIndex >= records.Length)
        {
            capacityOverflow = true;
            return;
        }
        records[writeIndex++] = record;
    }

    internal ReviewAnnouncementDiagnosticSnapshot Snapshot()
    {
        var captured = new ReviewAnnouncementDiagnosticRecord[writeIndex];
        Array.Copy(records, captured, writeIndex);
        return new(captured, capacityOverflow, wrongThread, ownerThreadId);
    }
}

public class ReviewSelectionCheckBox : CheckBox
{
    private bool pressedShownChoice;

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ReviewSelectionCheckBoxAutomationPeer(this);

    // The Keep | Remove style looks like two choices, so a mouse click on the choice that
    // is already shown leaves it as it is instead of flipping it. Keyboard and UI
    // Automation still toggle.
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        pressedShownChoice = GetTemplateChild(IsChecked == true ? "RemovePart" : "KeepPart")
            is UIElement { IsMouseOver: true };
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        pressedShownChoice = false;
        base.OnKeyDown(e);
    }

    protected override void OnClick()
    {
        if (pressedShownChoice)
        {
            pressedShownChoice = false;
            return;
        }
        base.OnClick();
    }

    protected override void OnToggle()
    {
        // Selection is projected exclusively from the coordinator snapshot.
        // OnClick still executes the bound command for every activation path.
    }

    internal void ActivateFromAutomation() => OnClick();
}

internal sealed class ReviewSelectionCheckBoxAutomationPeer(ReviewSelectionCheckBox owner)
    : CheckBoxAutomationPeer(owner), IToggleProvider
{
    private ReviewSelectionCheckBox ReviewOwner => (ReviewSelectionCheckBox)Owner;

    void IToggleProvider.Toggle()
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();
        ReviewOwner.Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(ReviewOwner.ActivateFromAutomation));
    }

    ToggleState IToggleProvider.ToggleState => ReviewOwner.IsChecked switch
    {
        true => ToggleState.On,
        false => ToggleState.Off,
        null => ToggleState.Indeterminate,
    };
}


