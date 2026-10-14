using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace WadSmith.App.Views;

/// <summary>
/// The Guide's picture of the kinds of door (D54): a door that rises, seen from the front, its door half up into the wall above
/// with an arrow up; a door that slides aside, seen from above, half into the wall beside it with an arrow along; and a door
/// that swings, seen from above, turned on its hinge with the path its end takes. The door in the accent colour, walls grey.
/// </summary>
internal sealed class DoorKindsDiagram : Control
{
    private static readonly IBrush Wall = new SolidColorBrush(Color.FromRgb(0x8A, 0x92, 0x9C));
    private static readonly IBrush Door = new SolidColorBrush(Color.FromRgb(0xE0, 0x58, 0x2A));
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0xD8, 0xCF, 0xC8));
    private static readonly IPen Arrow = new Pen(Ink, 1.6, lineCap: PenLineCap.Round);
    private static readonly IPen Path = new Pen(Ink, 1.2, new DashStyle([3, 3], 0));

    /// <summary>The three panels' names, as the Inspector's "Opens" says them.</summary>
    private static readonly string[] Names = ["Upward", "Slides aside", "Swings"];

    protected override Size MeasureOverride(Size availableSize) => new(Math.Min(double.IsInfinity(availableSize.Width) ? 360 : availableSize.Width, 380), 112);

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width / 3;
        for (var i = 0; i < 3; i++)
        {
            var panel = new Rect(i * w + 6, 4, w - 12, 82);
            using (context.PushTransform(Matrix.CreateTranslation(panel.X, panel.Y)))
            {
                switch (i)
                {
                    case 0: Rises(context, panel.Width, panel.Height); break;
                    case 1: Slides(context, panel.Width, panel.Height); break;
                    default: Swings(context, panel.Width, panel.Height); break;
                }
            }

            var label = new FormattedText(Names[i], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Ink);
            context.DrawText(label, new Point(i * w + (w - label.Width) / 2, 92));
        }
    }

    /// <summary>From the front: a wall with a doorway, the door risen halfway into the wall above it.</summary>
    private static void Rises(DrawingContext c, double w, double h)
    {
        var (x0, x1) = (w * 0.32, w * 0.68);
        var top = h * 0.22;
        c.FillRectangle(Wall, new Rect(0, 0, x0, h));
        c.FillRectangle(Wall, new Rect(x1, 0, w - x1, h));
        c.FillRectangle(Wall, new Rect(x0, 0, x1 - x0, top));
        c.FillRectangle(Door, new Rect(x0 + 2, top, x1 - x0 - 4, (h - top) * 0.45));
        var mid = (x0 + x1) / 2;
        Line(c, new Point(mid, h - 6), new Point(mid, top + (h - top) * 0.55));
        Head(c, new Point(mid, top + (h - top) * 0.55), new Point(0, -1));
    }

    /// <summary>From above: a wall with a gap, the door slid halfway into the wall beside it.</summary>
    private static void Slides(DrawingContext c, double w, double h)
    {
        var (x0, x1) = (w * 0.3, w * 0.7);
        var y = h * 0.5;
        c.FillRectangle(Wall, new Rect(0, y - 7, x0, 14));
        c.FillRectangle(Wall, new Rect(x1, y - 7, w - x1, 14));
        var shift = (x1 - x0) * 0.55;
        c.FillRectangle(Door, new Rect(x0 + shift, y - 3, x1 - x0, 6));
        Line(c, new Point(x0 + 4, y + 18), new Point(x1 + 6, y + 18));
        Head(c, new Point(x1 + 6, y + 18), new Point(1, 0));
    }

    /// <summary>From above: a wall with a gap, the door turned on its hinge, and the path of its end.</summary>
    private static void Swings(DrawingContext c, double w, double h)
    {
        var (x0, x1) = (w * 0.3, w * 0.7);
        var y = h * 0.35;
        c.FillRectangle(Wall, new Rect(0, y - 7, x0, 14));
        c.FillRectangle(Wall, new Rect(x1, y - 7, w - x1, 14));
        var length = x1 - x0;
        var hinge = new Point(x0, y);

        // Turned most of a quarter turn into the room below; its end's path from shut to open.
        var turn = 70 * Math.PI / 180;
        var end = new Point(hinge.X + length * Math.Cos(turn), hinge.Y + length * Math.Sin(turn));
        var door = new StreamGeometry();
        using (var g = door.Open())
        {
            var (nx, ny) = (-Math.Sin(turn) * 3, Math.Cos(turn) * 3);
            g.BeginFigure(new Point(hinge.X + nx, hinge.Y + ny), true);
            g.LineTo(new Point(end.X + nx, end.Y + ny));
            g.LineTo(new Point(end.X - nx, end.Y - ny));
            g.LineTo(new Point(hinge.X - nx, hinge.Y - ny));
            g.EndFigure(true);
        }

        c.DrawGeometry(Door, null, door);
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(new Point(x1, y), false);
            g.ArcTo(end, new Size(length, length), 0, false, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        c.DrawGeometry(null, Path, arc);
        Head(c, end, new Point(-Math.Sin(turn), Math.Cos(turn)));
    }

    private static void Line(DrawingContext c, Point from, Point to) => c.DrawLine(Arrow, from, to);

    /// <summary>An arrow head at <paramref name="tip"/>, pointing along <paramref name="direction"/>.</summary>
    private static void Head(DrawingContext c, Point tip, Point direction)
    {
        var length = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (length <= 0) return;
        var (dx, dy) = (direction.X / length, direction.Y / length);
        var back = new Point(tip.X - dx * 6, tip.Y - dy * 6);
        c.DrawLine(Arrow, tip, new Point(back.X - dy * 4, back.Y + dx * 4));
        c.DrawLine(Arrow, tip, new Point(back.X + dy * 4, back.Y - dx * 4));
    }
}
