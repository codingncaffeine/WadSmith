using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Editor;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.App.ViewModels;

/// <summary>One thing a search found, as the Find panel lists it: what it is, the part that matched, and a button that shows it.</summary>
internal sealed record FoundRow(string Label, string Where, IRelayCommand Show);

/// <summary>
/// Find and replace (Ctrl+F, D58): a kind of thing, what to look for, and optionally what to put in its place. The search
/// runs on a worker as you type and again after each change to the map; Replace all is one undo step.
/// </summary>
internal sealed partial class FindViewModel : ObservableObject
{
    /// <summary>The most found things the list shows; the count says how many there are in all.</summary>
    public const int Listed = 200;

    private readonly EditorSession _session;
    private readonly Action _showSelection;
    private IReadOnlyList<Found> _found = [];
    private bool _searched;

    public FindViewModel(EditorSession session, Action showSelection)
    {
        _session = session;
        _showSelection = showSelection;
        session.GeometryChanged += () =>
        {
            if (Active && _searched) Search();
        };
    }

    /// <summary>Whether the panel is showing: searches follow the map's changes only then.</summary>
    public bool Active { get; set; }

    private static readonly FindKind[] KindOrder = [FindKind.WallPicture, FindKind.FloorPicture, FindKind.Object, FindKind.WallAction, FindKind.RoomEffect, FindKind.Tag];

    public IReadOnlyList<string> Kinds { get; } = ["Wall pictures", "Floor and ceiling pictures", "Objects", "Wall actions", "Room effects", "Tags"];

    /// <summary>Which kind of thing is looked for.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsObjects), nameof(WhatHint), nameof(WithHint))]
    private int _kindIndex;

    /// <summary>What to look for, as typed.</summary>
    [ObservableProperty]
    private string _what = "";

    /// <summary>What to put in its place, as typed.</summary>
    [ObservableProperty]
    private string _with = "";

    [ObservableProperty]
    private bool _onEasy = true;

    [ObservableProperty]
    private bool _onNormal = true;

    [ObservableProperty]
    private bool _onHard = true;

    /// <summary>Whether only what is selected is searched.</summary>
    [ObservableProperty]
    private bool _inSelection;

    /// <summary>How many were found, or why the search cannot run.</summary>
    [ObservableProperty]
    private string _summary = "Choose what to look for and type its name or number.";

    /// <summary>Whether anything was found (the Select and Replace buttons work).</summary>
    [ObservableProperty]
    private bool _anyFound;

    public ObservableCollection<FoundRow> Results { get; } = [];

    /// <summary>Whether objects are looked for (the difficulty boxes show).</summary>
    public bool IsObjects => Kind == FindKind.Object;

    private FindKind Kind => KindOrder[Math.Clamp(KindIndex, 0, KindOrder.Length - 1)];

    public string WhatHint => Kind switch
    {
        FindKind.WallPicture => "A picture's name: STARTAN3, or STARTAN* for every one that starts so",
        FindKind.FloorPicture => "A picture's name: FLOOR4_8, or NUKAGE* for every one that starts so",
        FindKind.Object => "Its number, or a few letters of its name: 3001, imp",
        FindKind.WallAction => "Its number, or a few letters of its name: 12, Door_Raise",
        FindKind.RoomEffect => "Its number, or a few letters of its name: 71, hurts",
        _ => "A number: 7",
    };

    public string WithHint => Kind switch
    {
        FindKind.WallPicture or FindKind.FloorPicture => "The picture to put in its place",
        FindKind.Object => "The kind to make them: a number or the whole name",
        FindKind.Tag => "The new tag",
        _ => "The new one: a number or the whole name",
    };

    partial void OnKindIndexChanged(int value) => Search();

    partial void OnWhatChanged(string value) => Search();

    partial void OnOnEasyChanged(bool value) => Search();

    partial void OnOnNormalChanged(bool value) => Search();

    partial void OnOnHardChanged(bool value) => Search();

    partial void OnInSelectionChanged(bool value) => Search();

    private FindQuery Query()
    {
        var skills = (OnEasy ? FindSkills.Easy : 0) | (OnNormal ? FindSkills.Normal : 0) | (OnHard ? FindSkills.Hard : 0);
        return new FindQuery(Kind, What, skills);
    }

    /// <summary>Looks again with what is typed now (on a worker); the list and the count follow when it answers.</summary>
    public void Search()
    {
        var query = Query();
        if (Editor.Editing.Finding.WhyNot(query, _session.Resources?.Things) is { } why)
        {
            _searched = false;
            Show([], query.What.Trim().Length == 0 ? "Choose what to look for and type its name or number." : why);
            return;
        }

        _searched = true;
        IReadOnlyCollection<Element>? within = InSelection ? [.. _session.Selection.Items] : null;
        _session.Find(query, within, found =>
        {
            var count = found.Count;
            var where = InSelection ? " in the selection" : "";
            Show(found, count == 0 ? $"Nothing found{where}." : count <= Listed
                ? string.Create(CultureInfo.InvariantCulture, $"{count} found{where}.")
                : string.Create(CultureInfo.InvariantCulture, $"{count:N0} found{where}; the first {Listed} are listed. Select them all to see every one."));
        });
    }

    private void Show(IReadOnlyList<Found> found, string summary)
    {
        _found = found;
        Results.Clear();
        foreach (var f in found.Take(Listed))
        {
            var element = f.Element;
            Results.Add(new FoundRow(Describe(element), f.Where, new RelayCommand(() =>
            {
                if (!element.OnMap) return;
                _session.Selection.Set(element);
                _showSelection();
            })));
        }

        Summary = summary;
        AnyFound = found.Count > 0;
    }

    private string Describe(Element e) => e switch
    {
        Side { Line: { } l } s => string.Create(CultureInfo.InvariantCulture, $"Wall face, {l.Length:0} long, at {Mid(l)}{(ReferenceEquals(l.Back, s) ? " (back)" : "")}"),
        Line l => string.Create(CultureInfo.InvariantCulture, $"Wall, {l.Length:0} long, at {Mid(l)}"),
        Sector s => s.Tag != 0 ? string.Create(CultureInfo.InvariantCulture, $"Room, tag {s.Tag}") : "Room",
        Thing t => string.Create(CultureInfo.InvariantCulture, $"{_session.Resources?.Things.Find(t.Type)?.Name ?? $"Object {t.Type}"} at ({t.X:0}, {t.Y:0})"),
        _ => "Corner",
    };

    private static string Mid(Line l) => string.Create(CultureInfo.InvariantCulture, $"({(l.Start.X + l.End.X) / 2:0}, {(l.Start.Y + l.End.Y) / 2:0})");

    /// <summary>Selects everything found and brings it into view.</summary>
    [RelayCommand]
    private void SelectFound()
    {
        Element[] elements = [.. _found.Select(f => f.Element).Where(e => e.OnMap)];
        if (elements.Length == 0) return;
        _session.Selection.Set(elements);
        _showSelection();
    }

    /// <summary>Puts what is typed in "Replace with" in place of everything found, as one undo step; the search runs again after.</summary>
    [RelayCommand]
    private void ReplaceAll()
    {
        if (_found.Count == 0) return;
        _session.ReplaceFound(Query(), _found, With);
    }
}
