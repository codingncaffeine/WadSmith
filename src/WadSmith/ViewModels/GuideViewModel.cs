using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Model;

namespace WadSmith.App.ViewModels;

/// <summary>
/// The guided first map while it runs (§3.9): the open step, what to do, the control it points at, and the checks that
/// finish each step, read from the map on a worker after every change. It never blocks the editor: the user can do
/// anything meanwhile, skip a step or stop.
/// </summary>
internal sealed partial class GuideViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private readonly Func<string, string> _keyed;
    private readonly Action _stopped;
    private readonly (string Monster, string Weapon) _examples;
    private readonly LatestOnWorker<Reading> _facts;
    private IReadOnlyDictionary<Side, (string Top, string Middle, string Bottom)> _wallsAtStep = new Dictionary<Side, (string, string, string)>();
    private GuideFacts? _atStep;
    private bool _saw3D;
    private bool _played;

    // One reading of the map for a step; the walls come with the first reading after the step begins.
    private sealed record Reading(EditorSession Session, int Step, GuideFacts Facts, IReadOnlyDictionary<Side, (string Top, string Middle, string Bottom)>? Walls);

    public GuideViewModel(EditorSession session, Func<string, string> keyed, Action stopped)
    {
        _session = session;
        _keyed = keyed;
        _stopped = stopped;
        _examples = GuidedMap.Examples(session.Engine);
        _facts = new(MainViewModel.PostToUi, Arrived, "the guided map's checks");
        Begin(0);
    }

    /// <summary>The open step's number, from 0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepLabel), nameof(Target))]
    private int _index;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _text = "";

    /// <summary>Whether every step is done: the card says so and offers what to try next.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Target), nameof(StepLabel))]
    private bool _finished;

    public string StepLabel => Finished ? "Your first map" : $"Step {Index + 1} of {GuidedMap.Steps.Count}";

    /// <summary>The control the open step points at.</summary>
    public GuideTarget Target => Finished ? GuideTarget.None : GuidedMap.Steps[Index].Target;

    /// <summary>The map changed: the open step's check runs again on a worker.</summary>
    public void MapChanged()
    {
        if (Finished) return;
        var session = _session;
        var step = Index;
        var frame = session.Geometry.Frame;
        var walls = _wallsAtStep;
        var baseline = _atStep is null;
        var resources = session.Resources;
        _facts.Request(() =>
        {
            var before = baseline ? GuidedMap.Walls(frame) : walls;
            return new Reading(session, step, GuidedMap.Facts(frame, resources, before), baseline ? before : null);
        });
    }

    private void Arrived(Reading r)
    {
        if (!ReferenceEquals(r.Session, _session) || r.Step != Index || Finished) return;
        if (_atStep is null)
        {
            // The first reading after a step begins is what the step compares with.
            if (r.Walls is null) return;
            _wallsAtStep = r.Walls;
            _atStep = r.Facts;
        }

        if (GuidedMap.Done(GuidedMap.Steps[Index], r.Facts, _atStep, _saw3D, _played)) Next();
    }

    /// <summary>The 3D view filled the window (the "See it in 3D" step).</summary>
    public void Saw3D()
    {
        _saw3D = true;
        MapChanged();
    }

    /// <summary>Play started the engine (the last step).</summary>
    public void Played()
    {
        _played = true;
        MapChanged();
    }

    /// <summary>Goes on to the next step without doing this one.</summary>
    [RelayCommand]
    private void Skip() => Next();

    /// <summary>Ends the guide; the map stays as it is.</summary>
    [RelayCommand]
    private void Stop() => _stopped();

    private void Next()
    {
        if (Index + 1 >= GuidedMap.Steps.Count)
        {
            Finished = true;
            Title = $"You made a {GameName()} map.";
            Text = _keyed("Everything in it can be changed, and nothing needs saving. Try next: select a room and make it a lift, draw stairs with the Stairs tool{or:Stairs tool}, lock a door with a key, or make a room a secret. Ctrl+K finds anything else.");
            return;
        }

        Begin(Index + 1);
    }

    private void Begin(int step)
    {
        Index = step;
        var s = GuidedMap.Steps[step];
        Title = s.Title;
        Text = _keyed(s.Text.Replace("{monster}", _examples.Monster, StringComparison.Ordinal).Replace("{weapon}", _examples.Weapon, StringComparison.Ordinal));
        // What the map holds as the step begins is read on a worker and becomes what the step compares with.
        _atStep = null;
        _saw3D = false;
        _played = false;
        MapChanged();
    }

    /// <summary>The keys or the names changed in Preferences: the open step's text is written again.</summary>
    public void Rewrite()
    {
        if (Finished) return;
        var s = GuidedMap.Steps[Index];
        Text = _keyed(s.Text.Replace("{monster}", _examples.Monster, StringComparison.Ordinal).Replace("{weapon}", _examples.Weapon, StringComparison.Ordinal));
    }

    private string GameName() => _session.Engine switch
    {
        "heretic" => "Heretic",
        "hexen" => "Hexen",
        "strife" => "Strife",
        "chex" => "Chex Quest",
        _ => "Doom",
    };
}
