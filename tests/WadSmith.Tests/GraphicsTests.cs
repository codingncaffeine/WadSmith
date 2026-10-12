using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;

namespace WadSmith.Tests;

public sealed class GraphicsTests
{
    public static TheoryData<string> BaseGames() => new() { "DOOM.WAD", "DOOM2.WAD", "PLUTONIA.WAD", "TNT.WAD", "HERETIC.WAD", "HEXEN.WAD", "STRIFE1.WAD", "freedoom1.wad", "freedoom2.wad" };

    [Theory]
    [MemberData(nameof(BaseGames))]
    public void TextureLumpsWriteBackByteForByte(string fileName)
    {
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var pnamesEntry = wad.Find("PNAMES")!;
        var pnames = TextureLump.ReadPatchNames(pnamesEntry.Read());
        Assert.Equal(pnamesEntry.Read(), TextureLump.WritePatchNames(pnames));
        foreach (var name in new[] { "TEXTURE1", "TEXTURE2" })
        {
            var e = wad.Find(name);
            if (e is null) continue;
            var lump = TextureLump.Read(e.Read(), pnames, name);
            Assert.NotEmpty(lump.Textures);
            Assert.Equal(fileName == "STRIFE1.WAD", lump.IsStrifeLayout);
            Assert.True(e.Read().AsSpan().SequenceEqual(lump.Write()), $"{fileName} {name} changes when written back");
        }
    }

    [Theory]
    [MemberData(nameof(BaseGames))]
    public void EveryPatchAndSpriteReEncodesToTheSamePixels(string fileName)
    {
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var count = 0;
        foreach (var e in wad.Entries.Where(e => !e.IsMarker && e.Namespace is ResourceNamespace.Patches or ResourceNamespace.Sprites && e.Size > 0))
        {
            var data = e.Read();
            if (ImageCodec.IsKnownImage(data)) continue;
            Assert.True(DoomPicture.IsValid(data), $"{fileName} {e.Name} is not a valid Doom picture");
            var pic = DoomPicture.Decode(data, e.Name);
            var again = DoomPicture.Decode(DoomPicture.Encode(pic), e.Name);
            Assert.Equal((pic.Width, pic.Height, pic.OffsetX, pic.OffsetY), (again.Width, again.Height, again.OffsetX, again.OffsetY));
            Assert.Equal(pic.Opaque, again.Opaque);
            for (var i = 0; i < pic.Indices.Length; i++)
            {
                if (pic.Opaque[i]) Assert.Equal(pic.Indices[i], again.Indices[i]);
            }

            count++;
        }

        Assert.True(count > 50, $"only {count} pictures were checked in {fileName}");
    }

    [Theory]
    [MemberData(nameof(BaseGames))]
    public void EveryFlatReads(string fileName)
    {
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var flats = wad.Entries.Where(e => !e.IsMarker && e.Namespace == ResourceNamespace.Flats && e.Size > 0).ToList();
        Assert.NotEmpty(flats);
        foreach (var f in flats)
        {
            var img = RawPicture.DecodeFlat(f.Read());
            Assert.True(img.Width >= 64, $"{fileName} {f.Name} read as {img.Width}x{img.Height}");
        }
    }

    [Theory]
    [MemberData(nameof(BaseGames))]
    public void EveryTextureOfABaseGameFindsAllItsPatches(string fileName)
    {
        var lib = new TextureLibrary(new ResourceStack([WadContainer.Open(GameFiles.Require(fileName))]));
        Assert.NotEmpty(lib.Walls);
        foreach (var t in lib.Walls.Where(t => !t.IsNull))
        {
            var img = lib.GetWall(t.Name);
            Assert.NotNull(img);
            Assert.Equal((t.Width, t.Height), (img.Width, img.Height));
        }

        Assert.Empty(lib.Notes);
    }

    [Fact]
    public void AMissingPatchIsNotedNotThrown()
    {
        var tex = new CompositeTexture { Name = "BROKEN", Width = 8, Height = 8, Origin = "test" };
        tex.Patches.Add(new PatchPlacement("NOSUCH", 0, 0));
        var lib = new TextureLibrary(new ResourceStack());
        var img = lib.Compose(tex);
        Assert.Equal(8, img.Width);
        Assert.Contains(lib.Notes, n => n.Contains("NOSUCH", StringComparison.Ordinal));
    }

    [Fact]
    public void APngKeepsItsOffsetsInAGrabChunk()
    {
        var img = new RgbaImage(3, 2) { OffsetX = -7, OffsetY = 40 };
        img.Set(0, 0, 255, 0, 0);
        img.Set(2, 1, 0, 0, 255, 128);
        var png = ImageCodec.EncodePng(img);
        Assert.True(ImageCodec.IsPng(png));
        Assert.Equal((-7, 40), ImageCodec.ReadGrab(png));
        var back = ImageCodec.Decode(png);
        Assert.Equal((-7, 40), (back.OffsetX, back.OffsetY));
        Assert.Equal(img.Pixels, back.Pixels);
    }

    [Fact]
    public void ATallPictureRoundTrips()
    {
        var pic = new PalettedImage(2, 700);
        for (var y = 0; y < 700; y++)
        {
            // Column 0: a gap every 100 rows; column 1: a single pixel far down.
            pic.Opaque[y * 2] = y % 100 != 0;
            pic.Indices[y * 2] = (byte)(y % 256);
        }

        pic.Opaque[650 * 2 + 1] = true;
        pic.Indices[650 * 2 + 1] = 7;
        var back = DoomPicture.Decode(DoomPicture.Encode(pic));
        Assert.Equal(pic.Opaque, back.Opaque);
        for (var i = 0; i < pic.Indices.Length; i++)
        {
            if (pic.Opaque[i]) Assert.Equal(pic.Indices[i], back.Indices[i]);
        }
    }

    [Fact]
    public void TexturesLumpDefinitionsAreRead()
    {
        const string text = """
            // comment
            WallTexture "BIGDOOR", 128, 96
            {
                XScale 2.0
                Patch "DOOR2_1", 0, 0 { FlipX Rotate 90 Blend "#FF0000", 0.5 }
                Patch "DOOR2_4", 64, 0
            }
            Flat optional "MYFLAT", 64, 64 { Patch "SOMEPIC", 0, 0 }
            """;
        var lump = TexturesLump.Parse(System.Text.Encoding.ASCII.GetBytes(text), "TEXTURES");
        Assert.Equal(2, lump.Definitions.Count);
        var (use, t) = lump.Definitions[0];
        Assert.Equal(TextureUse.Wall, use);
        Assert.Equal(2.0, t.ScaleX);
        Assert.True(t.Patches[0].FlipX);
        Assert.Equal(90, t.Patches[0].Rotate);
        Assert.Equal(64, t.Patches[1].X);
        Assert.Equal(TextureUse.Flat, lump.Definitions[1].Use);
        Assert.Contains(lump.Notes, n => n.Contains("blend", StringComparison.Ordinal));
    }
}

public sealed class SpriteTests
{
    [Theory]
    [InlineData("TROOA1", "TROO", 'A', '1', null, null)]
    [InlineData("TROOA2A8", "TROO", 'A', '2', 'A', '8')]
    [InlineData("PLAYN0", "PLAY", 'N', '0', null, null)]
    [InlineData("BOS2[1", "BOS2", '[', '1', null, null)]
    public void SpriteNamesParse(string lump, string sprite, char frame, char rotation, char? f2, char? r2)
    {
        Assert.True(SpriteFrameName.TryParse(lump, out var n));
        Assert.Equal(new SpriteFrameName(sprite, frame, rotation, f2, r2), n);
    }

    [Theory]
    [InlineData("TROO")]
    [InlineData("TROOA")]
    [InlineData("TROOAH")]
    [InlineData("TROOA1A")]
    public void NonSpriteNamesAreRefused(string lump) => Assert.False(SpriteFrameName.TryParse(lump, out _));

    [Fact]
    public void EveryMonsterSpriteOfDoomTwoHasAFrontFrame()
    {
        var index = new SpriteIndex(new ResourceStack([WadContainer.Open(GameFiles.Require("DOOM2.WAD"))]));
        foreach (var sprite in new[] { "TROO", "POSS", "SPOS", "SARG", "HEAD", "CYBR", "SPID", "FATT", "SKEL", "VILE", "PAIN", "SKUL", "BSPI", "BOSS", "BOS2", "CPOS", "SSWV", "KEEN" })
        {
            Assert.True(index.Front(sprite) is not null, $"{sprite} has no front frame");
        }
    }
}
