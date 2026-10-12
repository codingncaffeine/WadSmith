using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using WadSmith.App.Audio;
using WadSmith.Core.Audio;

namespace WadSmith.Tests;

/// <summary>
/// Sounds on the shelf (D33): the sound table names each object's lump, the game holds it, and the player turns it into
/// samples. Nothing here makes a sound: the player's samples are only computed, and the window test plays into a recorder.
/// </summary>
public sealed class ShelfSoundTests
{
    [Fact]
    public void TheSoundTableFollowsRandomGroupsAliasesAndLaterFiles()
    {
        var info = SoundInfo.Parse(
        [
            """
            // Imp
            $random imp/sight { imp/sight1
                imp/sight2 }
            imp/sight1   dsbgsit1   // the first
            imp/sight2   dsbgsit2
            $limit imp/sight 4
            /* misc/gone dsgone */
            misc/i_pkup  dsitemup
            $alias misc/k_pkup misc/i_pkup
            """,
            "misc/i_pkup dsnewup",
        ]);
        Assert.Equal("dsbgsit1", info.LumpFor("imp/sight"));
        Assert.Equal("dsbgsit2", info.LumpFor("IMP/SIGHT2"));
        Assert.Equal("dsnewup", info.LumpFor("misc/k_pkup"));
        Assert.Null(info.LumpFor("misc/gone"));
        Assert.Null(info.LumpFor("nothing/here"));
    }

    [Fact]
    public void ObjectsKnowTheirSoundsFromTheEngineAndTheGameHoldsThem()
    {
        var freedoom = ProblemTests.Freedoom.Value;
        Assert.Equal("dsbgsit1", freedoom.Things.Find(3001)!.Sound, ignoreCase: true);
        Assert.Equal("dswpnup", freedoom.Things.Find(2001)!.Sound, ignoreCase: true);
        Assert.Equal("dsitemup", freedoom.Things.Find(2012)!.Sound, ignoreCase: true);
        Assert.Equal("dsitemup", freedoom.Things.Find(13)!.Sound, ignoreCase: true);
        Assert.Null(freedoom.Things.Find(1)!.Sound);
        var wav = freedoom.SoundFor(3001)!;
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Null(freedoom.SoundFor(1));
    }

    [Fact]
    public void TheDeviceGetsTheSoundAtItsRateInMonoAtHalfVolume()
    {
        // 11025 Hz, 8-bit: full scale up, silence, full scale down.
        var wav = AudioFormats.Wav([255, 128, 0], 11025, 1, 8);
        var samples = SdlSoundPlayer.Samples(wav);
        Assert.Equal(12, samples.Length);
        Assert.InRange(samples[0], 16000, 16384);
        Assert.Equal(0, samples[4]);
        Assert.InRange(samples[8], -16384, -16000);
        // 16-bit stereo at the device's own rate: the channels are averaged, nothing is stretched.
        var stereo = AudioFormats.Wav([0x00, 0x40, 0x00, 0xC0, 0x00, 0x40, 0x00, 0x40], 44100, 2, 16);
        Assert.Equal([0, 8191], SdlSoundPlayer.Samples(stereo));
        Assert.Throws<FormatException>(() => SdlSoundPlayer.Samples("not a sound"u8));
    }
}

/// <summary>Plays nothing; remembers what it was asked to play.</summary>
internal sealed class RecordingPlayer : ISoundPlayer
{
    public List<byte[]> Played { get; } = [];

    public void Play(byte[] wav)
    {
        lock (Played) Played.Add(wav);
    }

    public void Dispose()
    {
    }
}

/// <summary>Pointing at a monster on the Objects shelf plays its sound once, and the preference stops it.</summary>
[Collection(TimingCollection.Name)]
public sealed class ShelfSoundInTheWindowTests
{
    [Fact]
    public async Task PointingAtAMonsterPlaysItsSightSoundUntilThePreferenceIsOff()
    {
        var wad = GameFiles.Require("freedoom2.wad");
        using var temp = new TempFolder();
        var log = await Ui.Run(() =>
        {
            var (window, vm) = Ui.Start(temp.Path, Path.GetDirectoryName(wad)!);
            var recorder = new RecordingPlayer();
            App.App.Services!.Sound = recorder;
            var said = new List<string>();
            try
            {
                var editor = Ui.NewMap(vm);
                editor.ShowObjects = true;
                editor.ObjectShelf.Search = editor.Session.Resources!.Things.Find(3001)!.Name;
                Ui.Pump(1.0);
                Border Tile(int type) => window.GetVisualDescendants().OfType<Border>().First(b => b.DataContext is App.ViewModels.ShelfTile t && t.Drop.Thing == type && b.IsVisible);
                void Over(Visual v)
                {
                    var p = v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(p, RawInputModifiers.None);
                    for (var i = 0; i < 25; i++) Ui.Pump(0.02);
                }

                void Away()
                {
                    window.MouseMove(new Point(5, 400), RawInputModifiers.None);
                    Ui.Pump(0.1);
                }

                string Heard() => $"{recorder.Played.Count} {(recorder.Played.LastOrDefault() is { } w ? System.Text.Encoding.ASCII.GetString(w, 0, 4) : "-")}";
                said.Add(Heard());
                Over(Tile(3001));
                said.Add(Heard());
                Away();
                App.App.Services.Settings.Current.ShelfSounds = false;
                Over(Tile(3001));
                said.Add(Heard());
            }
            finally
            {
                App.App.Services.Settings.Current.ShelfSounds = true;
                window.Close();
            }

            return said;
        });
        Assert.Equal(["0 -", "1 RIFF", "1 RIFF"], log);
    }
}
