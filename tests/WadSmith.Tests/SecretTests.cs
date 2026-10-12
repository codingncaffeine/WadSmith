using WadSmith.Core.Containers;
using WadSmith.Core.Maps;
using WadSmith.Core.Maps.Udmf;
using WadSmith.Core.Translation;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;
using WadSmith.Editor.Smart;

namespace WadSmith.Tests;

/// <summary>Secrets: made from a room with its entrances hidden, a door into one made to look like the wall, recognised in any map.</summary>
public sealed class SecretTests(ITestOutputHelper output)
{
    private static string Udmf(MapModel m) => UdmfText.WriteString(MapConversion.ToUdmf(m));

    private static MapGeometry Shapes(EditorMap map) => MapGeometry.Build(map.Frame);

    [Fact]
    public void ASecretCountsAndItsEntrancesAreSolidWallsOnTheMap()
    {
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var before = Udmf(map.ToModel());
        Assert.Null(Secrets.Recognise(Shapes(map), rooms[1]));
        history.Record("Make a secret", e => Secrets.Make(e, Shapes(map), rooms[1]));

        var secret = Secrets.Recognise(Shapes(map), rooms[1]);
        Assert.NotNull(secret);
        Assert.Equal(Secrets.Flag, rooms[1].Special);
        var way = Assert.Single(secret.Entrances);
        Assert.True(way.Flag("secret"));
        Assert.True(secret.Hidden);
        Assert.Empty(secret.Doors);
        Assert.Null(Secrets.Recognise(Shapes(map), rooms[0]));

        history.Record("Not a secret", e => Secrets.Clear(e, Shapes(map), rooms[1]));
        Assert.Equal(before, Udmf(map.ToModel()));
        history.Undo();
        history.Undo();
        Assert.Equal(before, Udmf(map.ToModel()));
    }

    [Fact]
    public void AnAreaWithATypeOfItsOwnKeepsItAsASecret()
    {
        // A blinking light (type 1): the secret is a bit added to it, and taken off again.
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        rooms[1].Special = 1;
        history.Record("Make a secret", e => Secrets.Make(e, Shapes(map), rooms[1]));
        Assert.Equal(1 | Secrets.Flag, rooms[1].Special);
        Assert.NotNull(Secrets.Recognise(Shapes(map), rooms[1]));
        history.Record("Not a secret", e => Secrets.Clear(e, Shapes(map), rooms[1]));
        Assert.Equal(1, rooms[1].Special);
        Assert.Null(Secrets.Recognise(Shapes(map), rooms[1]));
    }

    [Fact]
    public void ADoorIntoASecretIsItsEntranceAndCanLookLikeTheWallBesideIt()
    {
        // A hall and a small room east of it behind a door made with the Door tool.
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (256, 64, 384, 192));
        var shared = map.Lines.Single(l => l.OnMap && l.Back is not null);
        Sector? door = null;
        history.Record("Make a door", e => door = Doors.Make(e, Shapes(map), shared, Editor.Look.For("doom")).Door);
        Assert.NotNull(door);
        history.Record("Make a secret", e => Secrets.Make(e, Shapes(map), rooms[1]));

        var secret = Secrets.Recognise(Shapes(map), rooms[1])!;
        var found = Assert.Single(secret.Doors);
        Assert.Same(door, found.Area);
        var outside = Assert.Single(secret.Entrances, l => Doors.Recognise(Shapes(map), door) is { } d && d.Openers.Contains(l) && (l.Front.Sector == rooms[0] || l.Back?.Sector == rooms[0]));
        Assert.True(secret.Hidden);
        Assert.Equal(Editor.Look.For("doom").BigDoor, (outside.Front.Sector == rooms[0] ? outside.Front : outside.Back!).Top);

        history.Record("Hide the secret door", e => Secrets.HideDoor(e, Shapes(map), found, rooms[1]));
        var face = outside.Front.Sector == rooms[0] ? outside.Front : outside.Back!;

        // The hall's east wall runs on into the door's face: the same texture, carried on across and down
        // (the door's texture hangs from its ceiling, 128 below the hall's).
        var (a, _) = outside.Front.Sector == rooms[0] ? (outside.Start, outside.End) : (outside.End, outside.Start);
        var before = map.Lines.Single(l => l.OnMap && l.Back is null && l.Front.Sector == rooms[0] && l.End == a);
        Assert.Equal(before.Front.Middle, face.Top);
        Assert.Equal(before.Front.OffsetX + before.Length, face.OffsetX);
        Assert.Equal(128, face.OffsetY);
        Assert.True(outside.Flag("secret"));
        DrawingTests.AssertSound(map);

        // It is still the same door.
        Assert.Equal(found.Settings, Doors.Recognise(Shapes(map), door!)!.Settings);
    }

    /// <summary>
    /// The control is the map as the game shipped it: areas of type 9 are its secrets (Boom's generalised
    /// secret bit, 128, too). The recogniser sees only the translated map.
    /// </summary>
    [Theory]
    [MemberData(nameof(DoorTests.DoomFormatGames), MemberType = typeof(DoorTests))]
    public void EveryClassicSecretIsRecognisedAndNothingElse(string fileName)
    {
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var wad = WadContainer.Open(GameFiles.Require(fileName));
        var (secrets, hidden, maps) = (0, 0, 0);
        foreach (var lumps in MapLocator.Find(wad))
        {
            var classic = MapConversion.Read(lumps, "doom");
            var expected = Enumerable.Range(0, classic.Sectors.Count).Where(i => classic.Sectors[i].Special == 9 || (classic.Sectors[i].Special & 128) != 0).ToHashSet();
            var map = EditorMap.FromModel(MapTranslation.ToZDoom(classic, xlat, []));
            var geometry = MapGeometry.Build(map.Frame);
            var found = map.Sectors.Select(s => Secrets.Recognise(geometry, s)).OfType<Secret>().ToList();
            var slots = found.Select(s => s.Area.Slot).ToHashSet();
            Assert.True(expected.SetEquals(slots), $"{fileName} {lumps.Name}: missed {string.Join(", ", expected.Except(slots))}; not secrets {string.Join(", ", slots.Except(expected))}");
            secrets += found.Count;
            hidden += found.Count(s => s.Hidden);
            maps++;
        }

        output.WriteLine($"{fileName}: {secrets} secrets in {maps} maps, all recognised, nothing else; {hidden} with every way in hidden on the map");
    }
}
