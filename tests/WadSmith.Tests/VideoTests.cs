using System.Diagnostics;
using Avalonia.VisualTree;
using WadSmith.App.Canvas;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>Videos on walls, doors and floors (Phase 7): frames through the user's ffmpeg, as one texture that moves. Skipped where there is no ffmpeg (CI).</summary>
[Collection(TimingCollection.Name)]
public sealed class VideoTests
{
    /// <summary>A two-second 640 × 360 test video made by ffmpeg itself (MPEG-4 in Matroska: no extra codec library needed).</summary>
    internal static string MakeVideo(string folder)
    {
        var ffmpeg = VideoFrames.Ffmpeg();
        if (ffmpeg is null) Assert.Skip("ffmpeg is not installed.");
        var path = Path.Combine(folder, "test card.mkv");
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-nostdin", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=25", "-t", "2", "-c:v", "mpeg4", path }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var said = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(File.Exists(path), said);
        return path;
    }

    [Fact]
    public void AVideosFramesBecomeOneTextureThatMovesThroughThem()
    {
        using var temp = new TempFolder();
        var video = MakeVideo(temp.Path);
        var frames = VideoFrames.Read(video);
        Assert.Equal(20, frames.Count);
        Assert.All(frames, f => Assert.Equal((320, 180, 100), (f.Image.Width, f.Image.Height, f.Milliseconds)));

        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var wad = GameFiles.Require("freedoom2.wad");
        var game = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;
        var made = Pictures.ImportFrames(project, game, "test card.mkv", frames);
        Assert.Equal(("TESTC000", 20), (made.Texture, made.Frames));
        Assert.Contains("texture TESTC000\n\tpic TESTC000 tics 4\n", File.ReadAllText(Path.Combine(project.Folder, Pictures.AnimationFile)), StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.GetDirectories(Path.GetTempPath(), "wadsmith-video-*"), d => Directory.GetCreationTimeUtc(d) > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task AVideoDroppedInARoomCoversItsFloor()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var video = MakeVideo(temp.Path);
        var said = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            try
            {
                var editor = Ui.NewMap(vm);
                var session = editor.Session!;
                var canvas = window.GetVisualDescendants().OfType<MapCanvas>().First(c => c.IsEffectivelyVisible);
                var room = session.Map.Sectors.First(s => s.OnMap);
                var corners = session.Geometry.LoopsAt(room.Slot).SelectMany(l => l).ToArray();
                var middle = new MapPoint((corners.Min(p => p.X) + corners.Max(p => p.X)) / 2, (corners.Min(p => p.Y) + corners.Max(p => p.Y)) / 2);
                SoundInTheWindowTests.DropFile(window, canvas, middle, video);
                for (var i = 0; i < 400 && room.FloorTexture != "TESTC000"; i++) Ui.Pump(0.05);
                var moving = Pictures.Animations(session.Project.CopiedNow()).TryGetValue(room.FloorTexture, out var a) ? a.Frames.Length : 0;
                return $"floor {room.FloorTexture} moving through {moving} frames";
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("floor TESTC000 moving through 20 frames", said);
    }
}
