using System.Text.Json;
using System.Text.Json.Serialization;

namespace WadSmith.Editor.App;

/// <summary>How much teaching the interface does (D34): never what it can do.</summary>
public enum TooltipLength
{
    Full,
    Brief,
    Off,
}

/// <summary>Which views share the map area (D46): the plan alone, the plan and the 3D view side by side, or the 3D view alone.</summary>
public enum MapLayout
{
    Split,
    Plan,
    ThreeD,
}

/// <summary>Everything WadSmith remembers between runs.</summary>
public sealed class AppSettings
{
    /// <summary>Folders the user added that hold base games and add-ons.</summary>
    public List<string> GameFolders { get; set; } = [];

    /// <summary>Recently opened projects and files, newest first.</summary>
    public List<string> Recent { get; set; } = [];

    /// <summary>The engine to play with, when the user picked one; null means the first one found.</summary>
    public string? EnginePath { get; set; }

    // The teaching layer (D34).
    public bool AskedAboutExperience { get; set; }

    public bool CoachTips { get; set; } = true;

    public bool GuidePrompts { get; set; } = true;

    public TooltipLength Tooltips { get; set; } = TooltipLength.Full;

    public bool Celebrations { get; set; } = true;

    public bool ClassicNames { get; set; }

    public bool DoomBuilderKeys { get; set; }

    /// <summary>Tips the coach has already given, so it does not repeat itself.</summary>
    public List<string> CoachSeen { get; set; } = [];

    // Comfort (D31).
    public bool LargeInterface { get; set; }

    /// <summary>The plan shows the player's size at the pointer and marks where the player cannot pass (D20).</summary>
    public bool ShowPlayerFit { get; set; } = true;

    /// <summary>Which views share the map area (D46); Split until the user picks another.</summary>
    public MapLayout Layout { get; set; }

    /// <summary>Use the desktop's own title bar instead of WadSmith's.</summary>
    public bool UseSystemTitleBar { get; set; }

    /// <summary>Window placement, restored on the next run.</summary>
    public double WindowWidth { get; set; } = 1280;

    public double WindowHeight { get; set; } = 800;

    /// <summary>The first launch opens maximised, which suits every screen from a handheld up.</summary>
    public bool WindowMaximized { get; set; } = true;
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. A save captures the settings at once and a
/// worker writes them, replacing the file in one step; saves in quick succession collapse into the
/// latest. A file that cannot be read is set aside and the defaults are used.
/// </summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private string? _pending;
    private Task _writer = Task.CompletedTask;

    private SettingsStore(string path, AppSettings current)
    {
        _path = path;
        Current = current;
    }

    public AppSettings Current { get; }

    public static SettingsStore Load(string path)
    {
        AppSettings? settings = null;
        if (File.Exists(path))
        {
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Settings could not be read ({ex.Message}); using the defaults and keeping the old file as settings.json.unreadable.");
                try
                {
                    File.Move(path, path + ".unreadable", overwrite: true);
                }
                catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return new SettingsStore(path, settings ?? new AppSettings());
    }

    /// <summary>Captures the settings now; a worker writes them. Returns at once.</summary>
    public void Save()
    {
        var json = JsonSerializer.Serialize(Current, Json);
        lock (_gate)
        {
            var idle = _pending is null;
            _pending = json;
            if (idle) _writer = Task.Run(WriteLoop);
        }
    }

    /// <summary>Waits for the last save to reach the disk (at exit).</summary>
    public bool Flush(TimeSpan timeout) => _writer.Wait(timeout);

    private void WriteLoop()
    {
        while (true)
        {
            string json;
            lock (_gate)
            {
                if (_pending is null) return;
                json = _pending;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".saving";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Settings could not be saved: {ex.Message}");
            }

            lock (_gate)
            {
                if (ReferenceEquals(_pending, json)) _pending = null;
            }
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}
