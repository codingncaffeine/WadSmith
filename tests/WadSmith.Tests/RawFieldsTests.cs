using System.Collections.Concurrent;
using WadSmith.App.ViewModels;
using WadSmith.Core.Projects;
using WadSmith.Editor;

namespace WadSmith.Tests;

/// <summary>
/// The Inspector's raw fields (6c, Eureka's raw editing): an element's action and arguments by number and every UDMF field of
/// its own, shown when asked for, each change one undo step; a field written empty goes, a new one is added as name = value, and
/// what does not fit is refused in words.
/// </summary>
public sealed class RawFieldsTests
{
    [Fact]
    public async Task AWallsActionArgumentsAndFieldsAreSetByHand()
    {
        using var temp = new TempFolder();
        var project = Project.Create(Path.Combine(temp.Path, "p"), "p", "doom2");
        project.SaveMap(StarterRoom.Create("MAP01", "doom"));
        var ui = new ConcurrentQueue<Action>();
        using var session = EditorSession.Open(project, "MAP01", ui.Enqueue);
        var told = new List<string>();
        session.Notice += told.Add;
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
        var wall = session.Map.Lines.First(l => l.OnMap);
        var inspector = new InspectorViewModel(session);
        session.Selection.Set(wall);
        await Settle();
        T Row<T>(string label) where T : InspectorRow => inspector.Rows.OfType<T>().Single(r => r.Label == label);
        void Write(string label, string text)
        {
            var row = Row<TextRow>(label);
            row.Value = text;
            row.Commit();
        }

        // In words until asked for.
        Assert.DoesNotContain(inspector.Rows, r => r.Label == "Action number");
        Row<ActionRow>("Raw fields…").Command.Execute(null);
        Row<NumberRow>("Action number").Value = 80;
        Row<NumberRow>("Argument 2").Value = 16;
        Row<NumberRow>("Line id").Value = 7;
        Assert.Equal((80, 16, 7), (wall.Special, wall.Args[1], wall.LineId));

        // A field of one's own, added, read back in its row, then taken out by writing it empty.
        Write("Add a field", "user_note = keep this");
        await Settle();
        Assert.Equal("keep this", wall.Extra["user_note"].AsString());
        Assert.Equal("keep this", Row<TextRow>("user_note").Value);
        Write("user_note", "");
        await Settle();
        Assert.False(wall.Extra.Contains("user_note"));

        // A number keeps its kind; text that is not one, a name a row above sets, and a bad name are refused.
        Write("Add a field", "alpha = 0.5");
        await Settle();
        Assert.Equal(0.5, wall.Extra["alpha"].AsFloat());
        Write("alpha", "half");
        Write("Add a field", "special = 5");
        Write("Add a field", "no value");
        Assert.Equal(["alpha holds a number.", "special is set by its own row.", "Write the new field as name = value: a name of letters, digits and _, then its value."], told.TakeLast(3));
        Assert.Equal(0.5, wall.Extra["alpha"].AsFloat());

        // Each change is one undo step: the field, then the line id, go back in turn.
        Assert.Equal("Add alpha", session.History.UndoLabel);
        session.History.Undo();
        Assert.False(wall.Extra.Contains("alpha"));
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("12", "12")]
    [InlineData("0.25", "0.25")]
    [InlineData("\"words\"", "\"words\"")]
    [InlineData("plain words", "\"plain words\"")]
    public void ANewFieldsValueReadsAsTheKindItLooksLike(string text, string udmf) =>
        Assert.Equal(udmf, InspectorViewModel.ParseRaw(text, null)!.Value.ToString());
}
