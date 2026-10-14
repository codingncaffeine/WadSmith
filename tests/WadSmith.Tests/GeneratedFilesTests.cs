using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;
using WadSmith.Core.Projects;
using WadSmith.Editor.App;

namespace WadSmith.Tests;

/// <summary>The text view of the files WadSmith writes (§3.10): which files it lists, and their colouring.</summary>
public sealed class GeneratedFilesTests
{
    [Fact]
    public void ThePiecesJoinBackAndSayWhatEachIs()
    {
        const string text = "// Written by WadSmith\nclass Lamp : Actor\n{\n\tDefault { Radius 16; Tag \"Tall lamp\"; }\n\t/* still */ States { Spawn: LAMP A -1; stop; }\n}\n# a line comment\nmap MAP01 lookup \"x\" { sky1 = \"SKY1\", 0.5 }\n";
        var pieces = GeneratedFiles.Pieces(text);
        Assert.Equal(text, string.Concat(pieces.Select(p => p.Text)));
        string[] Of(CodeKind kind) => [.. pieces.Where(p => p.Kind == kind).Select(p => p.Text)];
        Assert.Equal(["// Written by WadSmith", "/* still */", "# a line comment"], Of(CodeKind.Comment));
        Assert.Equal(["\"Tall lamp\"", "\"x\"", "\"SKY1\""], Of(CodeKind.Text));
        Assert.Equal(["16", "1", "0.5"], Of(CodeKind.Number));
        Assert.Equal(["class", "Actor", "Default", "States", "stop", "map", "lookup", "sky1"], Of(CodeKind.Word));
    }

    [Fact]
    public void OnlyTheFilesWadSmithWroteAreListedRootFirst()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        void Write(string relative, string text)
        {
            var path = Path.Combine(project.Folder, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        Write("zmapinfo.txt", "// Written by WadSmith from the maps' settings\n");
        Write(Path.Combine("zscript", "wadsmith", "Lamp.zs"), "// Tall lamp, a picture brought in with WadSmith as a decoration.\n");
        Write("animdefs.txt", "// Written by WadSmith: pictures brought in that move.\n");
        Write("mapinfo.txt", "// my own\n");
        Write(Path.Combine("credits", "r667-1.txt"), "Fetched by WadSmith\n");
        Write(Path.Combine(".wadsmith", "notes.txt"), "WadSmith\n");
        Assert.Equal(["animdefs.txt", "zmapinfo.txt", "zscript/wadsmith/Lamp.zs"], GeneratedFiles.List(project));
    }
}

/// <summary>The files WadSmith writes, opened as the user does: the Inspector's row with nothing selected, then the file chosen.</summary>
[Collection(TimingCollection.Name)]
public sealed class GeneratedFilesInTheWindowTests
{
    [Fact]
    public async Task TheInspectorsRowOpensTheFilesAsTheyAreColoured()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                // A map with a name of its own has a zmapinfo.txt, which WadSmith writes.
                editor.Session.ChangeMapSettings(m => m with { Title = "Lamp hall" });
                var zmapinfo = Path.Combine(editor.Session.Project.Folder, "zmapinfo.txt");
                Ui.Until(() => File.Exists(zmapinfo) && File.ReadAllText(zmapinfo).Contains("Lamp hall", StringComparison.Ordinal));
                editor.Session.Selection.Clear();
                editor.Panel = SidePanel.Inspector;
                Ui.Until(() => window.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyVisible && b.DataContext is ActionRow { Label: "Files WadSmith writes…" }));
                var row = window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.DataContext is ActionRow { Label: "Files WadSmith writes…" });
                row.BringIntoView();
                Ui.Pump(0.2);
                var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
                window.MouseMove(at, RawInputModifiers.None);
                window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
                Ui.Until(() => editor.Files is { Pieces.Count: > 0 });
                var code = window.GetVisualDescendants().OfType<CodeText>().First(c => c.IsEffectivelyVisible);
                Ui.Until(() => code.Inlines is { Count: > 0 });
                var shown = string.Concat(code.Inlines!.OfType<Run>().Select(r => r.Text));
                var onDisk = File.ReadAllText(Path.Combine(editor.Session.Project.Folder, editor.Files!.Selected!));
                var colours = code.Inlines!.OfType<Run>().Select(r => (r.Foreground as ISolidColorBrush)?.Color).Distinct().Count();
                return $"files {string.Join(",", editor.Files.Files)}; shown as on disk {shown == onDisk}; colours {(colours >= 3 ? "several" : colours.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("files zmapinfo.txt; shown as on disk True; colours several", said);
    }
}
