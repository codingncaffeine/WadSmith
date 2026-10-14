using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.App.ViewModels;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Camera screens (§3.5, Phase 7): the stand-in picture, ANIMDEFS' cameratexture, the handler that aims each map's camera, and the way the user makes one.</summary>
[Collection(TimingCollection.Name)]
public sealed class CameraScreenTests
{
    [Fact]
    public void AScreenWritesItsPictureCameraTextureAndHandler()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var wad = GameFiles.Require("freedoom2.wad");
        var game = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;

        var screen = Cameras.Make(project, game, "MAP01", 7, 256, 128);
        Assert.Equal(("CAM0001", 7, 256, 128), (screen.Texture, screen.Tid, screen.Width, screen.Height));
        Assert.True(game.Textures.Has("CAM0001"));
        Assert.True(File.Exists(Path.Combine(project.Folder, "textures", "CAM0001.png")));
        Assert.Contains("cameratexture CAM0001 256 128\n", File.ReadAllText(Path.Combine(project.Folder, Pictures.AnimationFile)), StringComparison.Ordinal);
        Assert.Contains("if (Level.MapName ~== \"MAP01\") Show(7, \"CAM0001\", 90);", File.ReadAllText(Path.Combine(project.Folder, Cameras.ScriptFile)), StringComparison.Ordinal);
        Assert.Contains($"#include \"{Cameras.ScriptFile}\"", File.ReadAllText(Path.Combine(project.Folder, DukeBorrowing.ScriptFile)), StringComparison.Ordinal);
        Assert.Contains("GameInfo\n{\n    AddEventHandlers = \"WadSmithCameras\"\n}\n", ProjectMapInfo.Text([], MapDefinitions.None, _ => [], project.CopiedNow()), StringComparison.Ordinal);

        // A second screen: one handler, both shown.
        Cameras.Make(project, game, "MAP02", 3, 128, 128);
        var script = File.ReadAllText(Path.Combine(project.Folder, Cameras.ScriptFile));
        Assert.Contains("Show(3, \"CAM0002\", 90)", script, StringComparison.Ordinal);
        Assert.Single(project.CopiedNow(), c => c.Kind == "eventhandler");
    }

    [Fact]
    public async Task TheFacesActionThenAClickPutsUpACameraAndItsScreen()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session!;
                var wall = session.Map.Lines.First(l => l.OnMap && l.Back is null);
                session.Selection.Set(wall.Front);
                editor.Panel = SidePanel.Inspector;
                Ui.Pump(0.5);
                var make = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.DataContext is ActionRow { Label: "Make it a camera screen…" });
                make.BringIntoView();
                Ui.Pump(0.2);
                var at = make.TranslatePoint(new Point(make.Bounds.Width / 2, make.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
                Ui.Pump(0.3);
                var lines = new List<string> { $"tool {editor.Tool}" };

                var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First(c => c.IsEffectivelyVisible);
                var room = wall.Front.Sector;
                var corners = session.Geometry.LoopsAt(room.Slot).SelectMany(l => l).ToArray();
                var spot = new MapPoint(Math.Round((corners.Min(p => p.X) + corners.Max(p => p.X)) / 128) * 64, Math.Round((corners.Min(p => p.Y) + corners.Max(p => p.Y)) / 128) * 64);
                var click = canvas.TranslatePoint(canvas.ToScreen(spot), window)!.Value;
                window.MouseMove(click, RawInputModifiers.None);
                window.MouseDown(click, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(click, MouseButton.Left, RawInputModifiers.None);
                for (var i = 0; i < 200 && !session.Map.Things.Any(t => t.OnMap && t.Type == Cameras.SecurityCamera); i++) Ui.Pump(0.05);
                Ui.Pump(0.3);
                var camera = session.Map.Things.First(t => t.OnMap && t.Type == Cameras.SecurityCamera);
                lines.Add($"camera at {camera.X:0},{camera.Y:0} id {camera.Tid}; a face shows {(session.Map.Sides.Any(s => s.OnMap && s.Middle == "CAM0001") ? "CAM0001" : "no screen")}; tool {editor.Tool}");
                return string.Join("\n", lines);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("tool Camera\ncamera at 0,0 id 1; a face shows CAM0001; tool Select", said);
    }
}
