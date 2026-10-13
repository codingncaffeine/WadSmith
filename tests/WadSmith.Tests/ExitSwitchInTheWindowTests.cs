using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>The exit step of the guided first map, done the user's way: a click on a wall of the plan, then the Inspector's button.</summary>
[Collection(TimingCollection.Name)]
public sealed class ExitSwitchInTheWindowTests
{
    [Fact]
    public async Task TheSwitchIsAPieceOfTheWallWhereItWasClicked()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var tile = vm.Welcome.Games.First();
                Ui.Wait(tile.GuidedMap!.ExecuteAsync(tile));
                Ui.Pump(1.0);
                var editor = (App.ViewModels.EditorViewModel)vm.Page!;
                var guide = editor.Guide!;
                for (var i = 0; i < 5; i++) guide.SkipCommand.Execute(null);
                Ui.Pump(0.6);
                var said = new List<string> { $"step {guide.Index + 1}: {guide.Title}" };

                // The west wall of the first room, clicked three quarters of the way up it, on the plan.
                var session = editor.Session;
                var look = session.Look;
                var leftmost = session.Map.Lines.Where(l => l.OnMap).Min(l => Math.Min(l.Start.X, l.End.X));
                var west = session.Map.Lines.Single(l => l.OnMap && l.Back is null && l.Start.X == leftmost && l.End.X == leftmost);
                var (x, y0, y1) = (leftmost, Math.Min(west.Start.Y, west.End.Y), Math.Max(west.Start.Y, west.End.Y));
                var canvas = window.GetVisualDescendants().OfType<App.Canvas.MapCanvas>().First();
                var click = canvas.TranslatePoint(canvas.ToScreen(new(x, y0 + (y1 - y0) * 0.75)), window)!.Value;
                window.MouseMove(click, RawInputModifiers.None);
                window.MouseDown(click, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(click, MouseButton.Left, RawInputModifiers.None);
                Ui.Pump(0.3);
                said.Add($"clicked a {y1 - y0} wall: {editor.Inspector.Heading}, {(session.Selection.At is not null ? "at a point" : "no point")}");

                editor.Inspector.Rows.OfType<App.ViewModels.ActionRow>().Single(r => r.Label == "Make it the exit switch").Command.Execute(null);
                Ui.Pump(1.0);

                // With the game's own picture (32 wide, 72 tall) in a 128 room: the switch sits in an alcove 8 deep behind the wall, where the click was.
                var exit = session.Selection.Items.OfType<Line>().Single();
                var alcove = exit.Front.Sector;
                string Shown(Line l) => l.Back is not null ? "opening" : l.Front.Middle == look.ExitSwitch ? "switch" : l.Front.Middle == look.Wall ? "wall" : l.Front.Middle;
                var pieces = session.Map.Lines.Where(l => l.OnMap && l.Start.X == x && l.End.X == x).OrderBy(l => Math.Min(l.Start.Y, l.End.Y)).Select(l => $"{l.Length} {Shown(l)}{(l.Special == 0 ? "" : " (exit)")}");
                said.Add($"made: {editor.Inspector.Heading}, the switch {exit.Length} long and {Math.Abs(exit.Start.X - x)} behind the wall in an alcove {alcove.CeilingHeight - alcove.FloorHeight} tall, starting {Math.Min(exit.Start.Y, exit.End.Y) - y0} up the wall; the wall is now {string.Join(" · ", pieces)}");
                said.Add($"step {guide.Index + 1}: {guide.Title}");
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal(["step 6: Add a way out", "clicked a 512 wall: Solid wall, at a point", "made: Exit switch, the switch 32 long and 8 behind the wall in an alcove 72 tall, starting 368 up the wall; the wall is now 368 wall · 32 opening · 112 wall", "step 7: Play it"], seen);
    }
}
