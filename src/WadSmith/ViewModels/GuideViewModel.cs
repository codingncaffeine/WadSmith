using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Model;

namespace WadSmith.App.ViewModels;

/// <summary>A chapter of the Guide offered by name: in the Guide menu and on a finished chapter's card.</summary>
internal sealed record GuideLink(string Title, string Tip, ICommand Open);

/// <summary>
/// A Guide chapter while it runs (§3.9): the open step, what to do, the control it points at, and the checks that finish
/// each step, read from the map on a worker after every change. It never blocks the editor: the user can do anything
/// meanwhile, skip a step or stop.
/// </summary>
internal sealed partial class GuideViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private readonly Func<string, string> _keyed;
    private readonly Action _stopped;
    private readonly Func<IEnumerable<string>> _standing;
    private readonly (string Monster, string Weapon) _examples;
    private readonly LatestOnWorker<Reading> _checks;
    private readonly HashSet<string> _happened = [];
    private MapFrame? _atStep;

    // One check of the open step against the map.
    private sealed record Reading(EditorSession Session, int Step, bool Done);

    /// <summary>
    /// Runs <paramref name="chapter"/> on the session's map. <paramref name="standing"/> says what holds in the editor now, such as
    /// classic names switched on, which a step sees as having happened; <paramref name="open"/> opens a chapter by id for the
    /// finished card's links; <paramref name="seeOne"/> makes the link to the chapter's landmark in a real map (D28).
    /// </summary>
    public GuideViewModel(EditorSession session, GuideChapter chapter, Func<string, string> keyed, Func<IEnumerable<string>> standing, Action stopped, Action<string> open, Func<string, GuideLink?> seeOne)
    {
        _session = session;
        Chapter = chapter;
        _keyed = keyed;
        _standing = standing;
        _stopped = stopped;
        _examples = GuideBook.Examples(session.Engine);
        _checks = new(MainViewModel.PostToUi, Arrived, "the Guide's checks");
        var next = chapter.Id == GuideBook.FirstMap ? GuideBook.AfterFirstMap : GuideBook.After(chapter) is { } after ? [after] : [];
        Next = [.. next.Select(c => new GuideLink(chapter.Id == GuideBook.FirstMap ? c.Title : "Next: " + c.Title, c.Summary, new RelayCommand(() => open(c.Id))))];
        SeeOne = chapter.Landmark is { } kind ? seeOne(kind) : null;
        Begin(0);
    }

    public GuideChapter Chapter { get; }

    /// <summary>The chapter's feature in a real map ("See one in Doom II", D28); null when the chapter has none or no game here shows one.</summary>
    public GuideLink? SeeOne { get; }

    /// <summary>The chapters the finished card offers.</summary>
    public IReadOnlyList<GuideLink> Next { get; }

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

    private bool IsFirstMap => Chapter.Id == GuideBook.FirstMap;

    public string StepLabel => Finished
        ? IsFirstMap ? "Your first map" : Chapter.Title
        : $"{(IsFirstMap ? "" : Chapter.Title + " · ")}Step {Index + 1} of {Chapter.Steps.Count}";

    /// <summary>The control the open step points at.</summary>
    public GuideTarget Target => Finished ? GuideTarget.None : Chapter.Steps[Index].Target;

    /// <summary>The map changed, or something happened: the open step's check runs again on a worker.</summary>
    public void MapChanged()
    {
        if (Finished) return;
        var session = _session;
        var step = Index;
        var frame = session.Geometry.Frame;
        // The map as the step began is the frame the first check after it sees.
        _atStep ??= frame;
        var start = _atStep;
        var happened = new HashSet<string>(_happened);
        var resources = session.Resources;
        var s = Chapter.Steps[step];
        _checks.Request(() => new Reading(session, step, GuideBook.Done(s, frame, start, resources, happened)));
    }

    private void Arrived(Reading r)
    {
        if (!ReferenceEquals(r.Session, _session) || r.Step != Index || Finished) return;
        if (r.Done) Advance();
    }

    /// <summary>Something a step may wait for happened in the editor (<see cref="GuideEvents"/>).</summary>
    public void Happened(string what)
    {
        if (Finished || !_happened.Add(what)) return;
        MapChanged();
    }

    /// <summary>Goes on to the next step without doing this one.</summary>
    [RelayCommand]
    private void Skip() => Advance();

    /// <summary>Ends the guide; the map stays as it is.</summary>
    [RelayCommand]
    private void Stop() => _stopped();

    private void Advance()
    {
        if (Index + 1 >= Chapter.Steps.Count)
        {
            Finished = true;
            Title = IsFirstMap ? $"You made a {GameName()} map." : "Done.";
            Text = _keyed(Chapter.TryIt);
            return;
        }

        Begin(Index + 1);
    }

    private void Begin(int step)
    {
        Index = step;
        Title = Chapter.Steps[step].Title;
        Text = Written(Chapter.Steps[step]);
        _atStep = null;
        _happened.Clear();
        _happened.UnionWith(_standing());
        MapChanged();
    }

    /// <summary>The keys or the names changed in Preferences: the open step's text is written again.</summary>
    public void Rewrite() => Text = Finished ? _keyed(Chapter.TryIt) : Written(Chapter.Steps[Index]);

    private string Written(GuideStep s) =>
        _keyed(s.Text.Replace("{monster}", _examples.Monster, StringComparison.Ordinal).Replace("{weapon}", _examples.Weapon, StringComparison.Ordinal));

    private string GameName() => _session.Engine switch
    {
        "heretic" => "Heretic",
        "hexen" => "Hexen",
        "strife" => "Strife",
        "chex" => "Chex Quest",
        _ => "Doom",
    };
}
