using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace WadSmith.App.Views;

/// <summary>
/// What a window does for itself once the desktop's frame is off: resizes from its edges, moves from
/// its title bar, and maximises on a double-click there (the pattern from Mailbox and Tuxflix).
/// </summary>
internal static class WindowFrame
{
    private const double ResizeMargin = 6;

    public static void Apply(Window window)
    {
        window.PointerMoved += (_, e) =>
        {
            window.Cursor = EdgeAt(window, e.GetPosition(window)) switch
            {
                WindowEdge.NorthWest => new Cursor(StandardCursorType.TopLeftCorner),
                WindowEdge.NorthEast => new Cursor(StandardCursorType.TopRightCorner),
                WindowEdge.SouthWest => new Cursor(StandardCursorType.BottomLeftCorner),
                WindowEdge.SouthEast => new Cursor(StandardCursorType.BottomRightCorner),
                WindowEdge.North => new Cursor(StandardCursorType.TopSide),
                WindowEdge.South => new Cursor(StandardCursorType.BottomSide),
                WindowEdge.West => new Cursor(StandardCursorType.LeftSide),
                WindowEdge.East => new Cursor(StandardCursorType.RightSide),
                _ => Cursor.Default,
            };
        };

        // Tunnelling, so a press on an edge starts a resize before whatever lies under the edge sees it.
        window.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
            if (EdgeAt(window, e.GetPosition(window)) is not { } edge) return;
            e.Handled = true;
            window.BeginResizeDrag(edge, e);
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Makes <paramref name="bar"/> move the window; a double-click toggles maximise.</summary>
    public static void Drags(Window window, Control bar)
    {
        bar.PointerPressed += (_, e) =>
        {
            if (e.Handled || !e.GetCurrentPoint(window).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual source && IsInteractive(source, bar)) return;
            if (e.ClickCount == 2)
            {
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }

            window.BeginMoveDrag(e);
        };
    }

    private static bool IsInteractive(Visual source, Control bar)
    {
        for (var v = source; v is not null && !ReferenceEquals(v, bar); v = v.GetVisualParent())
        {
            if (v is Button or TextBox or Thumb or ToggleButton) return true;
        }

        return false;
    }

    private static WindowEdge? EdgeAt(Window window, Point p)
    {
        if (window.WindowState != WindowState.Normal || window.WindowDecorations != WindowDecorations.None) return null;
        var west = p.X <= ResizeMargin;
        var east = p.X >= window.Bounds.Width - ResizeMargin;
        var north = p.Y <= ResizeMargin;
        var south = p.Y >= window.Bounds.Height - ResizeMargin;
        return (north, south, west, east) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (true, _, _, true) => WindowEdge.NorthEast,
            (_, true, true, _) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.North,
            (_, true, _, _) => WindowEdge.South,
            (_, _, true, _) => WindowEdge.West,
            (_, _, _, true) => WindowEdge.East,
            _ => null,
        };
    }
}
