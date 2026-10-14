using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WadSmith.Core;
using WadSmith.Core.Exporting;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Model;

namespace WadSmith.App.ViewModels;

/// <summary>What a project can be exported for: the engine or port family, the ports that play it, and the file it makes.</summary>
internal sealed record ExportChoice(string Id, string Title, string Plays, string Extension)
{
    public override string ToString() => Title;
}

/// <summary>One line of the report as the card shows it, with Show when it is about the open map.</summary>
internal sealed record ExportLine(string Title, string Detail, string Count, bool Breaks, ICommand? Show)
{
    /// <summary>How many, then what about them: "5 objects · number 9830: the game stops …".</summary>
    public string Sub => string.Join(" · ", new[] { Count, Detail }.Where(s => s.Length > 0));

    public bool HasSub => Sub.Length > 0;
}

/// <summary>
/// The Export card (Phase 12): choose GZDoom or a classic port, see what the map would lose there — exported into the
/// cache on a worker each time the choice changes — and save that same file where you want it.
/// </summary>
internal sealed partial class ExportViewModel : ObservableObject
{
    private readonly EditorSession _session;
    private readonly string _cache;
    private readonly Action<IReadOnlyList<Element>> _show;
    private readonly Action _close;
    private PreparedExport? _prepared;
    private int _request;

    public ExportViewModel(EditorSession session, string cacheFolder, Action<IReadOnlyList<Element>> show, Action close)
    {
        _session = session;
        _cache = cacheFolder;
        _show = show;
        _close = close;
        Choices = [.. ProjectExport.ChoicesFor(session.Project.ClassicNamespace).Select(c => new ExportChoice(c.Id, c.Title,
            c.Id == ProjectExport.GZDoom ? "UZDoom and GZDoom: the project as one .pk3" : ClassicPorts.Get(c.Id)!.Ports, c.Extension))];
        _selected = Choices.FirstOrDefault(c => c.Id == session.Project.Info.Target) ?? Choices[0];
        Prepare();
    }

    public IReadOnlyList<ExportChoice> Choices { get; }

    [ObservableProperty]
    private ExportChoice _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _busy = true;

    [ObservableProperty]
    private string _summary = "Exporting…";

    /// <summary>Whether something in the export stops the port with an error.</summary>
    [ObservableProperty]
    private bool _breaks;

    /// <summary>Where the last save went ("" before one).</summary>
    [ObservableProperty]
    private string _saved = "";

    public ObservableCollection<ExportLine> Lines { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>Set by the view: asks where to save, given a suggested file name; null when the user cancels.</summary>
    public Func<string, Task<string?>>? PickFile { get; set; }

    partial void OnSelectedChanged(ExportChoice value) => Prepare();

    /// <summary>Exports for the chosen target into the cache; the newest choice wins when the user changes it again meanwhile.</summary>
    private async void Prepare()
    {
        var request = ++_request;
        var choice = Selected;
        Busy = true;
        Summary = $"Exporting for {choice.Title}…";
        Saved = "";
        Lines.Clear();
        Notes.Clear();
        PreparedExport prepared;
        try
        {
            prepared = await Exports.PrepareAsync(_session, choice.Id, _cache).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CorruptDataException or ArgumentException)
        {
            if (request != _request) return;
            Log.Warn($"Exporting for {choice.Id} failed: {ex.Message}");
            Summary = $"The export could not be made: {ex.Message}";
            Busy = false;
            _prepared = null;
            SaveCommand.NotifyCanExecuteChanged();
            return;
        }

        if (request != _request) return;
        _prepared = prepared;
        var losses = prepared.Report.Losses;
        Breaks = prepared.Report.Breaks;
        Summary = losses.Count == 0
            ? $"Nothing is lost: the maps play in {choice.Title} as they are."
            : Breaks
                ? $"Something here stops {choice.Title} with an error (marked below): change it before you share the file."
                : $"{losses.Count} {(losses.Count == 1 ? "thing does" : "things do")} not survive in {choice.Title}:";
        foreach (var row in prepared.Rows)
        {
            var l = row.Loss;
            var count = l.Indexes.Count == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $"{l.Indexes.Count} {l.On}{(l.Indexes.Count == 1 ? "" : "s")}");
            var where = l.Map is null ? "" : l.Map + ": ";
            var shown = row.Show;
            Lines.Add(new ExportLine(where + l.Title, l.Detail, count, l.Breaks, shown.Count > 0 ? new RelayCommand(() => _show(shown)) : null));
        }

        foreach (var n in prepared.Report.Notes) Notes.Add(n);
        Busy = false;
    }

    private bool CanSave() => !Busy && _prepared is not null;

    /// <summary>Saves the exported file where the user chooses (copied on a worker).</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_prepared is not { } prepared || PickFile is null) return;
        var name = string.Concat(_session.Project.Info.Name.Split(Path.GetInvalidFileNameChars())).Trim();
        var path = await PickFile((name.Length > 0 ? name : "map") + prepared.Extension).ConfigureAwait(true);
        if (path is null) return;
        try
        {
            await Task.Run(() => File.Copy(prepared.File, path, overwrite: true)).ConfigureAwait(true);
            Saved = $"Saved as {path}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Saved = $"It could not be saved there: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Close() => _close();
}
