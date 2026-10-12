using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using WadSmith.Editor.Geometry;

namespace WadSmith.App.Canvas;

/// <summary>
/// The elevation profile drawn as a cross-section: rock below each floor and above each ceiling, the open space between
/// (sky where there is sky), what stops the player marked in red at its step, the floor heights written on, and the player
/// standing at the start for scale (drawn to the heights' scale, so it keeps its shape). Lengths and heights are each fitted
/// to the panel; when one is drawn larger than the other, the panel says by how much.
/// </summary>
internal sealed class ProfileView : Control
{
    public static readonly StyledProperty<ElevationProfile?> ProfileProperty = AvaloniaProperty.Register<ProfileView, ElevationProfile?>(nameof(Profile));

    private static readonly IBrush Rock = new SolidColorBrush(Color.FromRgb(0x3A, 0x2E, 0x2A));
    private static readonly IBrush Overhead = new SolidColorBrush(Color.FromRgb(0x2A, 0x22, 0x22));
    private static readonly IBrush Open = new SolidColorBrush(Color.FromRgb(0x14, 0x10, 0x10));
    private static readonly IBrush Sky = new SolidColorBrush(Color.FromRgb(0x26, 0x3A, 0x55));
    private static readonly IBrush Outside = new SolidColorBrush(Color.FromRgb(0x0C, 0x09, 0x0A));
    private static readonly IBrush Stop = new SolidColorBrush(Color.FromRgb(0xE8, 0x4A, 0x3A));
    private static readonly IBrush Text = new SolidColorBrush(Color.FromRgb(0xD8, 0xCF, 0xC8));
    private static readonly IPen Ghost = new Pen(new SolidColorBrush(Color.FromRgb(0x7C, 0xD8, 0x6A)), 1.5);

    static ProfileView()
    {
        AffectsRender<ProfileView>(ProfileProperty);
    }

    public ElevationProfile? Profile
    {
        get => GetValue(ProfileProperty);
        set => SetValue(ProfileProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Outside, new Rect(size));
        if (Profile is not { Length: > 0 } p || p.Spans.Count == 0 || size.Width < 40 || size.Height < 40) return;
        const double pad = 14;
        var inside = p.Spans.Where(s => s.Area >= 0).ToList();
        if (inside.Count == 0) return;
        var low = inside.Min(s => s.Floor) - 24;
        var high = Math.Max(inside.Max(s => s.Sky ? s.Floor + 96 : s.Ceiling), inside.Max(s => s.Floor) + 72) + 24;
        var sx = (size.Width - 2 * pad) / p.Length;
        var sy = (size.Height - 2 * pad - 14) / (high - low);
        double X(double along) => pad + along * sx;
        double Y(double z) => size.Height - pad - 14 - (z - low) * sy;

        foreach (var s in p.Spans)
        {
            var (x0, x1) = (X(s.From), Math.Max(X(s.To), X(s.From) + 1));
            if (s.Area < 0)
            {
                context.FillRectangle(Outside, new Rect(x0, pad, x1 - x0, size.Height - 2 * pad - 14));
                continue;
            }

            var top = s.Sky ? pad : Y(s.Ceiling);
            context.FillRectangle(Rock, new Rect(x0, Y(s.Floor), x1 - x0, Math.Max(0, Y(low) - Y(s.Floor))));
            context.FillRectangle(s.Sky ? Sky : Open, new Rect(x0, top, x1 - x0, Math.Max(0, Y(s.Floor) - top)));
            if (!s.Sky) context.FillRectangle(Overhead, new Rect(x0, pad, x1 - x0, Math.Max(0, Y(s.Ceiling) - pad)));
            if (x1 - x0 > 28) Label(context, s.Floor.ToString("0", CultureInfo.InvariantCulture), (x0 + x1) / 2, Y(s.Floor) + 2, Text, center: true);
        }

        // What stops the player: a red mark at the step, with why.
        foreach (var step in p.Steps.Where(st => st.Stops is not null && st.Stops != "a solid wall"))
        {
            var x = X(step.At);
            context.DrawLine(new Pen(Stop, 2), new Point(x, pad), new Point(x, size.Height - pad - 14));
            Label(context, step.Stops!, x + 4, pad + 2, Stop);
        }

        // The player at the start of the first room, for scale: 56 tall and 32 across, both at the heights' scale.
        var first = inside[0];
        var gx = X(first.From) + Math.Min(24, (first.To - first.From) / 2) * sx;
        var (gw, gh) = (32 * sy, 56 * sy);
        var feet = Y(first.Floor);
        context.DrawRectangle(null, Ghost, new Rect(gx - gw / 2, feet - gh * 0.82, gw, gh * 0.82), 2);
        context.DrawEllipse(null, Ghost, new Point(gx, feet - gh * 0.91), Math.Max(2, gw * 0.3), gh * 0.09);

        var scale = (sy / sx) switch
        {
            > 1.5 => string.Create(CultureInfo.InvariantCulture, $" · heights drawn ×{sy / sx:0.#} the lengths"),
            < 1 / 1.5 => string.Create(CultureInfo.InvariantCulture, $" · lengths drawn ×{sx / sy:0.#} the heights"),
            _ => "",
        };
        Label(context, string.Create(CultureInfo.InvariantCulture, $"0 … {p.Length:0}{scale}"), pad, size.Height - pad - 10, Text);
    }

    private static void Label(DrawingContext context, string text, double x, double y, IBrush brush, bool center = false)
    {
        var t = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);
        context.DrawText(t, new Point(center ? x - t.Width / 2 : x, y));
    }
}
