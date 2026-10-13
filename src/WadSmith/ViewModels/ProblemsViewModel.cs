using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Editor;
using WadSmith.Editor.Checks;
using WadSmith.Editor.Model;

namespace WadSmith.App.ViewModels;

/// <summary>One problem in the list: its sentence, how serious, and its Show and Fix buttons.</summary>
internal sealed class ProblemRow(Problem problem, Action<Problem> show, Action<Problem> fix)
{
    public string Sentence { get; } = problem.Sentence;

    public string Kind { get; } = problem.Severity == Severity.Error ? "Breaks" : "Check";

    public bool IsError { get; } = problem.Severity == Severity.Error;

    public bool CanShow { get; } = problem.Show.Count > 0;

    public bool CanFix { get; } = problem.Fix is not null;

    public string FixLabel { get; } = problem.FixLabel ?? "";

    public IRelayCommand ShowCommand { get; } = new RelayCommand(() => show(problem));

    public IRelayCommand FixCommand { get; } = new RelayCommand(() => fix(problem));
}

/// <summary>
/// The Problems panel (D20): the live checks, run on a worker after every change to the map, listed
/// with a sentence each, Show (select and bring into view) and a Fix where there is one.
/// </summary>
internal sealed partial class ProblemsViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private readonly LatestOnWorker<(EditorSession Session, long Version, IReadOnlyList<Problem> Found)> _checks;

    public ProblemsViewModel(EditorSession session)
    {
        _session = session;
        _checks = new(session.OnUiThread, r =>
        {
            if (!ReferenceEquals(r.Session, _session)) return;
            // What the engine said at the last Play, while still true, goes with the map's own checks.
            var known = new HashSet<WadSmith.Editor.Model.Element>(r.Found.Where(p => p.Rule == "unknown-texture").SelectMany(p => p.Show), ReferenceEqualityComparer.Instance);
            IReadOnlyList<Problem> all = [.. r.Found, .. EngineLog.Problems(EngineLog.Still(_session.LastPlay), known)];
            Rows.Clear();
            foreach (var p in all.OrderBy(p => p.Severity)) Rows.Add(new ProblemRow(p, ShowProblem, FixProblem));
            var errors = all.Count(p => p.Severity == Severity.Error);
            Summary = all.Count == 0 ? "No problems" : all.Count == 1 ? "1 problem" : $"{all.Count} problems";
            HasErrors = errors > 0;
            Empty = all.Count == 0;
        }, "the map's checks");
        session.GeometryChanged += Request;
        session.FitBaselineChanged += Request;
        session.LastPlayChanged += Request;
        Request();
    }

    public ObservableCollection<ProblemRow> Rows { get; } = [];

    [ObservableProperty]
    private string _summary = "Checking…";

    [ObservableProperty]
    private bool _hasErrors;

    [ObservableProperty]
    private bool _empty;

    /// <summary>Raised to bring elements into view on the plan.</summary>
    public event Action<IReadOnlyList<Element>>? ShowRequested;

    private void Request()
    {
        var session = _session;
        var geometry = session.Geometry;
        var resources = session.Resources;
        var look = session.Look;
        var engine = session.Engine;
        var fit = session.FitBaseline;
        var classic = session.ClassicRules;
        _checks.Request(() => (session, geometry.Version, Problems.Check(geometry, resources, look, engine, fit, classic)));
    }

    private void ShowProblem(Problem p)
    {
        var on = p.Show.Where(e => e.OnMap).ToArray();
        if (on.Length == 0) return;
        _session.Selection.Set(on);
        ShowRequested?.Invoke(on);
    }

    private void FixProblem(Problem p)
    {
        if (p.Fix is { } fix) _session.History.Record(p.FixLabel ?? "Fix", fix);
    }
}
