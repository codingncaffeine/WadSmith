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
/// fits the map. Drawing runs on the render thread from an immutable snapshot, so the UI thread only
/// takes input and updates the view.
/// </summary>
internal sealed class MapCanvas : Control
{
    public static readonly StyledProperty<EditorSession?> SessionProperty = AvaloniaProperty.Register<MapCanvas, EditorSession?>(nameof(Session));
    public static readonly StyledProperty<bool> ShowHeightsProperty = AvaloniaProperty.Register<MapCanvas, bool>(nameof(ShowHeights));

    private MapGeometry? _geometry;
    private CanvasSnapshot? _snapshot;
    private double _cx;
    private double _cy;
    private double _zoom = 0.5;
    private bool _fitted;
    private Point? _panFrom;
    private bool _spaceDown;
    private Element? _hover;

    static MapCanvas()
    {
        AffectsRender<MapCanvas>(ShowHeightsProperty);
        FocusableProperty.OverrideDefaultValue<MapCanvas>(true);
    }

    public MapCanvas() => ClipToBounds = true;

    public EditorSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public bool ShowHeights
    {
        get => GetValue(ShowHeightsProperty);
        set => SetValue(ShowHeightsProperty, value);
    }

    /// <summary>Raised when the element under the pointer changes (null over empty space).</summary>
    public event Action<Element?, MapPoint>? HoverChanged;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SessionProperty) return;
        if (change.OldValue is EditorSession old)
        {
            old.Map.Changed -= OnMapChanged;
            old.Selection.Changed -= InvalidateVisual;
        }

        if (change.NewValue is EditorSession s)
        {
            s.Map.Changed += OnMapChanged;
            s.Selection.Changed += InvalidateVisual;
            _geometry = new MapGeometry(s.Map);
            _snapshot = null;
            _fitted = false;
        }

        InvalidateVisual();
    }

    private void OnMapChanged(long version)
    {
        _snapshot = null;
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
        if (_geometry is null || Bounds.Width < 1) return;
        var (minX, minY, maxX, maxY) = _geometry.Extent();
        _cx = (minX + maxX) / 2;
        _cy = (minY + maxY) / 2;
        _zoom = Math.Min((Bounds.Width - 48) / Math.Max(maxX - minX, 64), (Bounds.Height - 48) / Math.Max(maxY - minY, 64));
        _fitted = true;
    }

    private MapPoint ToMap(Point p) => new(_cx + (p.X - Bounds.Width / 2) / _zoom, _cy - (p.Y - Bounds.Height / 2) / _zoom);

    public override void Render(DrawingContext context)
    {
        if (Session is null || _geometry is null) return;
        if (!_fitted) ComputeFit();
        _snapshot ??= CanvasSnapshot.Take(Session.Map, _geometry);
        var selection = SelectionOverlay.Take(Session, _geometry, _hover);
        context.Custom(new MapDrawOperation(new Rect(Bounds.Size), _snapshot, selection, new View(_cx, _cy, _zoom, Bounds.Width, Bounds.Height), ShowHeights));
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

    /// <summary>What a click at this spot picks: a thing, then a corner (zoomed in), then a wall, then the area.</summary>
    private Element? HitTest(Point p)
    {
        if (_geometry is null) return null;
        var m = ToMap(p);
        var px = 7 / _zoom;
        if (_geometry.ThingNear(m, Math.Max(16, px)) is { } t) return t;
        if (_zoom > 0.25 && _geometry.VertexNear(m, px) is { } v) return v;
        if (_geometry.LineNear(m, px) is { } l) return l;
        return _geometry.SectorAt(m);
    }

    internal readonly record struct View(double Cx, double Cy, double Zoom, double Width, double Height)
    {
        public SKMatrix Matrix => SKMatrix.CreateScaleTranslation((float)Zoom, (float)-Zoom, (float)(Width / 2 - Cx * Zoom), (float)(Height / 2 + Cy * Zoom));
    }
}
