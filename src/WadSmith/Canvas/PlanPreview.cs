using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WadSmith.Editor.Generate;

namespace WadSmith.App.Canvas;

/// <summary>
/// The "Make me a map" preview (D50): the plan from above, scaled to fit — rooms tinted by the part of the map they are in
/// (each locked door starts a new part), open sky in green, the arena in red, secrets in violet; corridors in grey with a
/// locked door in its key's colour, stairs and lifts marked; the way from the start to the exit as a line; the start, the
/// exit, keys and teleporters as marks. A plan is at most a few hundred shapes, so it is drawn as it is.
/// </summary>
internal sealed class PlanPreview : Control
{
    public static readonly StyledProperty<MapPlan?> PlanProperty = AvaloniaProperty.Register<PlanPreview, MapPlan?>(nameof(Plan));

    static PlanPreview() => AffectsRender<PlanPreview>(PlanProperty);

    public MapPlan? Plan
    {
        get => GetValue(PlanProperty);
        set => SetValue(PlanProperty, value);
    }

    private static readonly Color[] Zones = [Color.FromRgb(0x4A, 0x55, 0x66), Color.FromRgb(0x5E, 0x4A, 0x3A), Color.FromRgb(0x3E, 0x5A, 0x52), Color.FromRgb(0x5A, 0x48, 0x60)];

    /// <summary>A key's colour on the plan.</summary>
    public static Color KeyColour(string colour) => colour switch
    {
        "red" => Color.FromRgb(0xE0, 0x40, 0x30),
        "blue" => Color.FromRgb(0x40, 0x80, 0xF0),
        "yellow" => Color.FromRgb(0xF0, 0xD0, 0x30),
        "green" => Color.FromRgb(0x40, 0xC0, 0x50),
        _ => Color.FromRgb(0xC8, 0xC8, 0xC8),
    };

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0D)), new Rect(Bounds.Size));
        if (Plan is not { Rooms.Count: > 0 } plan) return;
        var boxes = plan.Rooms.Select(r => r.Box).Concat(plan.Links.Select(l => l.Box)).ToList();
        var (x0, y0, x1, y1) = (boxes.Min(b => b.X0), boxes.Min(b => b.Y0), boxes.Max(b => b.X1), boxes.Max(b => b.Y1));
        const double margin = 14;
        var scale = Math.Min((Bounds.Width - 2 * margin) / Math.Max(1, x1 - x0), (Bounds.Height - 2 * margin) / Math.Max(1, y1 - y0));
        var ox = (Bounds.Width - (x1 - x0) * scale) / 2;
        var oy = (Bounds.Height - (y1 - y0) * scale) / 2;

        // Map y runs north (up); the screen's runs down.
        Point P(double x, double y) => new(ox + (x - x0) * scale, oy + (y1 - y) * scale);
        Rect R(Box b) => new(P(b.X0, b.Y1), P(b.X1, b.Y0));
        var edge = new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA4)), 1);

        foreach (var link in plan.Links)
        {
            var fill = link.Kind switch
            {
                LinkKind.Stairs => Color.FromRgb(0x6A, 0x6A, 0x70),
                LinkKind.Lift => Color.FromRgb(0x8A, 0x6A, 0x3A),
                _ => Color.FromRgb(0x3A, 0x3A, 0x40),
            };
            context.DrawRectangle(new SolidColorBrush(fill), edge, R(link.Box));
            if (link.Kind is LinkKind.Door or LinkKind.Locked)
            {
                var room = plan.Rooms[link.DoorAt];
                var (mx, my) = Planner.Mouth(link, room);
                var half = (link.AlongX ? link.Box.Height : link.Box.Width) / 2.0;
                var colour = link.Key is { } key ? KeyColour(key.Colour) : Color.FromRgb(0xC8, 0xC8, 0xC8);
                var pen = new Pen(new SolidColorBrush(colour), link.Key is null ? 2 : 4);
                if (link.AlongX) context.DrawLine(pen, P(mx, my - half), P(mx, my + half));
                else context.DrawLine(pen, P(mx - half, my), P(mx + half, my));
            }
        }

        foreach (var room in plan.Rooms)
        {
            var fill = room.Role switch
            {
                RoomRole.Arena => Color.FromRgb(0x70, 0x2A, 0x24),
                RoomRole.Secret => Color.FromRgb(0x5A, 0x3A, 0x78),
                _ when room.Outdoors => Color.FromRgb(0x3A, 0x5E, 0x34),
                _ => Zones[room.Zone % Zones.Length],
            };
            if (room.Dark) fill = Color.FromRgb((byte)(fill.R * 0.6), (byte)(fill.G * 0.6), (byte)(fill.B * 0.6));
            context.DrawRectangle(new SolidColorBrush(fill), edge, R(room.Box));
        }

        // Pools: water blue, a hurting floor (nukage, lava) red-orange; pillars dark.
        foreach (var room in plan.Rooms)
        {
            foreach (var (box, _, damage) in room.Pools) context.FillRectangle(new SolidColorBrush(damage > 0 ? Color.FromRgb(0xC8, 0x50, 0x20) : Color.FromRgb(0x30, 0x60, 0xB0)), R(box));
            foreach (var pillar in room.Pillars) context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x18)), R(pillar));
        }

        foreach (var t in plan.Teleports) context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0x40, 0xD0, 0xE0)), 2), R(t.Pad));

        // The way from the start to the exit.
        var way = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0x90, 0x30)), 2, dashStyle: new DashStyle([3, 2], 0));
        for (var i = 1; i < plan.Path.Count; i++)
        {
            var (a, b) = (plan.Rooms[plan.Path[i - 1]].Box, plan.Rooms[plan.Path[i]].Box);
            context.DrawLine(way, P(a.CentreX, a.CentreY), P(b.CentreX, b.CentreY));
        }

        void Dot(double x, double y, Color colour, double radius) => context.DrawEllipse(new SolidColorBrush(colour), new Pen(Brushes.Black, 1), P(x, y), radius, radius);
        foreach (var t in plan.Things)
        {
            switch (t.Role)
            {
                case ThingRole.Key:
                    Dot(t.X, t.Y, KeyColour(plan.Rooms[t.Room].Key?.Colour ?? ""), 5);
                    break;
                case ThingRole.Boss:
                    Dot(t.X, t.Y, Color.FromRgb(0xFF, 0x30, 0x20), 6);
                    break;
                case ThingRole.Monster:
                    Dot(t.X, t.Y, Color.FromArgb(0x90, 0xE0, 0x60, 0x50), 1.6);
                    break;
                case ThingRole.Start:
                    Dot(t.X, t.Y, Color.FromRgb(0x50, 0xE0, 0x60), 6);
                    break;
            }
        }

        var e = P(plan.Exit.X, plan.Exit.Y);
        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)), new Pen(Brushes.Black, 1), new Rect(e.X - 5, e.Y - 5, 10, 10));
    }
}
