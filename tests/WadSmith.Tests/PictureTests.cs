using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Editing;

namespace WadSmith.Tests;

/// <summary>Pictures and animations as textures (D15): GIF frames composed and timed, written as frame textures and ANIMDEFS.</summary>
public sealed class PictureTests
{
    /// <summary>
    /// A GIF89a: 4 × 4, colours 0 black, 1 red, 2 blue, 3 white; frame 1 all red for 5/100 s; frame 2 only its 2 × 2 corner
    /// at (2, 2), blue, with a delay of 0 (shown for 100 ms, as browsers show it). LZW with a clear code before every pair of
    /// pixels, so codes stay 3 bits wide.
    /// </summary>
    internal static byte[] Gif()
    {
        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        w.Write("GIF89a"u8);
        w.Write((ushort)4); w.Write((ushort)4);
        w.Write((byte)0b1000_0001); w.Write((byte)0); w.Write((byte)0);
        w.Write(new byte[] { 0, 0, 0, 255, 0, 0, 0, 0, 255, 255, 255, 255 });
        w.Write(new byte[] { 0x21, 0xFF, 11 }); w.Write("NETSCAPE2.0"u8); w.Write(new byte[] { 3, 1, 0, 0, 0 });
        void Frame(int x, int y, int width, int height, byte colour, ushort delay)
        {
            w.Write(new byte[] { 0x21, 0xF9, 4, 0b0000_0100 }); w.Write(delay); w.Write(new byte[] { 0, 0 });
            w.Write((byte)0x2C); w.Write((ushort)x); w.Write((ushort)y); w.Write((ushort)width); w.Write((ushort)height); w.Write((byte)0);
            w.Write((byte)2);
            var codes = new List<int>();
            for (var i = 0; i < width * height; i++)
            {
                if (i % 2 == 0) codes.Add(4);
                codes.Add(colour);
            }

            codes.Add(5);
            var bytes = new List<byte>();
            int bits = 0, count = 0;
            foreach (var code in codes)
            {
                bits |= code << count;
                count += 3;
                while (count >= 8)
                {
                    bytes.Add((byte)bits);
                    bits >>= 8;
                    count -= 8;
                }
            }

            if (count > 0) bytes.Add((byte)bits);
            w.Write((byte)bytes.Count); w.Write(bytes.ToArray()); w.Write((byte)0);
        }

        Frame(0, 0, 4, 4, 1, 5);
        Frame(2, 2, 2, 2, 2, 0);
        w.Write((byte)0x3B);
        return file.ToArray();
    }

    [Fact]
    public void AGifsFramesAreComposedAndTimedAsBrowsersShowThem()
    {
        var frames = AnimatedPicture.Read(Gif(), "test.gif");
        Assert.Equal(2, frames.Count);
        Assert.Equal((255, 0, 0, 255), frames[0].Image.Get(3, 3));
        // The second frame draws only its corner over the first.
        Assert.Equal((255, 0, 0, 255), frames[1].Image.Get(0, 0));
        Assert.Equal((0, 0, 255, 255), frames[1].Image.Get(3, 3));
        Assert.Equal([50, 100], frames.Select(f => f.Milliseconds));
        Assert.Equal([2, 4], frames.Select(f => AnimatedPicture.Tics(f.Milliseconds)));
    }

    [Fact]
    public void TheEditorPlaysAnAnimationAtGZDoomsPace()
    {
        // Frame A for 2 tics, B for 4: 6 tics in all, 35 a second.
        var animation = (new[] { "A", "B" }, new[] { 2, 4 });
        Assert.Equal("A", Pictures.FrameAt(animation, 0));
        Assert.Equal("A", Pictures.FrameAt(animation, 1.9 / 35));
        Assert.Equal("B", Pictures.FrameAt(animation, 2.1 / 35));
        Assert.Equal("B", Pictures.FrameAt(animation, 5.9 / 35));
        Assert.Equal("A", Pictures.FrameAt(animation, 6.1 / 35));

        // The project's record gives the 3D view each animation by its first frame.
        var animations = Pictures.Animations([new WadSmith.Core.Projects.CopiedAsset { Kind = "animation", Game = "picture", Original = "x.gif", Name = "PORTA|2|2,4", File = Pictures.AnimationFile }]);
        Assert.Equal(["PORTA000", "PORTA001"], animations["PORTA000"].Frames);
        Assert.Equal([2, 4], animations["PORTA000"].Tics);
    }

    [Fact]
    public void ManyFramesAreThinnedRunningAsLong()
    {
        var one = new RgbaImage(1, 1);
        var frames = Enumerable.Range(0, 10).Select(_ => new AnimationFrame(one, 40)).ToList();
        var thinned = AnimatedPicture.Thin(frames, 4);
        Assert.Equal(4, thinned.Count);
        Assert.Equal(400, thinned.Sum(f => f.Milliseconds));
        Assert.Same(frames, AnimatedPicture.Thin(frames, 10));
    }

    [Fact]
    public void AnAnimationBecomesFrameTexturesAndAnimDefsAndAStillOneTexture()
    {
        using var temp = new TempFolder();
        var gif = Path.Combine(temp.Path, "portal_loop.gif");
        File.WriteAllBytes(gif, Gif());
        var still = Path.Combine(temp.Path, "poster.png");
        File.WriteAllBytes(still, ImageCodec.EncodePng(new RgbaImage(8, 4)));
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        var wad = GameFiles.Require("freedoom2.wad");
        var game = GameResources.Load("freedoom2", project.Folder, [new FoundGame(GameCatalog.Get("freedoom2")!, wad, new FileInfo(wad).Length)], null)!;

        var moving = Pictures.Import(project, game, gif);
        Assert.Equal(("PORTA000", 2, 4, 4), (moving.Texture, moving.Frames, moving.Width, moving.Height));
        Assert.True(game.Textures.Has("PORTA001"));
        Assert.NotNull(game.Textures.GetFlat("PORTA000"));
        var animdefs = File.ReadAllText(Path.Combine(project.Folder, Pictures.AnimationFile));
        Assert.Contains("texture PORTA000\n\tpic PORTA000 tics 2\n\tpic PORTA001 tics 4\n", animdefs, StringComparison.Ordinal);

        // The same file again gets frames of another stem.
        var again = Pictures.Import(project, game, gif);
        Assert.NotEqual("PORTA000", again.Texture);
        Assert.Contains($"texture {again.Texture}", File.ReadAllText(Path.Combine(project.Folder, Pictures.AnimationFile)), StringComparison.Ordinal);

        var poster = Pictures.Import(project, game, still);
        Assert.Equal(("POSTER", 1), (poster.Texture, poster.Frames));
        Assert.DoesNotContain("POSTER", File.ReadAllText(Path.Combine(project.Folder, Pictures.AnimationFile)), StringComparison.Ordinal);
    }
}
