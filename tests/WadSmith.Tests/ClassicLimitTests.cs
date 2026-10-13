using WadSmith.Core.Containers;
using WadSmith.Core.Projects;
using WadSmith.Core.Translation;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Geometry;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>The classic format's own limits as Problems, for a classic target only (6b.4).</summary>
public sealed class ClassicLimitTests
{
    [Fact]
    public void ANameLongerThanEightLettersIsAProblemForAClassicTargetOnly()
    {
        var xlat = XlatTranslator.Load(new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]), "xlat/doom.txt");
        var map = EditorMap.FromModel(StarterRoom.Create("MAP01", "doom"));
        var history = new History(map);
        history.Record("Long", e => e.Set(map.Lines[0].Front, s => s.Middle, (s, v) => s.Middle = v, "textures/mywall.png"));
        var geometry = MapGeometry.Build(map.Frame);
        Assert.DoesNotContain(Problems.Check(geometry, null, Look.For("doom"), "doom"), p => p.Rule == "long-names");
        var problem = Assert.Single(Problems.Check(geometry, null, Look.For("doom"), "doom", null, new ClassicRules("doom", xlat, Targets.Vanilla)), p => p.Rule == "long-names");
        Assert.Equal(Severity.Error, problem.Severity);
        Assert.Same(map.Lines[0], Assert.Single(problem.Show));
    }
}
