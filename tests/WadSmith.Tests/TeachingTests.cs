using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.Editor.App;

namespace WadSmith.Tests;

/// <summary>The teaching layer's switches (D34): tooltips at a length, the Guide's invitation, the one question.</summary>
public sealed class TipTextTests
{
    [Theory]
    [InlineData("Turn around the map like a model on a table.", "Turn around the map like a model on a table.")]
    [InlineData("Start from one of this game's own maps. Your game files are never changed.", "Start from one of this game's own maps.")]
    [InlineData("How high the floor is (heightfloor). The player steps up 24 at most.", "How high the floor is (heightfloor).")]
    [InlineData("A ramp climbs 1.5 a unit. It works.", "A ramp climbs 1.5 a unit.")]
    [InlineData("Move freely: drag to look, WASD to move", "Move freely: drag to look, WASD to move")]
    [InlineData("Is it lit? Yes, by the room.", "Is it lit?")]
    public void BriefIsTheFirstSentence(string full, string brief) => Assert.Equal(brief, TipText.Brief(full));

    [Fact]
    public void OffShowsNothingAndFullShowsAll()
    {
        Assert.Null(TipText.At(TooltipLength.Off, "One. Two."));
        Assert.Equal("One. Two.", TipText.At(TooltipLength.Full, "One. Two."));
        Assert.Equal("One.", TipText.At(TooltipLength.Brief, "One. Two."));
    }
}

/// <summary>Through the real window: the switches reach what they switch, and the editor's commands stay the same.</summary>
[Collection(TimingCollection.Name)]
public sealed class TeachingInTheWindowTests
{
    private static void Click(Window window, Control c)
    {
        var at = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window)!.Value;
        window.MouseMove(at, RawInputModifiers.None);
        window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        Ui.Pump(0.3);
    }

    // A button by its words, or by its name when it shows more than words (the start tiles).
    private static Button Shown(Window window, string content) => window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && (b.Content as string == content || Avalonia.Automation.AutomationProperties.GetName(b) == content))!;

    [Fact]
    public async Task TooltipsOpenFullBriefOrNotAtAll()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, _) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            var settings = App.App.Services!.Settings.Current;
            var said = new List<string>();
            try
            {
                var remix = Shown(window, "Remix a map");
                string Open(TooltipLength length)
                {
                    settings.Tooltips = length;
                    ToolTip.SetIsOpen(remix, true);
                    Ui.Pump(0.2);
                    var open = ToolTip.GetIsOpen(remix);
                    var tip = ToolTip.GetTip(remix) as string;
                    ToolTip.SetIsOpen(remix, false);
                    Ui.Pump(0.1);
                    return open ? tip ?? "?" : "closed";
                }

                said.Add(Open(TooltipLength.Brief));
                said.Add(Open(TooltipLength.Off));
                said.Add(Open(TooltipLength.Full));
            }
            finally
            {
                settings.Tooltips = TooltipLength.Full;
                window.Close();
            }

            return said;
        });
        Assert.Equal(["Start from one of this game's own maps.", "closed",
            "Start from one of this game's own maps. Your game files are never changed; your changes become a project of your own."], seen);
    }

    [Fact]
    public async Task YesToTheQuestionTurnsTheTeachingDownAndEveryCommandStays()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            var settings = App.App.Services!.Settings.Current;
            var said = new List<string>();
            try
            {
                string State() => $"asked {settings.AskedAboutExperience}, guided button {Shown(window, "Guided first map") is not null}, question {Shown(window, "Yes, I have") is not null}";
                said.Add(State());

                // The editor's commands and keys with the teaching on.
                var editor = Ui.NewMap(vm);
                var before = editor.Commands().Select(c => $"{c.Name}={c.Shortcut}").ToList();
                Ui.Wait(editor.CloseCommand.ExecuteAsync(null));
                Ui.Pump(0.5);

                Click(window, Shown(window, "Yes, I have"));
                said.Add(State());
                said.Add($"coach {settings.CoachTips}, prompts {settings.GuidePrompts}, tooltips {settings.Tooltips}, classic {settings.ClassicNames}");

                editor = Ui.NewMap(vm);
                var after = editor.Commands().Select(c => $"{c.Name}={c.Shortcut}").ToList();
                said.Add($"{before.Count} commands, {before.Except(after).Count()} lost");
                said.Add(editor.GuideMenu.Any(g => g.Title == "Guided first map") ? "guided map in the Guide menu" : "guided map gone");
            }
            finally
            {
                App.App.Services.Words.Classic = false;
                window.Close();
            }

            return said;
        });
        Assert.Equal([
            "asked False, guided button True, question True",
            "asked True, guided button False, question False",
            "coach False, prompts False, tooltips Brief, classic True",
            seen[3],
            "guided map in the Guide menu",
        ], seen);
        Assert.EndsWith(" commands, 0 lost", seen[3]);
    }
}
