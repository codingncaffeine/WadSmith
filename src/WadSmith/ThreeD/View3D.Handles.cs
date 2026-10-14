using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using WadSmith.App.Canvas;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;
using WadSmith.Editor.Smart;

namespace WadSmith.App.ThreeD;

/// <summary>
/// Handles in the 3D view (D49, Phase 5b): with Select, what is selected shows small squares to drag, as the plan's are. A
/// room's corners and a wall's ends move across the level of their floor; a face's middle slides its wall sideways; a window's
/// sill and head move up and down. Moves go through the plan's own <see cref="MoveEdit"/> and joining, one undo step each.
/// </summary>
internal sealed partial class View3D
{
    /// <summary>How tall a window's opening stays at least while its sill or head is dragged.</summary>
    private const double OpeningAtLeast = 16;

    /// <summary>How near a handle a press must land, in pixels.</summary>
    private const double HandleReach = 9;

    private enum HandleKind
    {
        Corner,
        Wall,
        Sill,
        Head,
    }

    /// <summary>A handle: what it moves, where it stands in the map (its point and height), and for a wall, the way it slides.</summary>
    private sealed record Handle(HandleKind Kind, MapPoint At, double Z, Vertex? Corner = null, Line? Wall = null, Sector? Window = null);

    /// <summary>A handle being dragged: the handle, what moves, the undo step, where the drag took hold, and the edit made so far.</summary>
    private sealed record HandleDrag(Handle Handle, MoveSet? Set, long Gesture, MapPoint Grab, Point Pressed)
    {
        public MoveEdit? Edit { get; set; }

        public double Applied { get; set; }
    }

    private HandleDrag? _handleDrag;

    /// <summary>The handles the selection shows (none while another tool is chosen).</summary>
    private List<Handle> Handles()
    {
        var handles = new List<Handle>();
        if (Tool != EditorTool.Select || Session is not { } session || session.Selection.Count == 0 || session.Selection.Count > 8) return handles;
        var corners = new HashSet<Vertex>(ReferenceEqualityComparer.Instance);
        void Corner(Vertex v, double z)
        {
            if (v.OnMap && corners.Add(v)) handles.Add(new Handle(HandleKind.Corner, new MapPoint(v.X, v.Y), z, Corner: v));
        }

        foreach (var item in session.Selection.Items)
        {
            switch (item)
            {
                case Sector { OnMap: true } room when WallWindows.Recognise(session.Geometry, room) is { } window:
                    var mid = Middle(window.Faces);
                    handles.Add(new Handle(HandleKind.Sill, mid, window.Sill, Window: room));
                    handles.Add(new Handle(HandleKind.Head, mid, window.Top, Window: room));
                    break;
                case Sector { OnMap: true } room:
                    foreach (var line in session.Map.Lines.Where(l => l.OnMap && (l.Front.Sector == room || l.Back?.Sector == room)))
                    {
                        Corner(line.Start, room.FloorHeight);
                        Corner(line.End, room.FloorHeight);
                    }

                    break;
                case Side { OnMap: true, Line: { OnMap: true } wall, Sector: { } side }:
                    Corner(wall.Start, side.FloorHeight);
                    Corner(wall.End, side.FloorHeight);
                    handles.Add(new Handle(HandleKind.Wall, new MapPoint((wall.Start.X + wall.End.X) / 2, (wall.Start.Y + wall.End.Y) / 2), (side.FloorHeight + side.CeilingHeight) / 2, Wall: wall));
                    break;
                case Line { OnMap: true } wall:
                    Corner(wall.Start, wall.Front.Sector.FloorHeight);
                    Corner(wall.End, wall.Front.Sector.FloorHeight);
                    break;
                case Vertex { OnMap: true } v:
                    Corner(v, session.Geometry.SectorAt(new MapPoint(v.X, v.Y))?.FloorHeight ?? 0);
                    break;
            }
        }

        return handles;
    }

    private static MapPoint Middle(IReadOnlyList<Line> faces) =>
        faces.Count == 0 ? default : new MapPoint(faces.Average(l => (l.Start.X + l.End.X) / 2), faces.Average(l => (l.Start.Y + l.End.Y) / 2));

    /// <summary>A left press on a handle takes hold of it; true when it did.</summary>
    private bool PressHandle(PointerPressedEventArgs e)
    {
        if (Session is not { } session || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        var at = e.GetPosition(this);
        Handle? best = null;
        var bestD = HandleReach;
        foreach (var h in Handles())
        {
            if (ScreenPoint(h.At.X, h.At.Y, h.Z) is not { } p) continue;
            var d = Math.Max(Math.Abs(p.X - at.X), Math.Abs(p.Y - at.Y));
            if (d <= bestD) (best, bestD) = (h, d);
        }

        if (best is null) return false;
        MoveSet? set = best.Kind switch
        {
            HandleKind.Corner => MoveSet.Gather(session.Geometry, MoveSet.Seeds.Of([best.Corner!])),
            HandleKind.Wall => MoveSet.Gather(session.Geometry, MoveSet.Seeds.Of([best.Wall!])),
            _ => null,
        };
        _handleDrag = new HandleDrag(best, set, History.NewGesture(), best.At, at);
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
        return true;
    }

    /// <summary>The handle follows the pointer: corners across their floor's level (snapped), a wall along its normal, a sill or head up and down.</summary>
    private bool MoveHandle(PointerEventArgs e)
    {
        if (_handleDrag is not { } drag || Session is not { } session) return false;
        var at = e.GetPosition(this);
        var free = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        switch (drag.Handle.Kind)
        {
            case HandleKind.Sill or HandleKind.Head:
                // Up and down by what a pixel is worth at the handle's distance, on the 8 grid (Shift: by 1).
                var c = Current;
                var distance = Math.Sqrt(Math.Pow(drag.Handle.At.X - c.X, 2) + Math.Pow(drag.Handle.At.Y - c.Y, 2) + Math.Pow(drag.Handle.Z - c.Z, 2));
                var perPixel = 2 * distance * Math.Tan(c.Fov * Math.PI / 360) / Math.Max(Bounds.Height, 1) / c.Stretch;
                var total = (drag.Pressed.Y - at.Y) * perPixel;
                var snapped = free ? Math.Round(total) : Math.Round(total / 8) * 8;

                // The opening stays at least 16 tall: the sill stops below the head, the head above the sill.
                if (drag.Handle.Window is { } area)
                {
                    var (sill, head) = drag.Handle.Kind == HandleKind.Sill ? (drag.Handle.Z, area.CeilingHeight) : (area.FloorHeight, drag.Handle.Z);
                    snapped = drag.Handle.Kind == HandleKind.Sill ? Math.Min(snapped, head - OpeningAtLeast - sill) : Math.Max(snapped, sill + OpeningAtLeast - head);
                }

                if (snapped != drag.Applied)
                {
                    var target = new SurfaceTarget(drag.Handle.Window!, WallPart.Middle, drag.Handle.Kind == HandleKind.Head);
                    var now = session.RaiseSurface(target, snapped - drag.Applied, drag.Gesture);
                    drag.Applied = snapped;
                    if (now is { } h) session.Tell($"The window's {(drag.Handle.Kind == HandleKind.Sill ? "sill" : "head")} at {h:0} · let go to keep it (Shift moves by 1).");
                }

                break;
            default:
                if (OnPlane(at, drag.Handle.Z) is not { } p || drag.Set is not { IsEmpty: false } set) break;
                var (dx, dy) = (p.X - drag.Grab.X, p.Y - drag.Grab.Y);
                if (drag.Handle.Kind == HandleKind.Wall && drag.Handle.Wall is { } wall)
                {
                    // Sideways only: the drag along the wall's normal.
                    var (wx, wy) = (wall.End.X - wall.Start.X, wall.End.Y - wall.Start.Y);
                    var length = Math.Max(Math.Sqrt(wx * wx + wy * wy), 1e-9);
                    var (nx, ny) = (-wy / length, wx / length);
                    var along = dx * nx + dy * ny;
                    along = free ? along : Math.Round(along / Step) * Step;
                    (dx, dy) = (nx * along, ny * along);
                }
                else if (!free)
                {
                    var target = Snap(new MapPoint(drag.Handle.At.X + dx, drag.Handle.At.Y + dy));
                    (dx, dy) = (target.X - drag.Handle.At.X, target.Y - drag.Handle.At.Y);
                }

                var edit = drag.Edit is null ? new MoveEdit(set, dx, dy, session.FlatSize) : drag.Edit.To(dx, dy);
                drag.Edit ??= edit;
                session.History.Run(edit, drag.Gesture);
                session.Tell(drag.Handle.Kind == HandleKind.Wall
                    ? $"The wall moved {Math.Sqrt(dx * dx + dy * dy):0} sideways · let go to keep it (Shift: free)."
                    : $"The corner at {drag.Handle.At.X + dx:0}, {drag.Handle.At.Y + dy:0} · let go to keep it (Shift: free).");
                break;
        }

        InvalidateVisual();
        return true;
    }

    /// <summary>Letting go keeps the move (joined with what it landed on, as on the plan), one undo step; a press that moved nothing leaves nothing.</summary>
    private bool ReleaseHandle(PointerReleasedEventArgs e)
    {
        if (_handleDrag is not { } drag) return false;
        _handleDrag = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
        if (Session is not { } session) return true;
        if (drag.Set is { IsEmpty: false } set && drag.Edit is not null) session.FinishMove(set, drag.Gesture);
        return true;
    }

    /// <summary>The selection's handles drawn over the view: squares, filled while held.</summary>
    private void DrawHandles(DrawingContext context)
    {
        var accent = Color.FromRgb(0xE0, 0x58, 0x2A);
        var pen = new Pen(new SolidColorBrush(accent), 1.5);
        foreach (var h in Handles())
        {
            if (ScreenPoint(h.At.X, h.At.Y, h.Z) is not { } p || p.X < 0 || p.Y < 0 || p.X > Bounds.Width || p.Y > Bounds.Height) continue;
            var held = _handleDrag is { } d && d.Handle.Kind == h.Kind && Equals(d.Handle.At, h.At);
            var size = h.Kind is HandleKind.Sill or HandleKind.Head ? 6 : 5;
            var box = new Rect(p.X - size, p.Y - size, size * 2, size * 2);
            context.FillRectangle(held ? new SolidColorBrush(accent) : new SolidColorBrush(Color.FromArgb(0xC0, 0x10, 0x0C, 0x0E)), box);
            context.DrawRectangle(pen, box);
        }
    }
}
