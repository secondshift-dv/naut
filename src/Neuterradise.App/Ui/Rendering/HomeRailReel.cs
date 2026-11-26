using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Neuterradise.App.Home;
using Neuterradise.App.Presentation;
using Neuterradise.App.SystemServices.Resources;
using Windows.Foundation;
using Windows.System;

namespace Neuterradise.App.Ui;

/// <summary>Bounded cyclic viewport: fractional motion translates one layer. Only crossing a card
/// boundary changes the virtual window; only entering slots receive a new binding.</summary>
internal sealed class HomeRailReel : Grid
{
    private sealed class Slot(HomeProfileTile tile)
    {
        public HomeProfileTile Tile { get; } = tile;
        public TranslateTransform Position { get; } = new();
        public long Index { get; set; } = long.MinValue;
    }

    private readonly Canvas _strip = new();
    private readonly TranslateTransform _motion = new();
    private readonly RectangleGeometry _clip = new();
    private readonly List<Slot> _slots = [];
    private readonly IReadOnlyList<HomeProfileTileViewModel> _items;
    private readonly HomeRailPlan _plan;
    private readonly double _width;
    private readonly double _gap;
    private readonly double _pitch;
    private readonly double _inset;
    private readonly Func<bool> _isActive;
    private readonly ScrollViewer _outer;
    private readonly Action<Guid> _navigate;
    private readonly Action<Guid> _warm;
    private HomeReelPosition _position;
    private Guid? _previewId;
    private MediaTransformState? _previewTransform;
    private bool _loaded;
    private bool _disposed;
    private bool _running;
    private bool _loop;
    private bool _hover;
    private bool _keyboardFocus;
    private bool _dragging;
    private bool _suppressClick;
    private uint? _pointer;
    private double _startX;
    private double _lastX;
    private double _viewport;
    private long _lastFrame;

    public HomeRailReel(IReadOnlyList<HomeProfileTileViewModel> items, HomeRailPlan plan, double width, double gap,
        Func<bool> isActive, ScrollViewer outer, Action<Guid> navigate, Action<Guid> warm)
    {
        _items = items;
        _plan = plan;
        _width = width;
        _gap = gap;
        _pitch = width + gap;
        _inset = Math.Ceiling(width * 0.10);
        _isActive = isActive;
        _outer = outer;
        _navigate = navigate;
        _warm = warm;
        Height = width + 2 * _inset;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Top;
        Background = ThemeRuntime.Current.Brush("transparent");
        Clip = _clip;
        _strip.RenderTransform = _motion;
        Children.Add(_strip);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        PointerEntered += OnEntered;
        PointerExited += OnExited;
        GotFocus += OnGotFocus;
        LostFocus += OnLostFocus;
        AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPressed), true);
        AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnMoved), true);
        AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnReleased), true);
        AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnReleased), true);
        PointerCaptureLost += OnCaptureLost;
        PointerWheelChanged += OnWheel;
        KeyDown += OnKeyDown;
    }

    public bool Matches(IReadOnlyList<HomeProfileTileViewModel> items, int count, HomeRailPlan plan, double width, double gap)
    {
        if (_disposed || _plan != plan || Math.Abs(_width - width) >= 0.01 || Math.Abs(_gap - gap) >= 0.01
            || _items.Count != count) return false;
        for (var i = 0; i < count; i++)
        {
            var a = _items[i];
            var b = items[i];
            if (!ReferenceEquals(a, b) && (a.ProfileId != b.ProfileId || a.DisplayName != b.DisplayName
                || a.CoverImagePath != b.CoverImagePath || a.MediaCount != b.MediaCount
                || a.IsFavorite != b.IsFavorite || a.CoverTransform != b.CoverTransform)) return false;
        }
        return true;
    }

    private double Extent => Math.Max(0, _items.Count * _pitch - _gap);
    private bool CanScroll => _viewport > 0 && Extent > _viewport + 1;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_disposed || _loaded) return;
        _loaded = true;
        ResourceGovernor.Shared.Changed += OnPolicyChanged;
        ReducedMotionAuthority.Changed += OnPolicyChanged;
        RefreshActivity();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_loaded) return;
        _loaded = false;
        ResourceGovernor.Shared.Changed -= OnPolicyChanged;
        ReducedMotionAuthority.Changed -= OnPolicyChanged;
        _hover = _keyboardFocus = _dragging = _suppressClick = false;
        _pointer = null;
        ReleasePointerCaptures();
        Stop();
    }

    private void OnPolicyChanged(object? sender, EventArgs args) => UiDispatch.Run(RefreshActivity);
    private void OnSizeChanged(object sender, SizeChangedEventArgs args)
    {
        RefreshActivity();
    }

    private void UpdateGeometry()
    {
        if (_disposed || !_loaded || !_isActive() || !double.IsFinite(ActualWidth) || ActualWidth <= 0) return;
        var width = ActualWidth;
        _clip.Rect = new Rect(0, 0, width, ActualHeight);
        if (Math.Abs(_viewport - width) < 0.01) return;
        _viewport = width;
        var loop = _plan.CanReel(_items.Count, _width, _gap, width);
        if (loop != _loop)
        {
            _loop = loop;
            _position = default;
        }
        if (!_loop)
            _position = HomeReelPosition.FromOffset(Math.Clamp(_position.Offset(_pitch), 0, Math.Max(0, Extent - width)), _pitch);
        UpdateWindow();
        _motion.X = -_position.Phase;
    }

    private Slot CreateSlot()
    {
        var tile = new HomeProfileTile("cover-only", _width, _width,
            id => { if (!_dragging && !_suppressClick) _navigate(id); });
        tile.PointerEntered += (_, _) => Warm(tile.ProfileId);
        tile.GotFocus += (_, _) => Warm(tile.ProfileId);
        var slot = new Slot(tile);
        tile.RenderTransform = slot.Position;
        Canvas.SetTop(tile, _inset);
        _strip.Children.Add(tile);
        _slots.Add(slot);
        return slot;
    }

    private void Warm(Guid id)
    {
        if (id != Guid.Empty && _isActive() && ResourceGovernor.Shared.Tier == PresentationTier.Full
            && !ResourceGovernor.Shared.IsMemoryPressure) _warm(id);
    }

    public void SetPreviewCover(Guid? id, MediaTransformState? transform)
    {
        if (_previewId == id && _previewTransform == transform) return;
        _previewId = id;
        _previewTransform = transform;
        foreach (var slot in _slots)
        {
            var item = _items[HomeReelPosition.ItemIndex(slot.Index, _items.Count)];
            slot.Tile.ApplyCoverTransform(CoverFor(item));
        }
    }

    private MediaTransformState CoverFor(HomeProfileTileViewModel item) =>
        item.ProfileId == _previewId && _previewTransform is { } preview ? preview : item.CoverTransform;

    private void UpdateWindow()
    {
        var (start, end) = _position.Window(_items.Count, _pitch, _viewport, _loop);
        for (var index = start; index < end; index++)
        {
            Slot? slot = null;
            foreach (var candidate in _slots)
                if (candidate.Index == index) { slot = candidate; break; }
            if (slot is null)
            {
                foreach (var candidate in _slots)
                    if (candidate.Index < start || candidate.Index >= end) { slot = candidate; break; }
                slot ??= CreateSlot();
                slot.Index = index;
                var item = _items[HomeReelPosition.ItemIndex(index, _items.Count)];
                slot.Tile.Bind(item.ProfileId, item.DisplayName, item.CoverImagePath, item.MediaCountText, item.IsFavorite, CoverFor(item));
            }
            var local = index - _position.First;
            slot.Position.X = local * _pitch;
            slot.Tile.TabIndex = (int)Math.Max(0, local);
            slot.Tile.IsTabStop = local >= 0 && local * _pitch < _viewport;
        }
        for (var i = _slots.Count - 1; i >= 0; i--)
        {
            var slot = _slots[i];
            if (slot.Index >= start && slot.Index < end) continue;
            slot.Tile.Clear();
            _strip.Children.Remove(slot.Tile);
            _slots.RemoveAt(i);
        }
    }

    private void Move(double delta)
    {
        if (_disposed || !CanScroll || !double.IsFinite(delta)) return;
        var next = _loop ? _position.Advance(delta, _pitch)
            : HomeReelPosition.FromOffset(Math.Clamp(_position.Offset(_pitch) + delta, 0, Math.Max(0, Extent - _viewport)), _pitch);
        var boundary = next.First != _position.First;
        _position = next;
        if (boundary) UpdateWindow();
        _motion.X = -_position.Phase;
    }

    private void OnEntered(object sender, PointerRoutedEventArgs args) { _hover = true; RefreshActivity(); }
    private void OnExited(object sender, PointerRoutedEventArgs args)
    {
        _hover = false;
        if (!_dragging) _pointer = null;
        RefreshActivity();
    }

    private void OnGotFocus(object sender, RoutedEventArgs args)
    {
        _keyboardFocus = args.OriginalSource is Control { FocusState: FocusState.Keyboard };
        if (_keyboardFocus && args.OriginalSource is HomeProfileTile tile)
        {
            var slot = _slots.Find(s => ReferenceEquals(s.Tile, tile));
            if (slot is not null)
            {
                var x = (slot.Index - _position.First) * _pitch - _position.Phase;
                if (x < 0) Move(x);
                else if (x + _width > _viewport) Move(x + _width - _viewport);
            }
        }
        RefreshActivity();
    }

    private void OnLostFocus(object sender, RoutedEventArgs args) { _keyboardFocus = false; RefreshActivity(); }

    private bool InViewport()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return false;
        for (DependencyObject? node = this; node is FrameworkElement element; node = VisualTreeHelper.GetParent(node))
            if (element.Visibility != Visibility.Visible || element.Opacity <= 0) return false;
        if (_outer.Visibility != Visibility.Visible) return true;
        try
        {
            var bounds = TransformToVisual(_outer).TransformBounds(new Rect(0, 0, ActualWidth, ActualHeight));
            return bounds.Bottom > 0 && bounds.Right > 0 && bounds.X < _outer.ActualWidth && bounds.Y < _outer.ActualHeight;
        }
        catch (InvalidOperationException) { return false; }
    }

    public void RefreshActivity()
    {
        if (_disposed || !_loaded || !_isActive()) { Stop(); return; }
        UpdateGeometry();
        var governor = ResourceGovernor.Shared;
        var allowed = _loop && !_hover && !_keyboardFocus
            && _pointer is null && !ReducedMotionAuthority.IsReduced && governor.Tier == PresentationTier.Full
            && !governor.IsMemoryPressure && !governor.IsScrolling && InViewport();
        if (!allowed) { Stop(); return; }
        if (_running) return;
        _running = true;
        _lastFrame = Environment.TickCount64;
        PresentationVisualScheduler.StartClock(OnFrame);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        PresentationVisualScheduler.StopClock(OnFrame);
    }

    private void OnFrame(long now)
    {
        if (!_running || now - _lastFrame < 33) return;
        var delta = Math.Min(100, now - _lastFrame) * _plan.ReelSpeed / 1000;
        _lastFrame = now;
        Move(delta);
    }

    private static bool Pressed(PointerRoutedEventArgs args, FrameworkElement element)
    {
        var point = args.GetCurrentPoint(element);
        return args.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse
            ? point.Properties.IsLeftButtonPressed : point.IsInContact;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!CanScroll || !Pressed(args, this) || _pointer is not null) return;
        _pointer = args.Pointer.PointerId;
        _startX = _lastX = args.GetCurrentPoint(this).Position.X;
        _dragging = _suppressClick = false;
        Stop();
    }

    private void OnMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId) return;
        if (!Pressed(args, this)) { OnReleased(sender, args); return; }
        var x = args.GetCurrentPoint(this).Position.X;
        if (!_dragging)
        {
            if (Math.Abs(x - _startX) < 6 || !CapturePointer(args.Pointer)) return;
            _dragging = _suppressClick = true;
        }
        Move(_lastX - x);
        _lastX = x;
        ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Scroll);
        args.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId) return;
        var dragged = _dragging;
        _pointer = null;
        _dragging = false;
        if (dragged)
        {
            ReleasePointerCapture(args.Pointer);
            args.Handled = true;
            if (DispatcherQueue?.TryEnqueue(() => _suppressClick = false) != true) _suppressClick = false;
        }
        RefreshActivity();
    }

    private void OnCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_dragging && ReferenceEquals(args.OriginalSource, this)) OnReleased(sender, args);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        if (!CanScroll) return;
        var delta = args.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;
        Move(-delta / 120.0 * _pitch);
        ResourceGovernor.Shared.NotifyInteraction(InteractionSignal.Scroll);
        args.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        var focused = _slots.Find(slot => slot.Tile.FocusState != FocusState.Unfocused);
        if (focused is null) return;
        var target = focused.Index + (args.Key == VirtualKey.Right ? 1 : -1);
        if (!_loop && (target < 0 || target >= _items.Count)) return;
        var x = (target - _position.First) * _pitch - _position.Phase;
        if (x < 0) Move(x);
        else if (x + _width > _viewport) Move(x + _width - _viewport);
        _slots.Find(slot => slot.Index == target)?.Tile.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    public void Retire()
    {
        if (_disposed) return;
        _disposed = true;
        OnUnloaded(this, new RoutedEventArgs());
        Stop();
        foreach (var slot in _slots) slot.Tile.Clear();
        _strip.Children.Clear();
        _slots.Clear();
    }
}
