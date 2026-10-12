using WadSmith.Editor.Checks;

namespace WadSmith.Tests;

/// <summary>GZDoom's log of a Play read back onto the map (lines as GZDoom 4.14 writes them, measured in a nested Play).</summary>
public sealed class EngineLogTests
{
    private const string Log = """
        Texman.Init: Init texture manager.
        MAP01 - Entryway
        Unknown middle texture 'NOSUCHTX' on first side of linedef 2
        Unknown upper texture 'NOSUCHTX' on second side of linedef 4
        Unknown floor texture 'NOSUCHFL' in sector 1
        """;

    [Fact]
    public void TheEnginesCountsNameTheMapsOwnWallsAndAreasSkippingEmptySlots()
    {
        // A small room drawn first and taken off again leaves empty slots before the others; the saved map does not count them.
        var (map, history, rooms) = MovingTests.Drawn((-512, -512, -448, -448), (0, 0, 256, 256), (256, 0, 512, 256));
        var gone = rooms[0];
        history.Record("Remove", e =>
        {
            foreach (var l in map.Lines.Where(l => l.OnMap && ReferenceEquals(l.Front.Sector, gone)).ToList()) e.Remove(l);
            e.Remove(gone);
        });
        var played = map.Frame;
        var lines = played.Lines.Where(l => l.Element is not null).Select(l => l.Element!).ToList();
        var areas = played.Sectors.Where(a => a.Element is not null).Select(a => a.Element!).ToList();
        Assert.True(played.Lines[0].Element is null && played.Sectors[0].Element is null);

        // The second side named is the shared wall's: the one wall with a room behind it.
        var shared = lines.FindIndex(l => l.Back is not null);
        var findings = EngineLog.Read(Log.Replace("second side of linedef 4", $"second side of linedef {shared}", StringComparison.Ordinal), played);
        Assert.Equal(3, findings.Count);
        Assert.Same(lines[2].Front, findings[0].Where);
        Assert.Equal(("NOSUCHTX", "middle"), (findings[0].Texture, findings[0].Part));
        Assert.Same(lines[shared].Back, findings[1].Where);
        Assert.Equal("upper", findings[1].Part);
        Assert.Same(areas[1], findings[2].Where);
        Assert.NotSame(played.Lines[2].Element, lines[2]);

        // A side the wall does not have is no finding.
        Assert.Equal(2, EngineLog.Read(Log.Replace("second side of linedef 4", "second side of linedef 0", StringComparison.Ordinal), played).Count);
    }

    [Fact]
    public void AFindingLastsOnlyWhileTheTextureIsStillThere()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 0, 512, 256));
        var lines = map.Lines.Where(l => l.OnMap).ToList();
        history.Record("Plant", e =>
        {
            e.Set(lines[2].Front, s => s.Middle, (s, v) => s.Middle = v, "NOSUCHTX");
            e.Set(rooms[1], s => s.FloorTexture, (s, v) => s.FloorTexture = v, "NOSUCHFL");
        });
        var findings = EngineLog.Read(Log, map.Frame);
        var still = EngineLog.Still(findings);
        Assert.Equal(2, still.Count);
        var problems = EngineLog.Problems(still);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Sentence.Contains("no texture called NOSUCHTX for 1 wall and drew a checkerboard", StringComparison.Ordinal));

        // What WadSmith's own check already names is not said twice.
        Assert.Single(EngineLog.Problems(still, new HashSet<Editor.Model.Element>(ReferenceEqualityComparer.Instance) { lines[2] }));
        Assert.Single(EngineLog.Problems(still, new HashSet<Editor.Model.Element>(ReferenceEqualityComparer.Instance) { rooms[1] }));
        Assert.All(problems, p => Assert.Equal(Severity.Error, p.Severity));

        history.Record("Fix", e => e.Set(lines[2].Front, s => s.Middle, (s, v) => s.Middle = v, "STARTAN3"));
        Assert.Equal("NOSUCHFL", Assert.Single(EngineLog.Still(findings)).Texture);
    }
}
