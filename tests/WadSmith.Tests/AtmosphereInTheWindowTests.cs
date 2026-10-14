using Avalonia.Controls;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>A room's atmosphere by name (Phase 7), as the user goes: the room selected, its Inspector's Atmosphere box, one undo step.</summary>
[Collection(TimingCollection.Name)]
public sealed class AtmosphereInTheWindowTests
{
    [Fact]
    public async Task TheAtmosphereBoxSetsBrightnessLightAndFogTogether()
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
                var room = session.Map.Sectors.First(s => s.OnMap);
                session.Selection.Set(room);
                editor.Panel = SidePanel.Inspector;
                Ui.Pump(0.5);
                string Room() => $"{room.Light} {Atmospheres.Colour(room, "lightcolor", Atmospheres.White):X6} {Atmospheres.Colour(room, "fadecolor", Atmospheres.NoFog):X6}";
                var lines = new List<string> { $"before {Room()}" };

                var row = editor.Inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Atmosphere");
                var box = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.IsEffectivelyVisible && ReferenceEquals(c.DataContext, row));
                lines.Add($"box shows {row.Options[row.Value]}");
                box.SelectedIndex = Atmospheres.All.ToList().FindIndex(a => a.Name == "Toxic");
                Ui.Pump(0.3);
                lines.Add($"toxic {Room()}; box shows {row.Options[row.Value]}");
                session.History.Undo();
                Ui.Pump(0.3);
                lines.Add($"undone {Room()}");
                return string.Join("\n", lines);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("before 192 FFFFFF 000000\nbox shows Plain\ntoxic 160 A0FF90 0C2808; box shows Toxic\nundone 192 FFFFFF 000000", said);
    }
}
