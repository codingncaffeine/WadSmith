using System.Diagnostics;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Core;
using WadSmith.Core.Engines;
using WadSmith.Core.Exporting;
using WadSmith.Core.Projects;
using WadSmith.Editor.App;

namespace WadSmith.App.ViewModels;

/// <summary>One entry of Play's "Play in" menu: the engine or port, ticked when chosen.</summary>
internal sealed record PlayInChoice(string Header, ICommand Choose, string Tip);

/// <summary>
/// Play in a classic port (Phase 12): the Play arrow's "Play in" chooses GZDoom or an installed port; a port plays the map
/// exported for it (the project's own classic target when the port plays that), with WadSmith's own copy of its settings.
/// </summary>
internal sealed partial class EditorViewModel
{
    /// <summary>Play in GZDoom (the engine found first).</summary>
    private const string InGZDoom = "gzdoom";

    /// <summary>Where Play plays: <see cref="InGZDoom"/>, or a classic port's program.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayInMenu))]
    private string _playIn = InGZDoom;

    /// <summary>The classic ports installed for this game family, found on a worker when the editor opens.</summary>
    private IReadOnlyList<PortInstall> _ports = [];

    /// <summary>The "Play in" menu: GZDoom, then each port installed for this game, the chosen one ticked.</summary>
    public IReadOnlyList<PlayInChoice> PlayInMenu =>
    [
        new((PlayIn == InGZDoom ? "✓ " : "    ") + "GZDoom", new RelayCommand(() => PlayIn = InGZDoom), "Plays the project as it is, in GZDoom."),
        .. _ports.Select(p => new PlayInChoice((PlayIn == p.Program ? "✓ " : "    ") + p.Name, new RelayCommand(() => PlayIn = p.Program),
            $"Plays the map exported for {ClassicPorts.Get(p.ExportFor(Session.Project.Info.Target))?.Title}, with WadSmith's own copy of {p.Name}'s settings.")),
    ];

    /// <summary>Finds the ports off the UI thread; a project kept for a classic target plays in that target's port first.</summary>
    private void FindPorts()
    {
        var family = Session.Project.ClassicNamespace;
        var target = Session.Project.Info.Target;
        _ = Task.Run(() => PortLocator.Find(family, Environment.GetEnvironmentVariable)).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            Session.OnUiThread(() =>
            {
                _ports = t.Result;
                var own = Targets.IsClassic(target) ? _ports.FirstOrDefault(p => p.Exports[0] == target) : null;
                if (own is not null) PlayIn = own.Program;
                OnPropertyChanged(nameof(PlayInMenu));
            });
        }, TaskScheduler.Default);
    }

    /// <summary>The port Play plays in now, or null for GZDoom.</summary>
    private PortInstall? PlayingPort => _ports.FirstOrDefault(p => p.Program == PlayIn);

    /// <summary>
    /// Plays the map in <paramref name="port"/>: the project exported for it into the cache, then the port started with the
    /// base game, the game's WADs from <paramref name="request"/>, the export, the map and the difficulty. A map the port
    /// would stop with an error over is not started: the user is told what, and Export lists it.
    /// </summary>
    private async Task PlayInPortAsync(PortInstall port, PlayRequest request)
    {
        var exportId = port.ExportFor(Session.Project.Info.Target);
        var title = ClassicPorts.Get(exportId)?.Title ?? exportId;
        Hint($"Exporting {Session.Slot} for {port.Name} ({title})…");
        PreparedExport prepared;
        try
        {
            prepared = await Exports.PrepareAsync(Session, exportId, Path.Combine(_services.Paths.Cache, "play")).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException or ArgumentException)
        {
            Log.Warn($"Exporting for {port.Name} failed: {ex.Message}");
            Hint($"The map could not be exported for {port.Name}: {ex.Message}");
            return;
        }

        if (prepared.Report.Losses.FirstOrDefault(l => l.Breaks) is { } stops)
        {
            Hint($"{port.Name} would stop with an error over this map: {stops.Title}{(stops.Map is null ? "" : $" in {stops.Map}")}. Export (Ctrl+E) lists everything to change for {title}.");
            return;
        }

        await Session.SaveVersion("Played").ConfigureAwait(true);
        var played = Session.Map.Frame;
        var family = Session.Project.ClassicNamespace;
        var data = _services.Paths.Data;
        var settings = await Task.Run(() => PortPlay.Settings(port, data, Environment.GetEnvironmentVariable)).ConfigureAwait(true);
        var wads = request.Files.Take(request.Files.Count - 1).Where(f => f.EndsWith(".wad", StringComparison.OrdinalIgnoreCase)).ToList();
        var (program, arguments) = PortPlay.For(port, exportId, family, request.Iwad, wads, prepared.File, Session.Slot, Skill + 1, settings);
        var lost = prepared.Report.Losses.Count;
        int exitCode;
        string said;
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in arguments) start.ArgumentList.Add(a);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("the port did not start");
            Playing = true;
            Guide?.Happened(GuideEvents.Played);
            Hint($"Playing {Session.Slot} in {port.Name} ({title})…{(lost == 0 ? "" : $" {lost} {(lost == 1 ? "thing does" : "things do")} not survive there; Export (Ctrl+E) lists {(lost == 1 ? "it" : "them")}.")}");
            var output = process.StandardOutput.ReadToEndAsync(_services.ShuttingDown);
            var errors = process.StandardError.ReadToEndAsync(_services.ShuttingDown);
            await process.WaitForExitAsync(_services.ShuttingDown).ConfigureAwait(true);
            exitCode = process.ExitCode;
            said = (await output.ConfigureAwait(true)) + (await errors.ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Starting {program} failed: {ex.Message}");
            Hint($"{port.Name} could not be started: {ex.Message}");
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            Playing = false;
        }

        var log = Path.Combine(data, "engine", "last-play-" + Path.GetFileName(program) + ".log");
        await Task.Run(() => File.WriteAllText(log, said)).ConfigureAwait(true);
        if (exitCode != 0)
        {
            var last = said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            Hint($"{port.Name} stopped with an error{(last.Length == 0 ? "" : ": " + last)}");
            return;
        }

        Hint("Back from the game.");
        await CelebrateFirstPlay(played, port.Name).ConfigureAwait(true);
    }
}
