using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.Core.Exporting;
using WadSmith.Core.Games;
using WadSmith.Core.Projects;

namespace WadSmith.Tests;

/// <summary>The Share check (D22, Phase 7): what a project copied from games others must own, on the Export card; nothing for free games.</summary>
[Collection(TimingCollection.Name)]
public sealed class ShareCheckTests
{
    private static CopiedAsset Copy(string game, string name) => new() { Kind = "wall", Game = game, Original = name, Name = name, File = $"textures/{name}.png" };

    [Fact]
    public void CopiesFromCommercialGamesAreCountedByGameAndFreeOnesAreNot()
    {
        var doom2 = GameCatalog.Get("doom2")!.Title;
        var heretic = GameCatalog.Get("heretic")!.Title;
        var said = ProjectExport.ShareCheck([Copy("doom2", "STARTAN3"), Copy("doom2", "BRICK1"), Copy("heretic", "SKY1"), Copy("freedoom2", "STARTAN2"), Copy("picture", "MINE")]);
        Assert.Contains($"copied from {doom2} (2) and {heretic} (1), which whoever plays it must own", said, StringComparison.Ordinal);
        Assert.Null(ProjectExport.ShareCheck([Copy("freedoom2", "STARTAN2"), Copy("blasphemer", "FLOOR01"), Copy("picture", "MINE"), Copy("file", "RAIN")]));
    }

    [Fact]
    public async Task TheExportCardShowsTheShareCheckOnlyForACommercialCopy()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        GameFiles.Require("gzdoom.pk3");
        using var temp = new TempFolder();
        var notes = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session;
                var canvas = window.GetVisualDescendants().OfType<App.Canvas.MapCanvas>().First();
                string Card()
                {
                    canvas.Focus();
                    Ui.Pump(0.1);
                    window.KeyPress(Key.E, RawInputModifiers.Control, PhysicalKey.None, null);
                    Ui.Pump(0.2);
                    var export = editor.Export!;
                    for (var i = 0; i < 300 && export.Busy; i++) Ui.Pump(0.02);
                    Ui.Pump(0.1);
                    var shown = export.Notes.Any(n => n.StartsWith("Share check", StringComparison.Ordinal)) ? "share check" : "no share check";
                    export.CloseCommand.Execute(null);
                    Ui.Pump(0.2);
                    return shown;
                }

                var before = Card();
                session.Project.Record(Copy("doom2", "STARTAN3"));
                return $"{before}; with a Doom II texture: {Card()}";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("no share check; with a Doom II texture: share check", notes);
    }
}
