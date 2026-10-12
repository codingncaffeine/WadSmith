using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>
/// The Balance meter (D29): the engine's own health and ammunition numbers, and an honest sentence per difficulty, from
/// plenty to running dry part of the way through.
/// </summary>
public sealed class BalanceTests
{
    private static EditorMap Built(string script)
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, script, Look.For("doom"), "doom"), s => Assert.True(s.Done, $"line {s.Line}: {s.Said}"));
        return map;
    }

    private static IReadOnlyList<BalanceReading> Read(EditorMap map) => Balance.Read(map.Things, ProblemTests.Freedoom.Value.Things, "doom")!;

    [Fact]
    public void TheCatalogueCarriesTheEnginesHealthAndAmmunition()
    {
        var things = ProblemTests.Freedoom.Value.Things;
        Assert.Equal(60, things.Find(3001)!.Health);
        Assert.Equal((50, "Clip"), (things.Find(2048)!.Amount, things.Find(2048)!.AmmoType));
        Assert.Equal((8, "Shell"), (things.Find(2001)!.AmmoGive, things.Find(2001)!.AmmoType));
        Assert.Equal(25, things.Find(2012)!.Amount);
    }

    [Fact]
    public void TwentyFiveImpsAgainstAThousandDamageRunDryTwoThirdsIn()
    {
        // 25 imps: 1,500 health. The 50 bullets the player starts with and a box of 50: 1,000 damage.
        var imps = string.Join("\n", Enumerable.Range(0, 25).Select(i => $"thing 3001 {32 + i % 5 * 64} {32 + i / 5 * 64}"));
        var reading = Read(Built($"room 0 0 512 512\nstart 448 448\n{imps}\nthing 2048 448 320"));
        Assert.Equal("Normal: the player runs dry about two thirds of the way through the monsters; no health to pick up.", reading[1].Sentence);
        Assert.Equal(1000.0 / 1500, reading[1].Ratio, 3);
    }

    [Fact]
    public void PlentyJustEnoughAndNoMonstersAreSaid()
    {
        Assert.Equal("Normal: plenty of ammunition, about 8.3 times what the monsters can take; 25 health to pick up.", Read(Built("room 0 0 512 512\nstart 448 448\nthing 3001 64 64\nthing 2012 128 64"))[1].Sentence);
        // 8 imps (480) against the 500 starting bullets.
        var imps = string.Join("\n", Enumerable.Range(0, 8).Select(i => $"thing 3001 {32 + i * 48} 64"));
        Assert.StartsWith("Normal: just enough ammunition, about 1.0 times", Read(Built($"room 0 0 512 512\nstart 448 448\n{imps}"))[1].Sentence);
        Assert.Equal("Normal: no monsters to fight.", Read(Built("room 0 0 512 512\nstart 448 448"))[1].Sentence);
        Assert.Null(Balance.Read([], ProblemTests.Freedoom.Value.Things, "heretic"));
    }

    [Fact]
    public void ADifficultyCountsOnlyItsOwnMonsters()
    {
        var map = Built("room 0 0 512 512\nstart 448 448\nthing 3001 64 64\nthing 3003 128 128");
        new History(map).Record("Hard only", e =>
        {
            var baron = map.Things.Single(t => t.Type == 3003);
            foreach (var f in (string[])["skill1", "skill2", "skill3"]) e.Flag(baron, f, false);
        });
        var r = Read(map);
        Assert.Equal((1, 1, 2), (r[0].Monsters, r[1].Monsters, r[2].Monsters));
    }
}
