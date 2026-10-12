using System.Buffers.Binary;
using System.Text.Json;
using WadSmith.Core.Assets;
using WadSmith.Core.Audio;
using WadSmith.Core.Containers;
using WadSmith.Core.Graphics;
using WadSmith.Core.Maps;

namespace WadSmith.Tests;

public sealed class ExtractorTests
{
    public static TheoryData<string> BaseGames() => new() { "DOOM2.WAD", "HERETIC.WAD", "HEXEN.WAD", "STRIFE1.WAD", "freedoom2.wad" };

    [Theory]
    [MemberData(nameof(BaseGames))]
    public void ExtractingABaseGameAccountsForEveryLump(string fileName)
    {
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        using var temp = new TempFolder();
        var result = AssetExtractor.Extract(wad, new ResourceStack([wad]), temp.Path, cancel: TestContext.Current.CancellationToken);

        Assert.Empty(result.Problems);
        var counts = result.Counts;
        int Count(AssetKind k) => counts.GetValueOrDefault(k);

        var maps = MapLocator.Find(wad);
        Assert.Equal(maps.Count, Count(AssetKind.Maps));
        Assert.Equal(wad.Entries.Count(e => !e.IsMarker && e.Namespace == ResourceNamespace.Flats), Count(AssetKind.Floors));
        Assert.Equal(wad.Entries.Count(e => !e.IsMarker && e.Namespace == ResourceNamespace.Patches), Count(AssetKind.Patches));
        Assert.Equal(wad.Entries.Count(e => !e.IsMarker && e.Namespace == ResourceNamespace.Sprites), Count(AssetKind.Sprites));
        Assert.Equal(new TextureLibrary(new ResourceStack([wad])).Walls.Count, Count(AssetKind.Textures));
        Assert.Equal(wad.Entries.Count(e => !e.IsMarker && e.Namespace == ResourceNamespace.Global && AudioFormats.Detect(e.Read()) == AudioKind.DmxSound), Count(AssetKind.Sounds) - result.Assets.Count(a => a.Kind == AssetKind.Sounds && a.Group != "Global"));
        Assert.Equal(wad.Entries.Count(e => !e.IsMarker && AudioFormats.Detect(e.Read()) is AudioKind.Mus or AudioKind.Midi), result.Assets.Count(a => a.Kind == AssetKind.Music && a.Path.EndsWith(".mid", StringComparison.Ordinal)));

        // Every lump outside a map ends up somewhere, under its own name.
        var mapLumps = new HashSet<string>(maps.SelectMany(m => m.Lumps.Select(l => l.Name)).Concat(maps.Select(m => m.Name)), LumpName.Comparer);
        var extracted = new HashSet<string>(result.Assets.Select(a => a.Lump), LumpName.Comparer);
        foreach (var e in wad.Entries.Where(e => !e.IsMarker && e.Size > 0 && !mapLumps.Contains(e.Name)))
        {
            Assert.True(extracted.Contains(e.Name), $"{fileName}: {e.Name} ({e.Namespace}) was not extracted");
        }

        foreach (var a in result.Assets) Assert.True(new FileInfo(Path.Combine(temp.Path, a.Path)).Length > 0, $"{a.Path} is empty");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(temp.Path, "manifest.json")));
        Assert.Equal(result.Assets.Count, manifest.RootElement.GetProperty("assets").GetArrayLength());
    }

    [Fact]
    public void AnAddOnBorrowsTheBaseGamePalette()
    {
        var iwad = WadContainer.Open(GameFiles.Require("DOOM.WAD"));
        var sigil = WadContainer.Open(GameFiles.Require("SIGIL_v1_21.wad"));
        using var temp = new TempFolder();
        var result = AssetExtractor.Extract(sigil, new ResourceStack([iwad, sigil]), temp.Path, cancel: TestContext.Current.CancellationToken);
        Assert.Empty(result.Problems);
        Assert.Equal(9, result.Counts[AssetKind.Maps]);
    }

    public static TheoryData<string> MusicGames() => new() { "DOOM.WAD", "DOOM2.WAD", "HERETIC.WAD", "HEXEN.WAD" };

    [Fact]
    public void AHandBuiltMusScoreConvertsToTheExpectedMidi()
    {
        // Header: id, score length 9, score start 16, 1 channel, 0 secondary, 0 instruments, padding.
        byte[] mus =
        [
            (byte)'M', (byte)'U', (byte)'S', 0x1A, 11, 0, 16, 0, 1, 0, 0, 0, 0, 0, 0, 0,
            0x90, 0xBC, 0x64, 0x46,  // play note 60 at volume 100 (last), delay 70 ticks
            0x00, 0x3C,              // release note 60
            0x4F, 0x03, 0x50,        // MUS channel 15 (percussion): controller 3 (volume) = 80
            0x60,                    // score end
            0x00,
        ];
        var midi = MusToMidi.Convert(mus);
        byte[] expectedTrack =
        [
            0x00, 0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20, // tempo 500000 us per quarter
            0x00, 0x90, 0x3C, 0x64,                   // note on, channel 1, note 60, velocity 100
            0x46, 0x80, 0x3C, 0x00,                   // 70 ticks later: note off
            0x00, 0xB9, 0x07, 0x50,                   // percussion (MIDI channel 10): volume 80
            0x00, 0xFF, 0x2F, 0x00,                   // end of track
        ];
        Assert.Equal(expectedTrack, midi.AsSpan(22).ToArray());
        Assert.Equal(70, BinaryPrimitives.ReadInt16BigEndian(midi.AsSpan(12)));
    }

    [Theory]
    [MemberData(nameof(MusicGames))]
    public void EveryMusTrackBecomesWellFormedMidi(string fileName)
    {
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var tracks = 0;
        foreach (var e in wad.Entries.Where(e => !e.IsMarker))
        {
            var data = e.Read();
            if (AudioFormats.Detect(data) != AudioKind.Mus) continue;
            var ticks = MidiTicks(MusToMidi.Convert(data, e.Name), $"{fileName} {e.Name}");
            Assert.True(ticks > 0, $"{fileName} {e.Name} has no length");
            tracks++;
        }

        Assert.True(tracks > 0, $"{fileName} has no MUS music");
    }

    /// <summary>Parses a format-0 MIDI file the way a player reads it and returns its length in ticks.</summary>
    private static long MidiTicks(byte[] midi, string name)
    {
        Assert.True(midi.AsSpan().StartsWith("MThd"u8), name);
        Assert.Equal(6, BinaryPrimitives.ReadInt32BigEndian(midi.AsSpan(4)));
        Assert.True(midi.AsSpan(14).StartsWith("MTrk"u8), name);
        var length = BinaryPrimitives.ReadInt32BigEndian(midi.AsSpan(18));
        Assert.Equal(midi.Length - 22, length);
        var pos = 22;
        long ticks = 0;
        var ended = false;
        while (pos < midi.Length)
        {
            Assert.False(ended, $"{name} has events after its end");
            long delay = 0;
            byte b;
            do
            {
                b = midi[pos++];
                delay = (delay << 7) | (b & 0x7Fu);
            }
            while ((b & 0x80) != 0);
            ticks += delay;
            var status = midi[pos++];
            Assert.True(status >= 0x80, $"{name}: running status or data byte where a status belongs at {pos - 1}");
            if (status == 0xFF)
            {
                var type = midi[pos++];
                int len = midi[pos++];
                if (type == 0x2F) ended = true;
                pos += len;
                continue;
            }

            var dataBytes = (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;
            for (var i = 0; i < dataBytes; i++) Assert.True(midi[pos + i] < 0x80, $"{name}: data byte out of range at {pos + i}");
            pos += dataBytes;
        }

        Assert.True(ended, $"{name} does not end with an end-of-track event");
        return ticks;
    }

    [Fact]
    public void FileNamesKeepSpriteFramesReadable()
    {
        Assert.Equal("BOS2^1", AssetExtractor.SafeName("BOS2\\1"));
        Assert.Equal("A_B_", AssetExtractor.SafeName("A:B?"));
    }
}
