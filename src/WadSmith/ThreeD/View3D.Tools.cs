using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using WadSmith.App.Canvas;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Scene;

namespace WadSmith.App.ThreeD;

/// <summary>
/// The tools in the 3D view (D49, Phase 5b): every tool of the column works where the pointer is, calling the same session code
/// the plan does with the point the pointer is over. A room is dragged on a floor, or pulled out of a solid wall; stairs and
/// Measure are dragged across a floor; Door and Window take the opening nearest the point, Switch a door then a solid wall,
/// Teleport a pad then the arrival spot, Light, Key and Camera a spot. Shift squares a room, Ctrl rounds it, as on the plan.
/// </summary>
internal sealed partial class View3D
{
    public static readonly StyledProperty<EditorTool> ToolProperty = AvaloniaProperty.Register<View3D, EditorTool>(nameof(Tool), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> GridSizeProperty = AvaloniaProperty.Register<View3D, double>(nameof(GridSize));

    /// <summary>What a left press does: Select picks and moves the camera; any other tool makes its object where the pointer is.</summary>
    public EditorTool Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    /// <summary>The grid chosen by hand (0: the 3D view snaps to 16).</summary>
    public double GridSize
    {
        get => GetValue(GridSizeProperty);
        set => SetValue(GridSizeProperty, value);
    }

    /// <summary>Raised when a line is measured in the view: its ends on the floor.</summary>
    public event Action<MapPoint, MapPoint>? Measured;

    /// <summary>How near an opening a click must land for Door, Window and Switch, in map units.</summary>
    private const double Reach = 32;

    /// <summary>
    /// A tool's gesture: the tool, what was under the pointer when it went down, the height of the level plane a drag runs on,
    /// the point where it began, the screen point pressed, and the solid wall a room is pulled out of.
    /// </summary>
    private sealed record ToolGesture(EditorTool Tool, SceneHit Hit, double PlaneZ, MapPoint From, Point Pressed, Side? Wall);

    private ToolGesture? _gesture;
    private MapPoint _gestureAt;
    private bool _gestureSquare;
    private bool _gestureRound;

    private double Step => GridSize > 0 ? GridSize : 16;

    private MapPoint Snap(MapPoint p) => new(Math.Round(p.X / Step) * Step, Math.Round(p.Y / Step) * Step);

    /// <summary>Where the ray under a screen point meets the level plane at height <paramref name="z"/>; null when it runs away from it.</summary>
    private MapPoint? OnPlane(Point screen, double z)
    {
        var aspect = Bounds.Width / Math.Max(Bounds.Height, 1);
        var (o, d) = Current.Ray(2 * screen.X / Math.Max(Bounds.Width, 1) - 1, 1 - 2 * screen.Y / Math.Max(Bounds.Height, 1), aspect);
        if (Math.Abs(d.Z) < 1e-9) return null;
        var t = (z - o.Z) / d.Z;
        return t <= 0 ? null : new MapPoint(o.X + d.X * t, o.Y + d.Y * t);
    }

    /// <summary>A left press with a tool other than Select begins its gesture on what the pointer is over; true when the press was the tool's.</summary>
    private bool PressTool(PointerPressedEventArgs e)
    {
        if (Tool == EditorTool.Select || Session is not { } session || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return false;
        e.Handled = true;
        if (_hover is not { } hit)
        {
            session.Tell("Point at a floor or a wall: the tool works on what the pointer is over.");
            return true;
        }

        var wall = Tool == EditorTool.Room && hit is { Kind: HitKind.Wall, Element: Side { Line.Back: null } face } ? face : null;
        if (Tool is EditorTool.Room or EditorTool.Stairs or EditorTool.Measure && hit.Kind != HitKind.Floor && wall is null)
        {
            session.Tell(Tool == EditorTool.Room ? "Drag on a floor to draw a room, or from a solid wall outward to add a room beyond it." : "Drag across a floor.");
            return true;
        }

        var at = new MapPoint(hit.X, hit.Y);
        _gesture = new ToolGesture(Tool, hit, hit.Z, wall is null ? Snap(at) : at, e.GetPosition(this), wall);
        _gestureAt = _gesture.From;
        e.Pointer.Capture(this);
        InvalidateVisual();
        return true;
    }

    /// <summary>A drag of a tool's gesture follows the pointer on its level plane; true while there is one.</summary>
    private bool MoveTool(PointerEventArgs e)
    {
        if (_gesture is not { } g) return false;
        if (OnPlane(e.GetPosition(this), g.PlaneZ) is { } p) _gestureAt = g.Wall is null ? Snap(p) : p;
        (_gestureSquare, _gestureRound) = ((e.KeyModifiers & KeyModifiers.Shift) != 0, (e.KeyModifiers & KeyModifiers.Control) != 0);
        InvalidateVisual();
        return true;
    }

    /// <summary>Letting go finishes the tool's gesture: the object is made through the session, as the plan makes it.</summary>
    private bool ReleaseTool(PointerReleasedEventArgs e)
    {
        if (_gesture is not { } g) return false;
        _gesture = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
        if (Session is not { } session || e.InitialPressMouseButton != MouseButton.Left) return true;
        var let = e.GetPosition(this);
        var dragged = Math.Abs(let.X - g.Pressed.X) + Math.Abs(let.Y - g.Pressed.Y) > 4;
        var to = _gestureAt;
        var at = new MapPoint(g.Hit.X, g.Hit.Y);
        (_gestureSquare, _gestureRound) = ((e.KeyModifiers & KeyModifiers.Shift) != 0, (e.KeyModifiers & KeyModifiers.Control) != 0);
        switch (g.Tool)
        {
            case EditorTool.Room:
                if (Shape(g, to) is { } shape) session.DrawRoom(shape);
                else session.Tell(g.Wall is null ? "Drag across the floor to draw a room, at least 16 each way." : "Drag away from the wall to pull a room out of it, at least 16 deep.");
                break;
            case EditorTool.Stairs:
                if (dragged && Math.Abs(to.X - g.From.X) >= 1 && Math.Abs(to.Y - g.From.Y) >= 1) session.MakeStairs(g.From, to);
                else session.Tell("Drag across a floor, from the bottom of the stairs to the top.");
                break;
            case EditorTool.Measure:
                if (g.From.DistanceTo(to) >= 1) Measured?.Invoke(g.From, to);
                break;
            case EditorTool.Door or EditorTool.Window:
                if (session.Geometry.LineNear(at, Reach) is not { Back: not null } opening)
                {
                    session.Tell(g.Tool == EditorTool.Door
                        ? "Click near the opening between two rooms: a door goes in the wall they share."
                        : "Click near the opening between two rooms: a window goes in the wall they share.");
                    break;
                }

                var (a, b) = (Along(opening, at), dragged && OnPlane(let, g.PlaneZ) is { } end ? Along(opening, end) : Along(opening, at));
                if (g.Tool == EditorTool.Door) session.MakeDoor(opening, Math.Abs(b - a) >= 8 ? (Math.Min(a, b), Math.Max(a, b)) : null);
                else session.MakeWindow(opening, a, b);
                break;
            case EditorTool.Light:
                session.PlaceLight(Snap(at));
                break;
            case EditorTool.Key:
                if (session.KeyToPlace is { } colour)
                {
                    session.KeyToPlace = null;
                    session.PlaceKey(colour, Snap(at));
                }

                Tool = EditorTool.Select;
                break;
            case EditorTool.Camera:
                session.PlaceCamera(Snap(at));
                Tool = EditorTool.Select;
                break;
            case EditorTool.Teleport:
                if (session.TeleportPad is not { OnMap: true } pad)
                {
                    session.TeleportPad = session.Geometry.SectorAt(at);
                }
                else
                {
                    session.TeleportPad = null;
                    session.MakeTeleporter(pad, Snap(at), 90);
                    Tool = EditorTool.Select;
                }

                break;
            case EditorTool.Switch:
                if (session.SwitchTarget is not { OnMap: true } target)
                {
                    session.SwitchTarget = Worked(session, g.Hit) ?? session.DoorAt(at, Reach) ?? session.Geometry.SectorAt(at);
                }
                else if (g.Hit is { Kind: HitKind.Wall, Element: Side { Line: { Back: null } solid } })
                {
                    session.SwitchTarget = null;
                    session.MakeSwitch(target, solid, at);
                    Tool = EditorTool.Select;
                }
                else
                {
                    session.Tell("Now click the solid wall where the switch goes.");
                }

                break;
        }

        return true;
    }

    /// <summary>What the chosen tool would do over what the pointer is on, in a sentence for the status line.</summary>
    private string ToolWords(Editor.EditorSession session, SceneHit? hit)
    {
        var floor = hit is { Kind: HitKind.Floor };
        var wall = hit is { } h && session.Geometry.LineNear(new MapPoint(h.X, h.Y), Reach) is { Back: not null } l ? l : null;
        var near = wall is not null;

        // A wall of a piece of wall (closed from floor to ceiling, as the wall beside a door or a deleted door is): a door goes through it.
        var through = wall is { Back: { } back } && wall.Front.Sector.FloorHeight >= wall.Front.Sector.CeilingHeight != back.Sector.FloorHeight >= back.Sector.CeilingHeight;
        return Tool switch
        {
            EditorTool.Room => floor ? "Drag on this floor to draw a room (Shift square, Ctrl round)."
                : hit is { Kind: HitKind.Wall, Element: Side { Line.Back: null } } ? "Drag away from this wall to pull a room out of it, sharing the wall."
                : "Point at a floor to draw a room, or at a solid wall to pull one out of it.",
            EditorTool.Door => through ? "Click: a door goes through this wall into the room beyond (drag along it for its width)." : near ? "Click: a door goes in this opening (drag along it for its width)." : "Point near the opening between two rooms: a door goes in the wall they share.",
            EditorTool.Window => near ? "Click: a window goes in this opening (drag along it for its width)." : "Point near the opening between two rooms: a window goes in the wall they share.",
            EditorTool.Stairs => floor ? "Drag across this floor, from the bottom of the stairs to the top." : "Point at a floor: stairs are dragged across one.",
            EditorTool.Measure => floor ? "Drag across the floor to measure it and see the floors and ceilings along the way." : "Point at a floor to measure from.",
            EditorTool.Teleport => session.TeleportPad is null ? "Click the room to step on: it becomes the teleport pad." : "Click where the player arrives.",
            EditorTool.Switch => session.SwitchTarget is null ? "Click the door or lift the switch works." : "Click the solid wall where the switch goes.",
            EditorTool.Light => "Click: a light goes here, 48 above the floor.",
            EditorTool.Key => "Click where the key goes, somewhere the player reaches before the door.",
            EditorTool.Camera => "Click where the camera stands.",
            _ => "",
        };
    }

    /// <summary>The door or lift a click landed on: the one whose face or floor it hit; null for anything else.</summary>
    private static Sector? Worked(Editor.EditorSession session, SceneHit hit)
    {
        bool Moves(Sector? s) => s is { OnMap: true } && (Editor.Smart.Doors.Recognise(session.Geometry, s) is not null || Editor.Smart.Lifts.Recognise(session.Geometry, s) is not null);
        return hit.Element switch
        {
            Side { Line: { } line } => Moves(line.Front.Sector) ? line.Front.Sector : Moves(line.Back?.Sector) ? line.Back!.Sector : null,
            Sector area when Moves(area) => area,
            _ => null,
        };
    }

    /// <summary>The room a Room gesture makes: a box on the floor (square with Shift, round with Ctrl), or a box pulled out of a solid wall; null when too small.</summary>
    private IReadOnlyList<MapPoint>? Shape(ToolGesture g, MapPoint to)
    {
        if (g.Wall is { Line: { } line })
        {
            // Out of the wall on its far side from the room (a solid wall's room is on its right), as deep as the drag.
            var (dx, dy) = (line.End.X - line.Start.X, line.End.Y - line.Start.Y);
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) return null;
            var (nx, ny) = (-dy / length, dx / length);
            var depth = Math.Round(((to.X - line.Start.X) * nx + (to.Y - line.Start.Y) * ny) / Step) * Step;
            if (depth < 16) return null;
            return [new(line.Start.X, line.Start.Y), new(line.Start.X + nx * depth, line.Start.Y + ny * depth), new(line.End.X + nx * depth, line.End.Y + ny * depth), new(line.End.X, line.End.Y)];
        }

        var a = g.From;
        var b = to;
        if (_gestureSquare)
        {
            var side = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            b = new MapPoint(a.X + Math.CopySign(side, b.X - a.X), a.Y + Math.CopySign(side, b.Y - a.Y));
        }

        if (Math.Abs(b.X - a.X) < 16 || Math.Abs(b.Y - a.Y) < 16) return null;
        return _gestureRound ? Shapes.Ellipse(a, b, Shapes.DefaultSides) : [new(a.X, a.Y), new(a.X, b.Y), new(b.X, b.Y), new(b.X, a.Y)];
    }

    /// <summary>How far along a wall a point lies, from its start, on the grid.</summary>
    private double Along(Line wall, MapPoint p)
    {
        var (dx, dy) = (wall.End.X - wall.Start.X, wall.End.Y - wall.Start.Y);
        var length = Math.Sqrt(dx * dx + dy * dy);
        var t = Math.Clamp(((p.X - wall.Start.X) * dx + (p.Y - wall.Start.Y) * dy) / Math.Max(length, 1e-9), 0, length);
        return Math.Clamp(Math.Round(t / Math.Min(Step, 8)) * Math.Min(Step, 8), 0, length);
    }

    /// <summary>The gesture drawn over the view: the room's outline on its floor, the stairs' or measured line, or the wall a click works on.</summary>
    private void DrawGesture(DrawingContext context)
    {
        if (_gesture is not { } g) return;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x58, 0x2A)), 2);
        IReadOnlyList<MapPoint> points = g.Tool switch
        {
            EditorTool.Room => Shape(g, _gestureAt) is { } shape ? [.. shape, shape[0]] : [g.From, _gestureAt],
            EditorTool.Stairs or EditorTool.Measure => [g.From, _gestureAt],
            _ => [],
        };
        Point? last = null;
        foreach (var p in points)
        {
            var now = ScreenPoint(p.X, p.Y, g.PlaneZ);
            if (last is { } l && now is { } n) context.DrawLine(pen, l, n);
            last = now;
        }
    }
}
