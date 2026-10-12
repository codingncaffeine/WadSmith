using WadSmith.App.ViewModels;
using WadSmith.Editor.App;

namespace WadSmith.Tests;

/// <summary>The command palette finds commands by a few letters, by their other names too, and runs the one chosen.</summary>
public sealed class PaletteTests
{
    [Fact]
    public void LettersMatchInOrderAndTheBetterMatchScoresHigher()
    {
        Assert.Null(Fuzzy.Score("rod", "Door tool"));
        Assert.Null(Fuzzy.Score("xyz", "Play"));
        Assert.True(Fuzzy.Score("play", "Play") > Fuzzy.Score("play", "Play from here"));
        Assert.True(Fuzzy.Score("door", "Door tool") > Fuzzy.Score("door", "Make a door"));
        Assert.True(Fuzzy.Score("dt", "Door tool") > Fuzzy.Score("dt", "Duplicate the room"));
        Assert.True(Fuzzy.Score("fh", "Play from here") > Fuzzy.Score("fh", "Mirror the selection left–right"));
        Assert.Null(Fuzzy.Score("fh", "Show heights as colours"));
        // A word start further on is taken only when the rest of the letters still fit after it.
        Assert.NotNull(Fuzzy.Score("ac", "xabc a"));
        Assert.Equal(0, Fuzzy.Score("  ", "Anything"));
    }

    private static (CommandPaletteViewModel Palette, List<string> Ran, List<string> Recent, Func<bool> Closed) Open(params EditorCommand[] extra)
    {
        var ran = new List<string>();
        var closed = false;
        var all = new List<EditorCommand>
        {
            new("Select tool", "Tools", "V", "pointer", () => ran.Add("select")),
            new("Room tool", "Tools", "R", "draw a room|sector", () => ran.Add("room")),
            new("Door tool", "Tools", "D", "make a door", () => ran.Add("door")),
            new("Paste", "Edit", "Ctrl+V", "", () => ran.Add("paste"), () => false),
            new("Play", "Play", "F9", "test|run", () => ran.Add("play")),
            new("Show only the walls", "View", "", "lines|linedefs", () => ran.Add("lines")),
        };
        all.AddRange(extra);
        var recent = new List<string>();
        return (new CommandPaletteViewModel(all, recent, () => closed = true), ran, recent, () => closed);
    }

    [Fact]
    public void ClassicNamesAndOtherWordsFindTheCommand()
    {
        var (palette, _, _, _) = Open();
        palette.Query = "sector";
        Assert.Equal("Room tool", palette.Results[0].Name);
        palette.Query = "linedef";
        Assert.Equal("Show only the walls", palette.Results[0].Name);
        palette.Query = "test";
        Assert.Equal("Play", palette.Results[0].Name);
        palette.Query = "tools";
        Assert.Equal(["Select tool", "Room tool", "Door tool"], palette.Results.Select(c => c.Name));
        palette.Query = "sector";
        Assert.Single(palette.Results);
        palette.Query = "qqq";
        Assert.True(palette.NothingFound);
        Assert.Equal(-1, palette.SelectedIndex);
    }

    [Fact]
    public void WhatCannotBeDoneNowIsNotListed()
    {
        var (palette, _, _, _) = Open();
        Assert.DoesNotContain(palette.Results, c => c.Name == "Paste");
        palette.Query = "paste";
        Assert.True(palette.NothingFound);
    }

    [Fact]
    public void EnterRunsTheHighlightedCommandClosesAndRemembersIt()
    {
        var (palette, ran, recent, closed) = Open();
        palette.Query = "oo";
        Assert.Equal("Room tool", palette.Results[0].Name);
        palette.Move(1);
        Assert.Equal("Door tool", palette.Results[palette.SelectedIndex].Name);
        palette.Move(50);
        Assert.Equal(palette.Results.Count - 1, palette.SelectedIndex);
        palette.Move(-50);
        Assert.Equal(0, palette.SelectedIndex);
        palette.Move(1);
        palette.RunSelectedCommand.Execute(null);
        Assert.Equal(["door"], ran);
        Assert.True(closed());
        Assert.Equal(["Door tool"], recent);

        // Opened again with nothing typed, the last command run comes first.
        var again = new CommandPaletteViewModel([.. new[] { "Select tool", "Door tool" }.Select(n => new EditorCommand(n, "Tools", "", "", () => { }))], recent, () => { });
        Assert.Equal("Door tool", again.Results[0].Name);
    }
}
