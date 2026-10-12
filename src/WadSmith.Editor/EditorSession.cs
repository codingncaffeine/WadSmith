using WadSmith.Core.Maps;
using WadSmith.Core.Preview;
using WadSmith.Core.Projects;
using WadSmith.Editor.App;
using WadSmith.Editor.Editing;
using WadSmith.Editor.Model;

namespace WadSmith.Editor;

public enum SaveState
{
    Saved,
    Pending,
    Saving,
    Failed,
}

/// <summary>
/// One map open for editing: the project it belongs to, the map, its history and selection, and
/// continuous saving (D24). There is no Save step: a change reaches the project folder about a second
/// after editing pauses. The map is captured on the UI thread, which is the only thread that touches
/// it, and written by a worker, so the disk never holds the interface up.
/// </summary>
public sealed class EditorSession : IDisposable
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(900);

    private readonly Action<Action> _onUiThread;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private long _savedVersion;
    private Task _lastSave = Task.CompletedTask;
    private bool _disposed;

    private EditorSession(Project project, string slot, EditorMap map, Action<Action> onUiThread)
    {
        Project = project;
        Slot = slot;
        Map = map;
        History = new History(map);
        _onUiThread = onUiThread;
        _savedVersion = map.Version;
        _timer = new Timer(_ => _onUiThread(CaptureAndSave), null, Timeout.Infinite, Timeout.Infinite);
        map.Changed += _ =>
        {
            State = SaveState.Pending;
            StateChanged?.Invoke(State);
            _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        };
    }

    public Project Project { get; }

    public string Slot { get; }

    public EditorMap Map { get; }

    public History History { get; }

    public Selection Selection { get; } = new();

    public SaveState State { get; private set; } = SaveState.Saved;

    /// <summary>The last save's failure, in a sentence.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="State"/> changes.</summary>
    public event Action<SaveState>? StateChanged;

    /// <param name="project">The project.</param>
    /// <param name="slot">The map to open.</param>
    /// <param name="onUiThread">Runs an action on the UI thread (the app passes its dispatcher).</param>
    public static EditorSession Open(Project project, string slot, Action<Action> onUiThread)
    {
        var map = EditorMap.FromModel(project.LoadMap(slot));
        var session = new EditorSession(project, slot, map, onUiThread);
        session.SaveVersion("Opened");
        return session;
    }

    /// <summary>Waits for every pending change to reach the disk (on close, before Play).</summary>
    public Task FlushAsync()
    {
        if (Map.Version != _savedVersion) CaptureAndSave();
        return _lastSave;
    }

    /// <summary>
    /// Keeps a copy of the map as it is now, with a thumbnail, in the project's versions folder
    /// (<c>.wadsmith/versions/SLOT/</c>). Runs on a worker after a quick capture.
    /// </summary>
    public void SaveVersion(string reason)
    {
        var model = Map.ToModel();
        var folder = Path.Combine(Project.Folder, ".wadsmith", "versions", Slot);
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(folder);
                var wad = Core.Containers.WadWriter.Write(Core.Containers.WadKind.Pwad,
                    new[] { (model.Name, (ReadOnlyMemory<byte>)Array.Empty<byte>()) }.Concat(MapConversion.ToUdmfLumps(model).Select(l => (l.Name, (ReadOnlyMemory<byte>)l.Data))));
                File.WriteAllBytes(Path.Combine(folder, $"{stamp}.wad"), wad);
                File.WriteAllBytes(Path.Combine(folder, $"{stamp}.png"), Core.Graphics.ImageCodec.EncodePng(MapPreview.Render(model, new MapPreview.Options(256, 8))));
                File.WriteAllText(Path.Combine(folder, $"{stamp}.txt"), reason);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"A version of {Slot} could not be kept: {ex.Message}");
            }
        });
    }

    private void CaptureAndSave()
    {
        if (_disposed || Map.Version == _savedVersion) return;
        var version = Map.Version;
        var model = Map.ToModel();
        State = SaveState.Saving;
        StateChanged?.Invoke(State);
        _lastSave = Task.Run(async () =>
        {
            await _writer.WaitAsync().ConfigureAwait(false);
            try
            {
                Project.SaveMap(model);
                _onUiThread(() =>
                {
                    _savedVersion = Math.Max(_savedVersion, version);
                    LastError = null;
                    State = Map.Version == _savedVersion ? SaveState.Saved : SaveState.Pending;
                    StateChanged?.Invoke(State);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Saving {Slot} failed", ex);
                _onUiThread(() =>
                {
                    LastError = $"Your changes could not be saved: {ex.Message}";
                    State = SaveState.Failed;
                    StateChanged?.Invoke(State);
                });
            }
            finally
            {
                _writer.Release();
            }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
