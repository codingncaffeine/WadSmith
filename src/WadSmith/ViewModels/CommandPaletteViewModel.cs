using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Editor.App;

namespace WadSmith.App.ViewModels;

/// <summary>
/// One thing the editor can do, by name: its group, its shortcut, and other words people search for it by (the
/// classic names among them, which the palette finds but never shows); <c>Also</c> is a second key that does the same,
/// not shown (Ctrl+Shift+Z for Redo).
/// </summary>
internal sealed record EditorCommand(string Name, string Group, string Shortcut, string Words, Action Run, Func<bool>? Can = null, string Also = "")
{
    public bool Available => Can?.Invoke() ?? true;
}

/// <summary>
/// The command palette (Ctrl+K, §3.3): every action by name with its shortcut, found by a few letters in any order of
/// closeness, run with Enter. With nothing typed it lists what was run last, then everything.
/// </summary>
internal sealed partial class CommandPaletteViewModel : ObservableObject
{
    private const int Shown = 60;
    private readonly IReadOnlyList<EditorCommand> _all;
    private readonly List<string> _recent;
    private readonly Action _close;
    private readonly Action? _ran;

    /// <summary>The palette over <paramref name="all"/>; <paramref name="ran"/> is told when a command is run from it, before it runs.</summary>
    public CommandPaletteViewModel(IReadOnlyList<EditorCommand> all, List<string> recent, Action close, Action? ran = null)
    {
        _all = all;
        _recent = recent;
        _close = close;
        _ran = ran;
        Refresh();
    }

    /// <summary>What has been typed.</summary>
    [ObservableProperty]
    private string _query = "";

    /// <summary>The commands that match, best first.</summary>
    public ObservableCollection<EditorCommand> Results { get; } = [];

    [ObservableProperty]
    private int _selectedIndex;

    /// <summary>Shown when nothing matches.</summary>
    public bool NothingFound => Results.Count == 0;

    partial void OnQueryChanged(string value) => Refresh();

    private void Refresh()
    {
        Results.Clear();
        var usable = _all.Where(c => c.Available);
        IEnumerable<EditorCommand> found;
        if (string.IsNullOrWhiteSpace(Query))
        {
            var recent = _recent.Select(n => usable.FirstOrDefault(c => c.Name == n)).OfType<EditorCommand>().ToList();
            found = recent.Concat(usable.Except(recent));
        }
        else
        {
            // A match in the name beats one in the other words; ties keep the catalogue's order.
            found = usable
                .Select((c, i) => (Command: c, Index: i, Score: Best(c)))
                .Where(x => x.Score is not null)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Index)
                .Select(x => x.Command);
        }

        foreach (var c in found.Take(Shown)) Results.Add(c);
        SelectedIndex = Results.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(NothingFound));
    }

    private int? Best(EditorCommand c)
    {
        var best = Fuzzy.Score(Query, c.Name);
        foreach (var word in c.Words.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Fuzzy.Score(Query, word) is { } s && (best is null || s - 500 > best)) best = s - 500;
        }

        // The group's name typed in full or begun ("tools", "view") lists the group.
        if (Fuzzy.Score(Query, c.Group) is >= 6_000 and var g && (best is null || g - 3000 > best)) best = g - 3000;
        return best;
    }

    /// <summary>Moves the highlight up or down the list (the arrow keys), stopping at the ends.</summary>
    public void Move(int by)
    {
        if (Results.Count == 0) return;
        SelectedIndex = Math.Clamp(SelectedIndex + by, 0, Results.Count - 1);
    }

    /// <summary>Runs the highlighted command (Enter).</summary>
    [RelayCommand]
    private void RunSelected()
    {
        if (SelectedIndex >= 0 && SelectedIndex < Results.Count) Run(Results[SelectedIndex]);
    }

    /// <summary>Closes the palette, then runs the command, and remembers it at the top of the list.</summary>
    [RelayCommand]
    private void Run(EditorCommand command)
    {
        _close();
        _recent.Remove(command.Name);
        _recent.Insert(0, command.Name);
        if (_recent.Count > 8) _recent.RemoveAt(_recent.Count - 1);
        if (!command.Available) return;
        _ran?.Invoke();
        command.Run();
    }

    [RelayCommand]
    private void Close() => _close();
}
