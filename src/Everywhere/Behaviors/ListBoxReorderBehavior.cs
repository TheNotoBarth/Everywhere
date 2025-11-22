using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;

namespace Everywhere.Behaviors;

public class ListBoxReorderBehavior : Behavior<ListBox>
{
    // Constants for configuration
    private const double ScrollMargin = 30;
    private const double ScrollStep = 10;
    private const double MaxScrollStep = 20;
    private const double ScrollAcceleration = 0.1;
    private const int AnimationDurationMs = 180;
    private const double DirectionChangeThreshold = 2.0;

    private ListBoxItem? _draggedItem;
    private object? _draggedData;
    private Point _lastMousePosition;
    private Point _previousMousePosition;
    private bool _isDragging;
    private DispatcherTimer? _autoScrollTimer;
    private ScrollViewer? _scrollViewer;
    private double _currentScrollDelta;
    
    // Fields for stable positioning
    private Avalonia.Vector _initialScrollOffset;
    private Point _initialParentPos;
    private Point _dragOffsetRoot;
    private ListBoxItem? _insertTargetItem;
    private int _draggedIndex = -1;
    private int? _insertIndex;
    private bool _hasMoved = false;
    // Track last stable movement direction to avoid jitter
    private bool _lastMovingDown = false;

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject != null)
        {
            AssociatedObject.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            AssociatedObject.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
            AssociatedObject.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel);
        }
    }

    protected override void OnDetaching()
    {
        base.OnDetaching();
        if (AssociatedObject != null)
        {
            AssociatedObject.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            AssociatedObject.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
            AssociatedObject.RemoveHandler(InputElement.PointerReleasedEvent, OnPointerReleased);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(AssociatedObject);
        if (!point.Properties.IsLeftButtonPressed) return;

        var visual = e.Source as Visual;
        if (visual != null && visual.FindAncestorOfType<Border>(true) is { Classes: { } classes } && classes.Contains("DragHandle"))
        {
            var item = visual.FindAncestorOfType<ListBoxItem>();
            if (item != null && item.Parent is Visual parent)
            {
                _draggedItem = item;
                _draggedData = item.DataContext;
                _lastMousePosition = point.Position;
                _previousMousePosition = point.Position;
                _scrollViewer = AssociatedObject?.FindDescendantOfType<ScrollViewer>();
                
                // Capture initial state
                _initialScrollOffset = _scrollViewer?.Offset ?? default;
                if (AssociatedObject != null)
                {
                    _initialParentPos = parent.TranslatePoint(new Point(0, 0), AssociatedObject) ?? default;
                }
                
                // Calculate drag offset
                var itemPosInRoot = _initialParentPos + item.Bounds.Position;
                _dragOffsetRoot = itemPosInRoot - _lastMousePosition;

                _isDragging = true;

                // Set drag flags and index
                _draggedIndex = AssociatedObject?.IndexFromContainer(item) ?? -1;
                SetupDraggedContainer(item);

                _hasMoved = false;

                e.Handled = true;
            }
        }
    }

    private void OnDraggedItemPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty)
        {
            UpdateTransform();
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || _draggedItem == null || AssociatedObject == null) return;

        var point = e.GetCurrentPoint(AssociatedObject);
        _previousMousePosition = _lastMousePosition;
        _lastMousePosition = point.Position;

        // Handle first move: ensure the item is moved to the end of the list
        if (!_hasMoved && AssociatedObject != null && AssociatedObject.ItemsSource is System.Collections.IList items && _draggedIndex != -1)
        {
            int newIndex = items.Count - 1;
            if (_draggedIndex != newIndex)
            {
                // Perform animations and move data
                EnsureImplicitAnimationsForVisibleItems(exclude: _draggedItem, durationMs: AnimationDurationMs);
                MoveItem(_draggedIndex, newIndex);

                // The container moved to the end; ensure we're dragging the correct container
                try
                {
                    var newContainer = AssociatedObject!.GetVisualDescendants().OfType<ListBoxItem>()
                        .FirstOrDefault(c => c.DataContext == _draggedData);
                    if (newContainer != null)
                    {
                        TransferDragStateToNewContainer(newContainer);
                        UpdateTransform();
                    }
                }
                catch { }


                _draggedIndex = newIndex;
            }
            _hasMoved = true;
        }

        UpdateTransform();
        UpdateInsertIndicator();

        // Handle auto-scroll
        HandleAutoScroll(_lastMousePosition);
        e.Handled = true;
    }

    private void UpdateTransform()
    {
        if (_draggedItem == null || AssociatedObject == null) return;

        // Calculate target position in Root coordinates
        var targetPosInRoot = _lastMousePosition + _dragOffsetRoot;
        
        // Calculate current Parent position in Root coordinates
        var currentScrollOffset = _scrollViewer?.Offset ?? default;
        var scrollDiff = currentScrollOffset - _initialScrollOffset;

        var currentParentPosInRoot = _initialParentPos - (Point)scrollDiff;
        
        // Calculate required TranslateTransform
        var itemLayoutPos = _draggedItem.Bounds.Position;
        var transform = targetPosInRoot - currentParentPosInRoot - itemLayoutPos;

        if (_draggedItem.RenderTransform is TranslateTransform tt)
        {
            tt.X = 0;
            tt.Y = transform.Y;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging && _draggedItem != null)
        {
            if (_hasMoved && _insertIndex.HasValue && _draggedIndex != -1 && AssociatedObject != null && AssociatedObject.ItemsSource is System.Collections.IList items)
            {
                var listBox = AssociatedObject!;
                var newIndex = _insertIndex.Value;
                if (newIndex > _draggedIndex)
                {
                    newIndex--; // because removal shifts indices left
                }

                EnsureImplicitAnimationsForVisibleItems(exclude: _draggedItem, durationMs: AnimationDurationMs);
                MoveItem(_draggedIndex, newIndex);

            }

            // Clear insert indicator
            ClearInsertIndicator();

            // Clean up drag state and visuals
            if (AssociatedObject != null)
            {
                try
                {
                    var currentContainer = AssociatedObject.GetVisualDescendants().OfType<ListBoxItem>()
                        .FirstOrDefault(c => c.DataContext == _draggedData) ?? _draggedItem;

                    CleanupDraggedContainer(currentContainer);
                }
                catch { }
            }

            _isDragging = false;
            _draggedItem = null;
            _draggedData = null;
            _draggedIndex = -1;
            _hasMoved = false;
            StopAutoScroll();

            e.Handled = true;
        }
    }

    private void EnsureImplicitAnimationsForVisibleItems(ListBoxItem? exclude, int durationMs = 180)
    {
        if (AssociatedObject == null) return;

        var items = AssociatedObject.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(c => c != exclude)
            .ToList();

        foreach (var item in items)
        {
            var visual = ElementComposition.GetElementVisual(item);
            if (visual == null || visual.ImplicitAnimations != null) continue; // skip items that already have animations set

            var compositor = visual.Compositor;
            var animationGroup = compositor.CreateAnimationGroup();

            var offsetAnimation = compositor.CreateVector3KeyFrameAnimation();
            offsetAnimation.Target = "Offset";
            offsetAnimation.InsertExpressionKeyFrame(1.0f, "this.FinalValue");
            offsetAnimation.Duration = TimeSpan.FromMilliseconds(durationMs);
            animationGroup.Add(offsetAnimation);

            var implicitAnimations = compositor.CreateImplicitAnimationCollection();
            implicitAnimations["Offset"] = animationGroup;

            visual.ImplicitAnimations = implicitAnimations;
        }
    }

    private void MoveItem(int oldIndex, int newIndex, bool restoreScroll = true)
    {
        if (AssociatedObject?.ItemsSource is System.Collections.IList items)
        {
            // Avoid automatic scrolling caused by Move
            var prevOffset = _scrollViewer?.Offset ?? default;

            // Try to call Move if backed by ObservableCollection<T>
            var type = items.GetType();
            var moveMethod = type.GetMethod("Move");
            if (moveMethod != null)
            {
                try
                {
                    moveMethod.Invoke(items, new object[] { oldIndex, newIndex });
                }
                catch
                {
                    // fall through to manual move
                    var item = items[oldIndex];
                    items.RemoveAt(oldIndex);
                    items.Insert(newIndex, item);
                }
            }
            else
            {
                var item = items[oldIndex];
                items.RemoveAt(oldIndex);
                items.Insert(newIndex, item);
            }
            
                if (restoreScroll && _scrollViewer != null)
            {
                try
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            _scrollViewer.Offset = prevOffset;
                        }
                        catch { }
                    });
                }
                catch { }
            }
        }
    }

    private void TransferDragStateToNewContainer(ListBoxItem newContainer)
    {
        // Clean up old container
        CleanupDraggedContainer(_draggedItem);

        // Set up new container
        _draggedItem = newContainer;
        SetupDraggedContainer(newContainer);
    }

    private void SetupDraggedContainer(ListBoxItem container)
    {
        container.ZIndex = 1000;
        container.Classes.Add("Dragging");
        container.RenderTransform = new TranslateTransform();
        try { container.PropertyChanged += OnDraggedItemPropertyChanged; } catch { }
    }

    private void CleanupDraggedContainer(ListBoxItem? container)
    {
        if (container != null)
        {
            container.ZIndex = 0;
            container.Classes.Remove("Dragging");
            container.RenderTransform = null;
            try { container.PropertyChanged -= OnDraggedItemPropertyChanged; } catch { }
        }
    }

    private void HandleAutoScroll(Point position)
    {
        if (_scrollViewer == null) return;

        var bounds = AssociatedObject!.Bounds;
        
        _currentScrollDelta = 0;

        if (position.Y < ScrollMargin)
        {
            var distance = ScrollMargin - position.Y;
            var step = ScrollStep + distance * ScrollAcceleration;
            step = Math.Min(step, MaxScrollStep);
            _currentScrollDelta = -step;
        }
        else if (position.Y > bounds.Height - ScrollMargin)
        {
            var distance = position.Y - (bounds.Height - ScrollMargin);
            var step = ScrollStep + distance * ScrollAcceleration;
            step = Math.Min(step, MaxScrollStep);
            _currentScrollDelta = step;
        }

        if (_currentScrollDelta != 0)
        {
            if (_autoScrollTimer == null)
            {
                _autoScrollTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };
                _autoScrollTimer.Tick += AutoScrollTimer_Tick;
                _autoScrollTimer.Start();
            }
        }
        else
        {
            StopAutoScroll();
        }
    }

    private void AutoScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (_scrollViewer == null || _currentScrollDelta == 0 || _draggedItem == null) return;

        var currentOffset = _scrollViewer.Offset;
        var newOffset = new Avalonia.Vector(currentOffset.X, currentOffset.Y + _currentScrollDelta);
        _scrollViewer.Offset = newOffset;
        
        UpdateTransform();
        UpdateInsertIndicator();
    }

    private void StopAutoScroll()
    {
        _autoScrollTimer?.Stop();
        if (_autoScrollTimer != null)
        {
            _autoScrollTimer.Tick -= AutoScrollTimer_Tick;
            _autoScrollTimer = null;
        }
    }

    private void ClearInsertIndicator()
    {
        if (_insertTargetItem != null)
        {
            _insertTargetItem.Classes.Remove("InsertBefore");
            _insertTargetItem.Classes.Remove("InsertAfter");
            _insertTargetItem = null;
            _insertIndex = null;
        }
    }

    private void UpdateInsertIndicator()
    {
        if (AssociatedObject == null) return;

        // Determine mouse movement direction
        var deltaY = _lastMousePosition.Y - _previousMousePosition.Y;
        bool movingDown;
        if (deltaY > DirectionChangeThreshold)
        {
            movingDown = true;
        }
        else if (deltaY < -DirectionChangeThreshold)
        {
            movingDown = false;
        }
        else
        {
            movingDown = _lastMovingDown;
        }
        _lastMovingDown = movingDown;


        // Get all item containers except the dragged one
        var containers = AssociatedObject.GetVisualDescendants().OfType<ListBoxItem>()
            .Select(c => new { Container = c, Index = AssociatedObject.IndexFromContainer(c) })
            .Where(x => x.Index != -1 && x.Container != _draggedItem)
            .OrderBy(x => x.Index)
            .ToList();

        int? computedIndex = null;
        if (containers.Count == 0)
        {
            computedIndex = 0;
            _insertIndex = computedIndex;
            return;
        }

        // Find the container under the pointer
        var targetEntry = containers.FirstOrDefault(x =>
        {
            var pos = x.Container.TranslatePoint(new Point(0, 0), AssociatedObject) ?? new Point(0, 0);
            return _lastMousePosition.Y >= pos.Y && _lastMousePosition.Y <= pos.Y + x.Container.Bounds.Height;
        });

        if (targetEntry != null)
        {
            ClearInsertIndicator();
            var pos = targetEntry.Container.TranslatePoint(new Point(0, 0), AssociatedObject) ?? new Point(0, 0);
            var centerY = pos.Y + targetEntry.Container.Bounds.Height / 2;

            if (_lastMousePosition.Y < centerY)
            {
                // Pointer in upper half: logical insert position is targetEntry.Index (insert above it)
                computedIndex = targetEntry.Index;

                // Visual: if moving up, show targetEntry's top insert line; otherwise show previous container's bottom insert line (if any)
                if (!movingDown)
                {
                    _insertTargetItem = targetEntry.Container;
                    _insertTargetItem.Classes.Add("InsertBefore");
                }
                else
                {
                    var prev = containers.LastOrDefault(x => x.Index < targetEntry.Index)?.Container;
                    if (prev != null)
                    {
                        _insertTargetItem = prev;
                        _insertTargetItem.Classes.Add("InsertAfter");
                    }
                    else
                    {
                        _insertTargetItem = targetEntry.Container;
                        _insertTargetItem.Classes.Add("InsertBefore");
                    }
                }
            }
            else
            {
                // Pointer in lower half: logical insert position is targetEntry.Index + 1 (insert below it)
                computedIndex = targetEntry.Index + 1;

                // Visual: if moving down, show targetEntry's bottom insert line; otherwise show next container's top insert line (if any)
                if (movingDown)
                {
                    _insertTargetItem = targetEntry.Container;
                    _insertTargetItem.Classes.Add("InsertAfter");
                }
                else
                {
                    var next = containers.FirstOrDefault(x => x.Index > targetEntry.Index)?.Container;
                    if (next != null)
                    {
                        _insertTargetItem = next;
                        _insertTargetItem.Classes.Add("InsertBefore");
                    }
                    else
                    {
                        _insertTargetItem = targetEntry.Container;
                        _insertTargetItem.Classes.Add("InsertAfter");
                    }
                }
            }
        }
        else
        {
            // pointer not inside any container: above first or below last
            var first = containers.First();
            var last = containers.Last();
            var firstPos = first.Container.TranslatePoint(new Point(0, 0), AssociatedObject) ?? new Point(0, 0);
            var lastPos = last.Container.TranslatePoint(new Point(0, 0), AssociatedObject) ?? new Point(0, 0);
            if (_lastMousePosition.Y < firstPos.Y)
            {
                ClearInsertIndicator();
                computedIndex = first.Index;
                // no previous container available -> show first's top insert
                _insertTargetItem = first.Container;
                _insertTargetItem.Classes.Add("InsertBefore");
            }
            else if (_lastMousePosition.Y > lastPos.Y + last.Container.Bounds.Height)
            {
                ClearInsertIndicator();
                computedIndex = last.Index + 1;
                // no next container available -> show last's bottom insert
                _insertTargetItem = last.Container;
                _insertTargetItem.Classes.Add("InsertAfter");
            }
            else
            {
                    // Keep unchanged when pointer is in a gap between items
            }
        }

        _insertIndex = computedIndex;
    }
}
