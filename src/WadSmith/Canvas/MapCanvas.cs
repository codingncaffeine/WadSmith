using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using SkiaSharp;
using WadSmith.Editor;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.App.Canvas;

/// <summary>
/// The 2D map view. Wheel zooms at the pointer; middle or right drag, or Space with left drag, pans;
/// a click selects the thing, corner, wall or area under the pointer (Shift adds or removes); Home
/// fits the map. The picture and the selection's outline are built on workers from the session's
/// geometry and drawn on the render thread, so the UI thread only takes input and updates the view.
/// </summary>
internal sealed class MapCanvas : Control
{
    public static readonly StyledProperty<EditorSession?> SessionProperty = AvaloniaProperty.Register<MapCanvas, EditorSession?>(nameof(Session));
    public static readonly StyledProperty<MapViewMode> ModeProperty = AvaloniaProperty.Register<MapCanvas, MapViewMode>(nameof(Mode));

    private readonly LatestOnWorker<(EditorSession Session, CanvasSnapshot Picture)> _pictures;
    private readonly LatestOnWorker<(EditorSession Session, long Generation, SelectionOverlay Outline)> _outlines;
    private CanvasSnapshot? _snapshot;
    private MapLook _look = new(null);
    private SelectionOverlay? _selection;
    private SelectionOverlay? _hoverOutline;
    private long _outlineGeneration;
    private double _cx;
    private double _cy;
    private double _zoom = 0.5;
    private bool _fitted;
    private Point? _panFrom;
    private bool _spaceDown;
    private Element? _hover;

    static MapCanvas()
    {
        FocusableProperty.OverrideDefaultValue<MapCanvas>(true);
    }

    public MapCanvas()
    {
        ClipToBounds = true;
        _pictures = new(ViewModels.MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session) || r.Picture.Mode != Mode || (_snapshot is not null && _snapshot.Mode == Mode && r.Picture.Version < _snapshot.Version)) return;
            _snapshot = r.Picture;
            InvalidateVisual();
        }, "the map picture");
        _outlines = new(ViewModels.MainViewModel.PostToUi, r =>
        {
            if (!ReferenceEquals(r.Session, Session) || r.Generation != _outlineGeneration) return;
            _selection = r.Outline;
            InvalidateVisual();
        }, "the selection outline");
    }

    public EditorSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    /// <summary>How the map is painted: floors, ceilings, heights or lines only.</summary>
    public MapViewMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>Raised when the element under the pointer changes (null over empty space).</summary>
    public event Action<Element?, MapPoint>? HoverChanged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ModeProperty)
        {
            RequestPicture();
            return;
        }

        if (change.Property != SessionProperty) return;
        if (change.OldValue is EditorSession old)
        {
            old.GeometryChanged -= OnGeometryChanged;
            old.Selection.Changed -= OnSelectionChanged;
        }

        _snapshot = null;
        _selection = null;
        _hover = null;
        _hoverOutline = null;
        _fitted = false;
        if (change.NewValue is EditorSession s)
        {
            _look = new MapLook(s.Resources);
            s.GeometryChanged += OnGeometryChanged;
            s.Selection.Changed += OnSelectionChanged;
            RequestPicture();
            OnSelectionChanged();
        }

        InvalidateVisual();
    }

    /// <summary>The map changed and its new shapes are ready: rebuild the picture and outlines from them.</summary>
    private void OnGeometryChanged()
    {
        RequestPicture();
        OnSelectionChanged();
        if (_hover is not null && Session is { } s) _hoverOutline = SelectionOverlay.Take([_hover], s.Geometry, _look);
    }

    private void RequestPicture()
    {
        if (Session is not { } session) return;
        var geometry = session.Geometry;
        var look = _look;
        var mode = Mode;
        _pictures.Request(() =>
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var picture = CanvasSnapshot.Take(geometry, look, mode);
            FrameStats.Snapshot(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return (session, picture);
        });
    }

    /// <summary>The selection's outline, built on a worker (a selection can be the whole map).</summary>
    private void OnSelectionChanged()
    {
        if (Session is not { } session) return;
        var generation = ++_outlineGeneration;
        if (session.Selection.IsEmpty)
        {
            _selection = null;
            InvalidateVisual();
            return;
        }

        var items = session.Selection.Items.ToArray();
        var geometry = session.Geometry;
        var look = _look;
        _outlines.Request(() => (session, generation, SelectionOverlay.Take(items, geometry, look)));
        InvalidateVisual();
    }

    /// <summary>Centres the map and zooms so all of it shows.</summary>
    public void FitMap()
    {
        ComputeFit();
        InvalidateVisual();
    }

    /// <summary>The fit itself, without asking for a redraw (Render calls it, and a render pass may not invalidate).</summary>
    private void ComputeFit()
    {
        if (Session is null || Bounds.Width < 1) return;
        var (minX, minY, maxX, maxY) = Session.Geometry.Extent();
        _cx = (minX + maxX) / 2;
        _cy = (minY + maxY) / 2;
        _zoom = Math.Min((Bounds.Width - 48) / Math.Max(maxX - minX, 64), (Bounds.Height - 48) / Math.Max(maxY - minY, 64));
        _fitted = true;
    }

    private MapPoint ToMap(Point p) => new(_cx + (p.X - Bounds.Width / 2) / _zoom, _cy - (p.Y - Bounds.Height / 2) / _zoom);

    public override void Render(DrawingContext context)
    {
        if (Session is null) return;
        if (!_fitted) ComputeFit();
        var hover = _hover is not null && !Session.Selection.Contains(_hover) ? _hoverOutline : null;
        context.Custom(new MapDrawOperation(new Rect(Bounds.Size), _snapshot, _selection, hover, new View(_cx, _cy, _zoom, Bounds.Width, Bounds.Height)));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var before = ToMap(p);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.18, e.Delta.Y), 0.005, 64);
        var after = ToMap(p);
        _cx += before.X - after.X;
        _cy += before.Y - after.Y;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed || (props.IsLeftButtonPressed && _spaceDown))
        {
            _panFrom = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed && Session is not null)
        {
            var hit = HitTest(e.GetPosition(this));
            if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
            {
                if (hit is not null) Session.Selection.Toggle(hit);
            }
            else if (hit is null)
            {
                Session.Selection.Clear();
            }
            else
            {
                Session.Selection.Set(hit);
            }

            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_panFrom is { } from)
        {
            _cx -= (p.X - from.X) / _zoom;
            _cy += (p.Y - from.Y) / _zoom;
            _panFrom = p;
            InvalidateVisual();
            return;
        }

        var hit = HitTest(p);
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            _hoverOutline = hit is null || Session is null ? null : SelectionOverlay.Take([hit], Session.Geometry, _look);
            InvalidateVisual();
        }

        HoverChanged?.Invoke(hit, ToMap(p));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_panFrom is not null)
        {
            _panFrom = null;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_hover is null) return;
        _hover = null;
        _hoverOutline = null;
        InvalidateVisual();
        HoverChanged?.Invoke(null, default);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                _spaceDown = true;
                e.Handled = true;
                break;
            case Key.Home:
                FitMap();
                e.Handled = true;
                break;
            case Key.Escape:
                Session?.Selection.Clear();
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add:
                _zoom = Math.Min(_zoom * 1.4, 64);
                InvalidateVisual();
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                _zoom = Math.Max(_zoom / 1.4, 0.005);
                InvalidateVisual();
                e.Handled = true;
                break;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Space) _spaceDown = false;
    }

    /// <summary>Zooms about the centre (for tools that measure drawing).</summary>
    internal void ZoomBy(double factor)
    {
        _zoom *= factor;
        InvalidateVisual();
    }

    /// <summary>Moves the view by screen pixels (for tools that measure panning).</summary>
    internal void PanBy(double dx, double dy)
    {
        _cx -= dx / _zoom;
        _cy += dy / _zoom;
        InvalidateVisual();
    }

    /// <summary>What a click at this spot picks: a thing, then a corner (zoomed in), then a wall, then the area.</summary>
    private Element? HitTest(Point p)
    {
        if (Session is null) return null;
        var geometry = Session.Geometry;
        var m = ToMap(p);
        var px = 7 / _zoom;
        if (geometry.ThingNear(m, Math.Max(16, px)) is { } t) return t;
        if (_zoom > 0.25 && geometry.VertexNear(m, px) is { } v) return v;
        if (geometry.LineNear(m, px) is { } l) return l;
        return geometry.SectorAt(m);
    }

    internal readonly record struct View(double Cx, double Cy, double Zoom, double Width, double Height)
    {
        public SKMatrix Matrix => SKMatrix.CreateScaleTranslation((float)Zoom, (float)-Zoom, (float)(Width / 2 - Cx * Zoom), (float)(Height / 2 + Cy * Zoom));
    }
}
