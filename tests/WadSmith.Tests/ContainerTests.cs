using System.IO.Compression;
using System.Text;
using WadSmith.Core;
using WadSmith.Core.Containers;

namespace WadSmith.Tests;

public sealed class ContainerTests
{
    private static (string, ReadOnlyMemory<byte>) Lump(string name, string content = "") => (name, Encoding.ASCII.GetBytes(content));

    [Fact]
    public void AWrittenWadReadsBackWithTheSameLumps()
    {
        var bytes = WadWriter.Write(WadKind.Pwad, [Lump("MAP01"), Lump("THINGS", "abc"), Lump("PLAYPAL", "palette")]);
        var wad = WadContainer.FromBytes(bytes);
        Assert.Equal(WadKind.Pwad, wad.Kind);
        Assert.Equal(["MAP01", "THINGS", "PLAYPAL"], wad.Entries.Select(e => e.Name));
        Assert.Equal("abc", Encoding.ASCII.GetString(wad.Entries[1].Read()));
        Assert.Empty(wad.Warnings);
    }

    [Fact]
    public void MarkersPutLumpsInNamespaces()
    {
        var bytes = WadWriter.Write(WadKind.Pwad,
        [
            Lump("S_START"), Lump("TROOA1", "s"), Lump("S_END"),
            Lump("FF_START"), Lump("F1_START"), Lump("FLOOR0_1", "f"), Lump("F1_END"), Lump("F_END"),
            Lump("PP_START"), Lump("WALL00", "p"), Lump("PP_END"),
            Lump("TX_START"), Lump("MYTEX", "t"), Lump("TX_END"),
            Lump("DEMO1", "g"),
        ]);
        var wad = WadContainer.FromBytes(bytes);
        Assert.Equal(ResourceNamespace.Sprites, wad.Find("TROOA1", ResourceNamespace.Sprites)?.Namespace);
        Assert.NotNull(wad.Find("FLOOR0_1", ResourceNamespace.Flats));
        Assert.NotNull(wad.Find("WALL00", ResourceNamespace.Patches));
        Assert.NotNull(wad.Find("MYTEX", ResourceNamespace.Textures));
        Assert.NotNull(wad.Find("DEMO1"));
        Assert.Null(wad.Find("FLOOR0_1"));
        Assert.True(wad.Entries.Single(e => e.Name == "F1_START").IsMarker);
        Assert.Empty(wad.Warnings);
    }

    [Fact]
    public void AnUnclosedMarkerRunsToTheEndAndIsReported()
    {
        var wad = WadContainer.FromBytes(WadWriter.Write(WadKind.Pwad, [Lump("F_START"), Lump("LAVA1", "x")]));
        Assert.NotNull(wad.Find("LAVA1", ResourceNamespace.Flats));
        Assert.Contains(wad.Warnings, w => w.Contains("F_START is never closed", StringComparison.Ordinal));
    }

    [Fact]
    public void NamesAreFoundWithoutRegardToCaseAndTheLastDuplicateWins()
    {
        var wad = WadContainer.FromBytes(WadWriter.Write(WadKind.Pwad, [Lump("SECRET", "one"), Lump("SECRET", "two")]));
        Assert.Equal("two", Encoding.ASCII.GetString(wad.Find("secret")!.Read()));
    }

    [Fact]
    public void ACutShortDirectoryIsCorruptWithAPlainMessage()
    {
        var bytes = WadWriter.Write(WadKind.Pwad, [Lump("A", "1234"), Lump("B", "5678")]);
        var ex = Assert.Throws<CorruptDataException>(() => WadContainer.FromBytes(bytes[..^8], "cut.wad"));
        Assert.Equal("cut.wad's lump directory runs past the end of the file; the file is probably cut short.", ex.Message);
    }

    [Fact]
    public void ALumpPastTheEndIsReadAsEmptyWithAWarning()
    {
        var bytes = WadWriter.Write(WadKind.Pwad, [Lump("A", "1234")]);
        // Point lump A's offset far past the end.
        BitConverter.GetBytes(1_000_000).CopyTo(bytes, bytes.Length - 16);
        var wad = WadContainer.FromBytes(bytes);
        Assert.Empty(wad.Entries[0].Read());
        Assert.Contains(wad.Warnings, w => w.Contains("(A) runs past the end", StringComparison.Ordinal));
    }

    [Fact]
    public void ANameLongerThanEightCharactersCannotBeWritten()
    {
        Assert.Throws<ArgumentException>(() => WadWriter.Write(WadKind.Pwad, [Lump("TOOLONGNAME")]));
    }

    [Fact]
    public void ResourceContainerOpenDecidesByContent()
    {
        using var temp = new TempFolder();
        var wadPath = temp.File("named-like-a.pk3");
        File.WriteAllBytes(wadPath, WadWriter.Write(WadKind.Iwad, [Lump("PLAYPAL", "x")]));
        Assert.IsType<WadContainer>(ResourceContainer.Open(wadPath));

        var junk = temp.File("junk.wad");
        File.WriteAllText(junk, "hello world");
        var ex = Assert.Throws<CorruptDataException>(() => ResourceContainer.Open(junk));
        Assert.Equal("junk.wad is not a WAD, PK3 or ZIP file.", ex.Message);
    }

    [Fact]
    public void APk3MapsDirectoriesToNamespaces()
    {
        var zip = new MemoryStream();
        using (var a = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var w = new StreamWriter(a.CreateEntry(path).Open());
                w.Write(text);
            }

            Add("MAPINFO.txt", "map");
            Add("sprites/monsters/TROOA1.png", "s");
            Add("textures/MyLongTextureName.png", "t");
            Add("maps/MAP01.wad", "m");
            Add("models/thing.md3", "o");
            a.CreateEntry("sounds/");
        }

        var pk3 = ZipContainer.FromBytes(zip.ToArray(), "test.pk3");
        Assert.Equal(5, pk3.Entries.Count);
        Assert.Equal("map", Encoding.ASCII.GetString(pk3.Find("MAPINFO")!.Read()));
        Assert.Equal("sprites/monsters/TROOA1.png", pk3.Find("TROOA1", ResourceNamespace.Sprites)!.FullPath);
        Assert.NotNull(pk3.Find("mylongtexturename", ResourceNamespace.Textures));
        Assert.NotNull(pk3.Find("MAP01", ResourceNamespace.Maps));
        Assert.Equal(ResourceNamespace.Other, pk3.FindPath("models/thing.md3")!.Namespace);
    }

    [Fact]
    public void AFolderReadsLikeAPk3AndSkipsHiddenFiles()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("textures/brick.png"), "b");
        File.WriteAllText(temp.File("ANIMDEFS.txt"), "a");
        File.WriteAllText(temp.File(".git/config"), "x");
        var folder = new FolderContainer(temp.Path);
        Assert.Equal(["ANIMDEFS.txt", "textures/brick.png"], folder.Entries.Select(e => e.FullPath));
        Assert.NotNull(folder.Find("BRICK", ResourceNamespace.Textures));
    }

    [Fact]
    public void TheStackLetsLaterContainersReplaceEarlierOnes()
    {
        var iwad = WadContainer.FromBytes(WadWriter.Write(WadKind.Iwad, [Lump("TEXTURE1", "base"), Lump("PLAYPAL", "base")]));
        var pwad = WadContainer.FromBytes(WadWriter.Write(WadKind.Pwad, [Lump("TEXTURE1", "mod")]));
        var stack = new ResourceStack([iwad, pwad]);
        Assert.Equal("mod", Encoding.ASCII.GetString(stack.Find("TEXTURE1")!.Read()));
        Assert.Equal("base", Encoding.ASCII.GetString(stack.Find("PLAYPAL")!.Read()));
        Assert.Equal(["base", "mod"], stack.FindAll("TEXTURE1").Select(e => Encoding.ASCII.GetString(e.Read())));
    }

    public static TheoryData<string> RealWads()
    {
        var data = new TheoryData<string>();
        foreach (var w in GameFiles.AllWads) data.Add(Path.GetFileName(w));
        if (data.Count == 0) data.Add("(none)");
        return data;
    }

    [Theory]
    [MemberData(nameof(RealWads))]
    public void EveryRealWadOpensCleanlyAndRewritesLumpForLump(string fileName)
    {
        var path = GameFiles.Require(fileName);
        var wad = WadContainer.Open(path);
        Assert.Empty(wad.Warnings);
        Assert.NotEmpty(wad.Entries);

        var rewritten = WadContainer.FromBytes(WadWriter.Write(wad.Kind, wad.Entries.Select(e => (e.Name, (ReadOnlyMemory<byte>)e.Read()))));
        Assert.Equal(wad.Entries.Count, rewritten.Entries.Count);
        for (var i = 0; i < wad.Entries.Count; i++)
        {
            Assert.Equal(wad.Entries[i].Name, rewritten.Entries[i].Name);
            Assert.Equal(wad.Entries[i].Namespace, rewritten.Entries[i].Namespace);
            Assert.True(wad.LumpData(wad.Entries[i]).Span.SequenceEqual(rewritten.LumpData(rewritten.Entries[i]).Span), $"{fileName} lump {i} ({wad.Entries[i].Name}) differs after rewriting");
        }
    }
}
