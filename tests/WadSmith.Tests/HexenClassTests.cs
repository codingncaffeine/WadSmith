using System.Collections.Concurrent;
using WadSmith.App.ViewModels;
using WadSmith.Core.Maps;
using WadSmith.Core.Projects;
using WadSmith.Editor;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>Hexen's class flags (Phase 9): an object's Inspector says which of the three heroes it is in the map for, and changes it.</summary>
public sealed class HexenClassTests
{
    private static async Task<(TempFolder Temp, EditorSession Session, InspectorViewModel Inspector, Thing Thing, Func<Task> Settle)> Open(string game)
    {
        var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", game);
        var model = StarterRoom.Create("MAP01", game == "hexen" ? "hexen" : "doom");
        var thing = new MapThing { X = 32, Y = 32, Type = 2001 };
        foreach (var f in WadSmith.Editor.Smart.Things.Everywhere(game == "hexen" ? "hexen" : "doom")) thing.SetFlag(f, true);
        model.Things.Add(thing);
        project.SaveMap(model);
        var ui = new ConcurrentQueue<Action>();
        var session = EditorSession.Open(project, "MAP01", ui.Enqueue);
        async Task Settle()
        {
            var until = DateTime.UtcNow.AddSeconds(60);
            do
            {
                while (ui.TryDequeue(out var a)) a();
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
            while ((session.Geometry.Version != session.Map.Version || !ui.IsEmpty) && DateTime.UtcNow < until);
        }

        await Settle();
        var placed = session.Map.Things.Single(t => t.OnMap && t.Type == 2001);
        var inspector = new InspectorViewModel(session);
        session.Selection.Set(placed);
        await Settle();
        return (temp, session, inspector, placed, Settle);
    }

    [Fact]
    public async Task AHexenObjectSaysAndChangesWhichHeroesItIsFor()
    {
        var (temp, session, inspector, thing, settle) = await Open("hexen");
        using var _ = temp;
        using var __ = session;
        var rows = inspector.Rows.OfType<FlagRow>().Where(r => r.Label.StartsWith("For the ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["For the Fighter", "For the Cleric", "For the Mage"], rows.Select(r => r.Label));
        Assert.All(rows, r => Assert.True(r.Value));
        rows[2].Value = false;
        await settle();
        Assert.Equal((true, true, false), (thing.Flag("class1"), thing.Flag("class2"), thing.Flag("class3")));
        Assert.Equal("Hide from the Mage", session.History.UndoLabel);
        session.History.Undo();
        Assert.True(thing.Flag("class3"));
    }

    [Fact]
    public async Task ADoomObjectHasNoHeroes()
    {
        var (temp, session, inspector, _, _) = await Open("doom2");
        using var _ = temp;
        using var __ = session;
        Assert.DoesNotContain(inspector.Rows, r => r.Label.StartsWith("For the ", StringComparison.Ordinal));
        Assert.Contains(inspector.Rows, r => r.Label == "In deathmatch");
    }
}
