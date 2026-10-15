using WadSmith.Core.Games;
using WadSmith.Editor.App;

namespace WadSmith.App;

/// <summary>
/// Watches the folders the user added (and WadSmith's own games folder) for game and map files arriving, moving or going,
/// a folder of them included, and says so once things have been quiet for a moment, as a copy fires many changes: the
/// start screen then looks for the games and maps again. Only those folders are watched, each whole, never the game
/// libraries or the folders around them (a drive's worth of folders); a folder that cannot be watched (the system's limit
/// on watches reached, a drive gone) is left to the look taken when the window is used again.
/// </summary>
internal sealed class GameFolderWatcher : IDisposable
{
    /// <summary>How long after the last change the folders count as settled.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1.5);

    /// <summary>At most this many folders are watched: each watch is one of the user's limited inotify instances.</summary>
    private const int MostFolders = 16;

    private readonly Action _changed;
    private readonly Timer _settle;
    private readonly object _gate = new();
    private List<FileSystemWatcher> _watchers = [];
    private bool _disposed;

    /// <param name="changed">Called on a worker thread once the folders have settled after a change.</param>
    public GameFolderWatcher(Action changed)
    {
        _changed = changed;
        _settle = new Timer(_ => Settled(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>The folders being watched now.</summary>
    public IReadOnlyList<string> Folders
    {
        get
        {
            lock (_gate) return [.. _watchers.Select(w => w.Path)];
        }
    }

    /// <summary>Watches <paramref name="folders"/> (those that exist) instead of the folders watched before. Walks each folder: call it off the UI thread.</summary>
    public void Watch(IEnumerable<string> folders)
    {
        var wanted = folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f)))
            .Distinct(Core.WindowsFolders.PathComparer).Where(Directory.Exists).Take(MostFolders).ToList();
        var made = new List<FileSystemWatcher>();
        foreach (var folder in wanted)
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += (_, e) => Seen(e.FullPath, folder);
                watcher.Deleted += (_, e) => Seen(e.FullPath, folder);
                watcher.Changed += (_, e) => Seen(e.FullPath, folder);
                watcher.Renamed += (_, e) =>
                {
                    Seen(e.OldFullPath, folder);
                    Seen(e.FullPath, folder);
                };
                watcher.Error += (_, e) =>
                {
                    Log.Warn($"Watching {folder} for new games and maps stopped: {e.GetException().Message}");
                    Changed();
                };
                watcher.EnableRaisingEvents = true;
                made.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                Log.Warn($"{folder} cannot be watched for new games and maps ({ex.Message}); WadSmith looks again when its window is used.");
            }
        }

        List<FileSystemWatcher> old;
        lock (_gate)
        {
            if (_disposed)
            {
                old = made;
            }
            else
            {
                old = _watchers;
                _watchers = made;
            }
        }

        foreach (var w in old) w.Dispose();
    }

    /// <summary>A change under <paramref name="root"/>: a game or map file, or a folder (one moved in may hold some), within the depth a scan looks.</summary>
    private void Seen(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length > GameLocator.DefaultDepth + 1) return;
        var extension = Path.GetExtension(path);
        if (extension.Length > 0 && !GameLocator.IsMapOrGameFile(path) && !Directory.Exists(path)) return;
        Changed();
    }

    private void Changed()
    {
        lock (_gate)
        {
            if (!_disposed) _settle.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void Settled()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }

        _changed();
    }

    public void Dispose()
    {
        List<FileSystemWatcher> old;
        lock (_gate)
        {
            _disposed = true;
            old = _watchers;
            _watchers = [];
            _settle.Dispose();
        }

        foreach (var w in old) w.Dispose();
    }
}
