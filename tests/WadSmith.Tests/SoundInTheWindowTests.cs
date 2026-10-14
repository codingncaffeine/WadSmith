using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>
/// Sounds and music dropped from the file manager (Phase 7), as the user goes: a sound file let go in the room is placed
/// there playing over and over, its Inspector's Plays box makes it play once; a MIDI file let go on the plan becomes the
/// map's music.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class SoundInTheWindowTests
{
    private static void DropFile(MainWindow window, MapCanvas canvas, MapPoint at, string path)
    {
        var data = new DataTransfer();
        var asked = window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
        Ui.Wait(asked);
        var file = asked.Result ?? throw new InvalidOperationException("The window cannot hand over " + path);
        data.Add(DataTransferItem.Create(DataFormat.File, (IStorageItem)file));
        var point = canvas.TranslatePoint(canvas.ToScreen(at), window)!.Value;
        foreach (var kind in new[] { Avalonia.Input.Raw.RawDragEventType.DragEnter, Avalonia.Input.Raw.RawDragEventType.DragOver, Avalonia.Input.Raw.RawDragEventType.Drop })
        {
            window.DragDrop(point, kind, data, DragDropEffects.Copy, RawInputModifiers.None);
            Ui.Pump(0.1);
        }
    }

    [Fact]
    public async Task ASoundDroppedInTheRoomPlaysThereAndItsInspectorMakesItPlayOnce()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var rain = Path.Combine(temp.Path, "rain.ogg");
        File.WriteAllBytes(rain, SoundFileTests.Ogg());
        var theme = Path.Combine(temp.Path, "theme.mid");
        File.WriteAllBytes(theme, SoundFileTests.Midi());
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session!;
                var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First(c => c.IsEffectivelyVisible);
                var room = session.Geometry.Frame.Sectors.First(s => s.Element is not null && session.Geometry.LoopsAt(s.Element.Slot).Count > 0);
                var corners = session.Geometry.LoopsAt(room.Element!.Slot).SelectMany(l => l).ToArray();
                var middle = new MapPoint(Math.Round((corners.Min(p => p.X) + corners.Max(p => p.X)) / 2 / 64) * 64, Math.Round((corners.Min(p => p.Y) + corners.Max(p => p.Y)) / 2 / 64) * 64);
                var lines = new List<string>();

                var before = session.Map.Things.Count;
                DropFile(window, canvas, middle, rain);
                for (var i = 0; i < 400 && session.Map.Things.Count == before; i++) Ui.Pump(0.05);
                var sound = session.Map.Things.Last();
                lines.Add($"placed {session.Resources!.Things.Find(sound.Type)?.ClassName} at {sound.X:0},{sound.Y:0} = middle {sound.X == middle.X && sound.Y == middle.Y}; args {string.Join(",", sound.Args)}");

                editor.Panel = SidePanel.Inspector;
                Ui.Pump(0.5);
                lines.Add($"{editor.Inspector.Heading}: {editor.Inspector.Summary}");
                var row = editor.Inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Plays");
                var box = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && ReferenceEquals(c.DataContext, row));
                box.SelectedIndex = 3;
                Ui.Pump(0.3);
                lines.Add($"args {string.Join(",", sound.Args)}; {editor.Inspector.Summary}");
                session.History.Undo();
                Ui.Pump(0.3);
                lines.Add($"undone: args {string.Join(",", sound.Args)}");

                DropFile(window, canvas, middle, theme);
                for (var i = 0; i < 400 && session.MapEntry.Music is null; i++) Ui.Pump(0.05);
                lines.Add($"music {session.MapEntry.Music}; things {session.Map.Things.Count - before}");
                return string.Join("\n", lines);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(
            "placed WadSmithSound at 0,0 = middle True; args 0,100,0,2,8\n"
            + "Sound: Plays rain over and over, at 100% volume, heard nearby.\n"
            + "args 0,100,3,2,8; Plays rain once, when a player first comes into its room, at 100% volume, heard nearby.\n"
            + "undone: args 0,100,0,2,8\n"
            + "music THEME; things 1",
            said.Replace(" at 0,0", " at 0,0", StringComparison.Ordinal));
    }
}
