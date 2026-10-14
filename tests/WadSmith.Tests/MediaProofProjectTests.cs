using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>
/// Writes a project holding every piece Phase 7 generates for GZDoom, for the engine's own check after the test sessions can
/// run (tools/engine/made-map-check.sh WORK GAMES FOLDER): placed sounds in each way of playing, a decoration from a GIF, a
/// sign, a mirror and a camera screen. Skipped unless WADSMITH_PROOF_PROJECT names a folder that does not exist yet.
/// </summary>
public sealed class MediaProofProjectTests
{
    /// <summary>A fifth of a second of a 440 Hz tone, 8-bit mono WAV.</summary>
    private static byte[] Tone()
    {
        const int rate = 11025, count = rate / 5;
        var data = new byte[count];
        for (var i = 0; i < count; i++) data[i] = (byte)(128 + 60 * Math.Sin(2 * Math.PI * 440 * i / rate));
        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        w.Write("RIFF"u8); w.Write(36 + count); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate); w.Write((short)1); w.Write((short)8);
        w.Write("data"u8); w.Write(count); w.Write(data);
        return file.ToArray();
    }

    [Fact]
    public void WritesAProjectWithEveryGeneratedPieceForGZDoom()
    {
        var folder = Environment.GetEnvironmentVariable("WADSMITH_PROOF_PROJECT");
        if (string.IsNullOrEmpty(folder) || Directory.Exists(folder)) Assert.Skip("Set WADSMITH_PROOF_PROJECT to a new folder to write the media proof project.");
        Write(folder!);
    }

    /// <summary>
    /// ZScript keeps words such as play, ui, action and state for itself, in any case: a method of that name stops GZDoom
    /// loading the whole project ("Unexpected 'play'" — the sounds' class once named its method Play). Every script the
    /// project writes is read for the methods it declares; the names seen must include the sounds' own, so the reading works.
    /// </summary>
    [Fact]
    public void NoGeneratedScriptNamesAMethodWithAWordZScriptKeeps()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "proof");
        Write(folder);
        var declared = new System.Text.RegularExpressions.Regex(@"^\s*(?:(?:static|override|virtual|action|clearscope|play|ui|private|protected|final|native|const)\s+)*[\w.<>]+\s+(\w+)\s*\(", System.Text.RegularExpressions.RegexOptions.Multiline);
        var names = Directory.EnumerateFiles(Path.Combine(folder, "zscript"), "*.zs", SearchOption.AllDirectories)
            .SelectMany(f => declared.Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Name: m.Groups[1].Value)))
            .ToList();
        Assert.Contains(names, n => n.Name == "SoundOf");
        Assert.Empty(names.Where(n => Kept.Contains(n.Name)).Select(n => $"{n.File}: {n.Name}"));
    }

    /// <summary>Words ZScript keeps (its scanner's keywords and type names), which a script may not use as a method's name.</summary>
    private static readonly HashSet<string> Kept = new(StringComparer.OrdinalIgnoreCase)
    {
        "play", "ui", "clearscope", "virtualscope", "action", "latent", "meta", "native", "static", "final", "abstract", "override",
        "virtual", "readonly", "transient", "property", "flagdef", "states", "default", "version", "out", "in", "sound", "state",
        "name", "color", "string", "map", "array", "let", "wait", "stop", "loop", "fail", "goto", "is", "vector2", "vector3",
        "class", "struct", "enum", "extend", "mixin", "const", "private", "protected", "deprecated", "internal", "replaces",
        "self", "super", "sizeof", "alignof", "null", "true", "false", "foreach", "function", "void", "int", "uint", "bool",
        "float", "double", "char", "byte", "short", "long",
    };

    private static void Write(string folder)
    {
        var wad = GameFiles.Require("freedoom2.wad");
        var engineData = GameFiles.Require("gzdoom.pk3");
        var project = Project.Create(folder, "Media proof", "doom2");
        var game = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], engineData)!;
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, "room -512 -256 512 256\nstart -448 0 0", Look.For("doom"), "doom"), s => Assert.True(s.Done, s.Said));
        var model = map.Frame.ToModel();
        void Place(int type, double x, double y, int angle = 90, int[]? args = null, int id = 0, double height = 0)
        {
            var t = new MapThing { X = x, Y = y, Type = type, Angle = angle, Id = id, Height = height };
            foreach (var f in Things.Everywhere("doom")) t.SetFlag(f, true);
            for (var i = 0; args is not null && i < args.Length; i++) t.Args[i] = args[i];
            model.Things.Add(t);
        }

        // Sounds: one file, placed four times, once in each way of playing.
        using var temp = new TempFolder();
        var tone = Path.Combine(temp.Path, "proof tone.wav");
        File.WriteAllBytes(tone, Tone());
        var sound = SoundFiles.ImportSound(project, game, tone);
        foreach (var (plays, x) in new[] { (SoundPlays.Loop, -320.0), (SoundPlays.Random, -160.0), (SoundPlays.Every, 0.0), (SoundPlays.Once, 160.0) })
        {
            Place(sound.Number, x, 160, args: (SoundSettings.New(sound.Sound) with { Plays = plays, Wait = 2, LongestWait = 4 }).Args);
        }

        // A decoration from a GIF, in the player's view.
        var gif = Path.Combine(temp.Path, "proof portal.gif");
        File.WriteAllBytes(gif, PictureTests.Gif());
        var decoration = PictureProps.Import(project, game, gif);
        Place(decoration.Actor.Number, -256, 0);

        // A sign on the north wall, a mirror on the south wall, a camera screen on the east wall, the camera looking west.
        MapLine LineWhere(Func<MapVertex, MapVertex, bool> which) => model.Lines.First(l => l.Back < 0 && which(model.Vertices[l.V1], model.Vertices[l.V2]));
        var sign = SignPictures.Write(project, game, SignSettings.New with { Text = "WadSmith media proof" }, 1024, 128, "STARTAN3");
        model.Sides[LineWhere((a, b) => a.Y == 256 && b.Y == 256).Front].Middle = sign.Texture;
        LineWhere((a, b) => a.Y == -256 && b.Y == -256).Special = 182;
        var screen = Cameras.Make(project, game, "MAP01", 7, 512, 128);
        model.Sides[LineWhere((a, b) => a.X == 512 && b.X == 512).Front].Middle = screen.Texture;
        Place(Cameras.SecurityCamera, 0, -192, angle: 180, id: 7, height: 40);

        ProjectMapInfo.Refresh(project, game.Game, game.Files, engineData);
        project.SaveMap(model, "Media proof");
        Assert.True(File.Exists(Path.Combine(project.Folder, SoundFiles.ScriptFile)));
        Assert.True(File.Exists(Path.Combine(project.Folder, Cameras.ScriptFile)));
    }
}
