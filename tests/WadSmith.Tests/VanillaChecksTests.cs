using WadSmith.Core.Containers;
using WadSmith.Core.Projects;
using WadSmith.Core.Translation;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// Eureka's checks WadSmith lacked (D58): objects on no difficulty, too few deathmatch starts for vanilla Doom, and the
/// pictures vanilla Doom cannot draw where they are (Medusa, tutti-frutti) — the last two for a vanilla target only.
/// </summary>
public sealed class VanillaChecksTests
{
    private static readonly Lazy<XlatTranslator> Xlat = new(() => XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt"));

    private static ClassicRules Vanilla => new("doom", Xlat.Value, Targets.Vanilla);

    private static IReadOnlyList<Problem> Check(EditorMap map, ClassicRules? classic = null) =>
        Problems.Check(MapGeometry.Build(map.Frame), ProblemTests.Freedoom.Value, Look.For("doom"), "doom", null, classic);

    [Fact]
    public void AnObjectOnNoDifficultyIsAProblemAndTheFixPutsItOnEveryOne()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var history = new History(map);
        var imp = new Thing { X = 0, Y = 64, Type = 3001 };
        history.Record("Place", e => e.Add(imp));
        var problem = Assert.Single(Check(map), p => p.Rule == "no-difficulty");
        Assert.Same(imp, Assert.Single(problem.Show));
        Assert.Contains("never puts it on the map", problem.Sentence);

        history.Record(problem.FixLabel!, problem.Fix!);
        Assert.DoesNotContain(Check(map), p => p.Rule == "no-difficulty");
        Assert.All(new[] { "skill1", "skill2", "skill3", "skill4", "skill5" }, s => Assert.True(imp.Flag(s)));
    }

    [Fact]
    public void FewerThanFourDeathmatchStartsIsAProblemForVanillaOnly()
    {
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var history = new History(map);
        for (var i = 0; i < 2; i++)
        {
            var start = new Thing { X = -64 + 128 * i, Y = -64, Type = 11 };
            foreach (var s in new[] { "skill1", "skill2", "skill3", "skill4", "skill5" }) start.SetFlag(s, true);
            history.Record("Place", e => e.Add(start));
        }

        Assert.DoesNotContain(Check(map), p => p.Rule == "few-deathmatch-starts");
        var problem = Assert.Single(Check(map, Vanilla), p => p.Rule == "few-deathmatch-starts");
        Assert.Equal(2, problem.Show.Count);

        for (var i = 0; i < 2; i++)
        {
            var start = new Thing { X = -64 + 128 * i, Y = 64, Type = 11 };
            foreach (var s in new[] { "skill1", "skill2", "skill3", "skill4", "skill5" }) start.SetFlag(s, true);
            history.Record("Place", e => e.Add(start));
        }

        Assert.DoesNotContain(Check(map, Vanilla), p => p.Rule == "few-deathmatch-starts");
    }

    [Fact]
    public void AMiddleOfOverlappingPatchesIsMedusaForVanillaAndTheFixTakesItOff()
    {
        var textures = ProblemTests.Freedoom.Value.Textures;
        var stacked = textures.Walls.First(t => textures.StacksPatches(t.Name)).Name;
        var single = textures.Walls.First(t => !textures.StacksPatches(t.Name) && !textures.HasHoles(t.Name)).Name;
        var (map, history, _) = MovingTests.Drawn((0, 0, 128, 128), (128, 0, 256, 128));
        var shared = map.Lines.Single(l => l.OnMap && l.Back is not null);
        history.Record("Dress", e => e.Set(shared.Front, s => s.Middle, (s, v) => s.Middle = v, stacked));
        Assert.DoesNotContain(Check(map), p => p.Rule == "medusa");
        Assert.DoesNotContain(Check(map, new ClassicRules("doom", Xlat.Value, Targets.LimitRemoving)), p => p.Rule == "medusa");
        var problem = Assert.Single(Check(map, Vanilla), p => p.Rule == "medusa");
        Assert.Same(shared, Assert.Single(problem.Show));

        history.Record(problem.FixLabel!, problem.Fix!);
        Assert.Equal("-", shared.Front.Middle);
        history.Record("Dress", e => e.Set(shared.Front, s => s.Middle, (s, v) => s.Middle = v, single));
        Assert.DoesNotContain(Check(map, Vanilla), p => p.Rule == "medusa");
    }

    [Fact]
    public void AShortOrSeeThroughPictureOnASolidWallIsTuttiFruttiForVanilla()
    {
        var textures = ProblemTests.Freedoom.Value.Textures;
        var tall = textures.Walls.First(t => t.Height >= 128 && !textures.HasHoles(t.Name)).Name;
        var shortOne = textures.Walls.First(t => t.Height is > 0 and < 128 && !textures.HasHoles(t.Name)).Name;
        var holed = textures.Walls.First(t => textures.HasHoles(t.Name)).Name;

        // A room 128 tall: a tall picture is fine, a short one shows more than it has, a see-through one shows holes.
        var (map, history, _) = MovingTests.Drawn((0, 0, 256, 256));
        var wall = map.Lines.First(l => l.OnMap);
        IReadOnlyList<Problem> With(string picture)
        {
            history.Record("Dress", e => e.Set(wall.Front, s => s.Middle, (s, v) => s.Middle = v, picture));
            foreach (var other in map.Lines.Where(l => l.OnMap && l != wall)) history.Record("Dress", e => e.Set(other.Front, s => s.Middle, (s, v) => s.Middle = v, tall));
            return Check(map, Vanilla);
        }

        Assert.DoesNotContain(With(tall), p => p.Rule == "tutti-frutti");
        Assert.Same(wall, Assert.Single(Assert.Single(With(shortOne), p => p.Rule == "tutti-frutti").Show));
        Assert.Same(wall, Assert.Single(Assert.Single(With(holed), p => p.Rule == "tutti-frutti").Show));
        Assert.DoesNotContain(Check(map), p => p.Rule == "tutti-frutti");
    }
}
