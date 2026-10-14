using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;
using WadSmith.Core.Projects;

namespace WadSmith.Tests;

/// <summary>
/// What only GZDoom has, in a project that plays in a classic port (6b.3), as the user sees it: the rows shown greyed with
/// "(GZDoom only)" and the reason first in their tooltip, the Light tool and its Ctrl+K entry unavailable; back for GZDoom.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class GZDoomOnlyInTheWindowTests
{
    [Fact]
    public async Task ClassicProjectsGreyWhatOnlyGZDoomHas()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        GameFiles.Require("gzdoom.pk3");
        using var temp = new TempFolder();
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session!;
                var room = session.Map.Sectors.First(s => s.OnMap);
                var wall = session.Map.Lines.First(l => l.OnMap && l.Back is null);
                editor.Panel = SidePanel.Inspector;
                var lightButton = window.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Content as string == editor.Faces.Light.Label);
                string Look(string target)
                {
                    session.SetTarget(target);
                    Ui.Pump(0.5);
                    session.Selection.Set(room);
                    Ui.Pump(0.4);
                    string Row(string label)
                    {
                        var row = editor.Inspector.Rows.FirstOrDefault(r => r.Label.StartsWith(label, StringComparison.Ordinal));
                        if (row is null) return $"{label}: missing";
                        var shown = window.GetVisualDescendants().OfType<Avalonia.Controls.Control>().Where(c => ReferenceEquals(c.DataContext, row) && c.IsEffectivelyVisible).ToList();
                        return $"{row.Label} {(shown.Count > 0 && shown.All(c => c.IsEffectivelyEnabled) ? "usable" : shown.Count == 0 ? "not shown" : "greyed")}{(row.Tooltip.StartsWith("GZDoom only:", StringComparison.Ordinal) ? ", says why" : "")}";
                    }

                    var rows = new List<string> { Row("Light colour"), Row("Fog colour"), Row("Atmosphere"), Row("Brightness") };
                    session.Selection.Set(wall);
                    Ui.Pump(0.4);
                    rows.Add(Row("A mirror"));
                    var palette = editor.Commands().First(c => c.Name == "Light tool");
                    rows.Add($"Light tool {(lightButton.IsEffectivelyEnabled ? "usable" : "greyed")}, Ctrl+K {(palette.Available ? "offers it" : "greys it")}");
                    return $"{target}: {string.Join("; ", rows)}";
                }

                return Look(Targets.Vanilla) + "\n" + Look(Targets.GZDoom);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(
            "vanilla: Light colour (GZDoom only) greyed, says why; Fog colour (GZDoom only) greyed, says why; Atmosphere (GZDoom only) greyed, says why; Brightness usable; A mirror (GZDoom only) greyed, says why; Light tool greyed, Ctrl+K greys it\n"
            + "gzdoom: Light colour usable; Fog colour usable; Atmosphere usable; Brightness usable; A mirror usable; Light tool usable, Ctrl+K offers it",
            said);
    }
}

/// <summary>The Objects shelf in a project for a classic port (6b.3): a picture decoration of the project's is faint and says why; back for GZDoom.</summary>
[Collection(TimingCollection.Name)]
public sealed class GZDoomOnlyShelfTests
{
    [Fact]
    public async Task AClassicProjectDimsItsOwnDecorationOnTheShelf()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        GameFiles.Require("gzdoom.pk3");
        using var temp = new TempFolder();
        var png = Path.Combine(temp.Path, "tall lamp.png");
        File.WriteAllBytes(png, WadSmith.Core.Graphics.ImageCodec.EncodePng(new WadSmith.Core.Graphics.RgbaImage(40, 128)));
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session!;
                editor.ShowObjects = true;
                session.ImportDecoration(png);
                for (var i = 0; i < 400 && !editor.ObjectShelf.Tiles.Any(t => t.Group == ShelfViewModel.PictureGroup); i++) Ui.Pump(0.05);
                ShelfTile Tile() => editor.ObjectShelf.Tiles.First(t => t.Group == ShelfViewModel.PictureGroup);
                string Seen() => $"{(Tile().Unusable is null ? "usable" : "faint")}{(Tile().ShownTip.StartsWith("GZDoom only:", StringComparison.Ordinal) ? ", says why" : "")}";
                var before = Seen();
                session.SetTarget(Targets.Vanilla);
                Ui.Pump(0.5);
                var classic = Seen();
                session.SetTarget(Targets.GZDoom);
                Ui.Pump(0.5);
                return $"{before} | vanilla: {classic} | gzdoom: {Seen()}";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("usable | vanilla: faint, says why | gzdoom: usable", said);
    }
}
