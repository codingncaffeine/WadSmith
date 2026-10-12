using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace WadSmith.Tests;

/// <summary>
/// The first-Play card (D33) through the real window, with stand-in engines (scripts that exit at once; no game starts):
/// it comes the first time a map comes back from the game without an error, once per profile, and not when celebrations
/// are off.
/// </summary>
[Collection(TimingCollection.Name)]
public sealed class FirstPlayInTheWindowTests
{
    private static string StandIn(string folder, string name, int exit)
    {
        if (OperatingSystem.IsWindows())
        {
            var batch = Path.Combine(folder, name + ".cmd");
            File.WriteAllText(batch, $"@exit /b {exit}\r\n");
            return batch;
        }

        var path = Path.Combine(folder, name + ".sh");
        File.WriteAllText(path, $"#!/bin/sh\nexit {exit}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task TheCardComesOnceAfterAPlayThatEndedWell()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var (ok, failed) = (StandIn(temp.Path, "ends-well", 0), StandIn(temp.Path, "fails", 3));
        var seen = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(Path.Combine(temp.Path, "profile"), Path.GetDirectoryName(wad)!);
            var settings = App.App.Services!.Settings.Current;
            var said = new List<string>();
            try
            {
                var editor = Ui.NewMap(vm);
                said.Add(Core.Games.GameCatalog.Get(editor.Session.Project.Info.Game)!.Title);
                void Play(string engine)
                {
                    settings.EnginePath = engine;
                    Ui.Wait(editor.PlayAnywayCommand.ExecuteAsync(null));
                    Ui.Pump(0.5);
                    said.Add(editor.Celebration?.Heading ?? "-");
                }

                Play(failed);
                Play(ok);
                said.Add(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == editor.Celebration?.Heading) ? "shown" : "not shown");

                // Carry on, clicked.
                var carry = window.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content as string == "Carry on");
                var at = carry.TranslatePoint(new Point(carry.Bounds.Width / 2, carry.Bounds.Height / 2), window)!.Value;
                window.MouseMove(at, RawInputModifiers.None);
                window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
                Ui.Pump(0.3);
                said.Add(editor.Celebration?.Heading ?? "-");

                Play(ok);
                settings.FirstPlayCelebrated = false;
                settings.Celebrations = false;
                Play(ok);
            }
            finally
            {
                settings.EnginePath = null;
                settings.Celebrations = true;
                window.Close();
            }

            return said;
        });
        Assert.StartsWith("Freedoom", seen[0]);
        Assert.Equal(["-", $"You made a {seen[0]} map.", "shown", "-", "-", "-"], seen.Skip(1));
    }
}
