using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;

namespace WadSmith.Tests;

/// <summary>Export in the real editor (Phase 12): Ctrl+E opens the card, the port is chosen in it, Show finds what is lost, Save as writes the file.</summary>
[Collection(TimingCollection.Name)]
public sealed class ExportInTheWindowTests
{
    [Fact]
    public async Task CtrlEShowsWhatVanillaLosesAndSavesTheWad()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        GameFiles.Require("gzdoom.pk3");
        using var temp = new TempFolder();
        var saveTo = Path.Combine(temp.Path, "out", "mine.wad");
        Directory.CreateDirectory(Path.GetDirectoryName(saveTo)!);
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session;
                var room = session.Map.Sectors.First(s => s.OnMap);
                session.History.Record("Colour the light", e => e.Field(room, "lightcolor", UdmfValue.Int(0xFF0000)));
                var canvas = window.GetVisualDescendants().OfType<App.Canvas.MapCanvas>().First();
                canvas.Focus();
                Ui.Pump(0.1);
                window.KeyPress(Key.E, RawInputModifiers.Control, PhysicalKey.None, null);
                Ui.Pump(0.2);
                var said = new List<string> { $"Ctrl+E: {(editor.Export is null ? "nothing" : "the Export card")}" };
                var export = editor.Export!;
                for (var i = 0; i < 300 && export.Busy; i++) Ui.Pump(0.02);
                Ui.Pump(0.1);
                said.Add($"{export.Selected.Title}: {export.Summary}");

                // Vanilla, chosen in the card's own list.
                var choose = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && c.ItemsSource == export.Choices);
                choose.SelectedIndex = export.Choices.ToList().FindIndex(c => c.Id == "vanilla");
                for (var i = 0; i < 300 && (export.Busy || export.Selected.Id != "vanilla"); i++) Ui.Pump(0.02);
                Ui.Pump(0.1);
                said.Add($"{export.Selected.Title}: {export.Summary} | {string.Join("; ", export.Lines.Select(l => l.Title))}");

                // Show selects the room whose light is coloured.
                var show = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content as string == "Show" && b.DataContext is App.ViewModels.ExportLine);
                show.Command!.Execute(null);
                Ui.Pump(0.1);
                said.Add($"Show: {(session.Selection.Items.SequenceEqual([room]) ? "the room" : string.Join(",", session.Selection.Items))}");

                // Save as: the file the report was read from, where the user chose.
                export.PickFile = _ => Task.FromResult<string?>(saveTo);
                var save = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content as string == "Save as…");
                save.Command!.Execute(null);
                for (var i = 0; i < 300 && export.Saved.Length == 0; i++) Ui.Pump(0.02);
                Ui.Pump(0.1);
                said.Add(export.Saved);
                return said;
            }
            finally
            {
                window.Close();
            }
        });
        Assert.Equal("Ctrl+E: the Export card", seen[0]);
        Assert.Equal("GZDoom and UZDoom: Nothing is lost: the maps play in GZDoom and UZDoom as they are.", seen[1]);
        Assert.Matches("^Vanilla: 1 thing does not survive in Vanilla: \\| (E1M1|MAP01): Coloured light$", seen[2]);
        Assert.Equal("Show: the room", seen[3]);
        Assert.Equal($"Saved as {saveTo}", seen[4]);
        var written = WadContainer.Open(saveTo);
        Assert.Equal(MapFormat.Doom, MapLocator.Find(written).Single().Format);
        Assert.Contains(written.Entries, e => e.Name == "NODES");
    }
}
