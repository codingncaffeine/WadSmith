using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Graphics;
using WadSmith.Core.Maps;
using WadSmith.Core.Translation;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// Switches that show no switch picture (Eureka's "non-animating switch textures", D58): the game's switch pictures come
/// from GZDoom's own list and the files' ANIMDEFS and SWITCHES lumps, and a wall the player uses to work something
/// elsewhere without one is a Problem.
/// </summary>
public sealed class StillSwitchTests(ITestOutputHelper output)
{
    [Fact]
    public void AnimdefsSwitchesAreReadForTheirGameWithEveryPicture()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SwitchPictures.Animdefs("""
            // a comment: switch doom 1 NOTME on pic NOTME2 tics 0
            switch doom 1 SW1BRCOM on pic SW2BRCOM tics 0
            switch hexen SW_1_UP on sound Switch1 pic SW_1_DN tics 0
            switch "MYSW1" on pic "MYSW2" tics 4 pic MYSW3 tics 4
            /* switch doom 1 HIDDEN on pic HIDDEN2 tics 0 */
            """, "doom", names);
        Assert.Equal(["MYSW1", "MYSW2", "MYSW3", "SW1BRCOM", "SW2BRCOM"], names.Order(StringComparer.Ordinal));

        var boom = new byte[60];
        System.Text.Encoding.Latin1.GetBytes("SW1ABC").CopyTo(boom, 0);
        System.Text.Encoding.Latin1.GetBytes("SW2ABC").CopyTo(boom, 9);
        boom[18] = 3;
        System.Text.Encoding.Latin1.GetBytes("SW1XYZ").CopyTo(boom, 20);
        System.Text.Encoding.Latin1.GetBytes("SW2XYZ").CopyTo(boom, 29);
        var fromBoom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SwitchPictures.Switches(boom, fromBoom);
        Assert.Equal(["SW1ABC", "SW2ABC"], fromBoom.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AStarterRoomSwitchWithAPlainWallPictureIsAProblemAndASwitchPictureIsNot()
    {
        var resources = ProblemTests.Freedoom.Value;
        Assert.Contains("SW1BRCOM", resources.SwitchPictures);
        var (map, history, rooms) = MovingTests.Drawn((0, 0, 256, 256), (512, 0, 640, 128));
        var wall = map.Lines.First(l => l.OnMap && l.Back is null && l.Front.Sector == rooms[0]);
        history.Record("Wire", e =>
        {
            e.Set(rooms[1], s => s.Tag, (s, v) => s.Tag = v, 5);
            e.Set(wall, l => l.Special, (l, v) => l.Special = v, Editor.Smart.Doors.DoorOpen);
            e.Set(wall, l => l.Args[0], (l, v) => l.Args[0] = v, 5);
            e.Flag(wall, "playeruse", true);
            e.Set(wall.Front, s => s.Middle, (s, v) => s.Middle = v, "BROWN1");
        });
        IReadOnlyList<Problem> Check() => Problems.Check(MapGeometry.Build(map.Frame), resources, Look.For("doom"), "doom");
        Assert.Same(wall, Assert.Single(Assert.Single(Check(), p => p.Rule == "still-switch").Show));

        history.Record("Dress", e => e.Set(wall.Front, s => s.Middle, (s, v) => s.Middle = v, "SW1BRCOM"));
        Assert.DoesNotContain(Check(), p => p.Rule == "still-switch");

        // A door's own wall (tag 0: the door behind it) is no switch, whatever its picture.
        history.Record("Own", e => { e.Set(wall, l => l.Args[0], (l, v) => l.Args[0] = v, 0); e.Set(wall.Front, s => s.Middle, (s, v) => s.Middle = v, "BROWN1"); });
        Assert.DoesNotContain(Check(), p => p.Rule == "still-switch");
    }

    /// <summary>The games as shipped: how many maps have a switch without a switch picture (printed; id's own maps are the control).</summary>
    [Theory]
    [MemberData(nameof(DoorTests.DoomFormatGames), MemberType = typeof(DoorTests))]
    public void TheShippedGamesSwitchesHaveSwitchPictures(string fileName)
    {
        var path = GameFiles.Require(fileName);
        var games = GameLocator.Scan([Path.GetDirectoryName(path)!], cancel: TestContext.Current.CancellationToken);
        var game = games.FirstOrDefault(g => string.Equals(Path.GetFileName(g.Path), fileName, StringComparison.OrdinalIgnoreCase))?.Game.Id ?? games.First(g => g.Game.Engine == "doom").Game.Id;
        var resources = GameResources.Load(game, null, games, GameFiles.Require("gzdoom.pk3"))!;
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var still = new List<string>();
        foreach (var lumps in MapLocator.Find(WadContainer.Open(path)))
        {
            var map = EditorMap.FromModel(MapTranslation.ToZDoom(MapConversion.Read(lumps, "doom"), xlat, []));
            if (Problems.Check(MapGeometry.Build(map.Frame), resources, Look.For("doom"), "doom").FirstOrDefault(p => p.Rule == "still-switch") is { } p) still.Add($"{lumps.Name}: {p.Show.Count}");
        }

        output.WriteLine($"{fileName}: {still.Count} maps with still switches: {string.Join(", ", still)}");

        // The control: as shipped, a door's or lift's own walls (most of id's used walls) are no switches; what is left is a few
        // walls in some maps (hidden switches, exit signs that do not change), never half a game (before, nearly every map).
        Assert.True(still.Count * 2 < MapLocator.Find(WadContainer.Open(path)).Count(), $"{still.Count} maps of {fileName} have still switches");
    }
}
