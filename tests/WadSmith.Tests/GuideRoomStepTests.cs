using WadSmith.Core.Maps;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Tests;

/// <summary>The guided first map's room step is done when the new room shares a wall with the first; otherwise the card says what to do.</summary>
public sealed class GuideRoomStepTests
{
    private static MapFrame Map(string script)
    {
        var map = EditorMap.FromModel(new MapModel { Name = "MAP01", Namespace = "zdoom" });
        Assert.All(EditScript.Run(map, script, Look.For("doom"), "doom"), s => Assert.True(s.Done, $"line {s.Line}: {s.Said}"));
        return map.Frame;
    }

    private static GuideStep Step(string id) => GuideBook.Chapters.Single(c => c.Id == GuideBook.FirstMap).Steps.Single(s => s.Id == id);

    [Fact]
    public void ASecondRoomCountsOnlyWhenItSharesAWallAndTheCardSaysSoOtherwise()
    {
        var start = Map("room 0 0 256 256\nstart 128 128");
        var apart = Map("room 0 0 256 256\nstart 128 128\nroom 512 0 768 256");
        var against = Map("room 0 0 256 256\nstart 128 128\nroom 256 0 512 256");
        var none = new HashSet<string>();

        Assert.False(GuideBook.Done(Step("room"), apart, start, null, none));
        Assert.True(GuideBook.Done(Step("room"), against, start, null, none));
        Assert.StartsWith("Your new room does not touch the first one", GuideBook.Hint(Step("room"), apart, start));
        Assert.Null(GuideBook.Hint(Step("room"), against, start));
        Assert.Null(GuideBook.Hint(Step("room"), start, start));

        // The door step on rooms that share no wall says why a door cannot go in yet.
        Assert.StartsWith("Your rooms share no wall yet", GuideBook.Hint(Step("door"), apart, apart));
        Assert.Null(GuideBook.Hint(Step("door"), against, against));
    }
}
