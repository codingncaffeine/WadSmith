using WadSmith.Core.Containers;
using WadSmith.Core.Games;
using WadSmith.Core.Maps;
using WadSmith.Core.Translation;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Geometry;

namespace WadSmith.Tests;

/// <summary>
/// Keys per game family: Heretic's green, blue and yellow keys and Hexen's named keys open their own locks in the
/// Progression walk and Problems, as Doom's do. The control is the games as shipped: no key and no exit of theirs is out of
/// reach when read as a remix reads them.
/// </summary>
public sealed class FamilyKeysTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("HERETIC.WAD", "heretic")]
    [InlineData("HEXEN.WAD", "hexen")]
    public void NoKeyOrExitOfTheShippedGamesIsOutOfReach(string fileName, string family)
    {
        var game = GameCatalog.Get(family)!;
        var engine = new ResourceStack([ResourceContainer.Open(GameFiles.Require("gzdoom.pk3"))]);
        var faults = new List<string>();
        var keys = 0;
        foreach (var lumps in MapLocator.Find(WadContainer.Open(GameFiles.Require(fileName))))
        {
            var model = MapConversion.Read(lumps, game.MapNamespace);
            var xlat = lumps.Format == MapFormat.Doom ? XlatTranslator.Load(engine, XlatTranslator.PathFor(model.Namespace)) : null;
            var map = Editor.Model.EditorMap.FromModel(MapTranslation.ToZDoom(model, xlat, []));
            var geometry = MapGeometry.Build(map.Frame);
            keys += map.Things.Count(t => t.OnMap && Editor.Smart.Keys.ColourOf(t.Type, family) is not null);
            faults.AddRange(Problems.Check(geometry, null, Look.For(family), family).Where(p => p.Rule.Contains("out-of-reach", StringComparison.Ordinal) || p.Rule.StartsWith("missing-key", StringComparison.Ordinal)).Select(p => $"{lumps.Name}: {p.Sentence}"));
        }

        output.WriteLine($"{fileName}: {keys} keys");
        Assert.True(keys > 5, $"only {keys} keys were recognised");
        Assert.Empty(faults);
    }
}
