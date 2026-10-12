// Renders poses of WadSmith to PNG files, headlessly, with the application's own styles.
//
//   dotnet run --project tools/WadSmith.Capture -c Release -- OUTPUT_DIR [WIDTHxHEIGHT] [--games=FOLDER] [pose ...]
//
// Poses: welcome, editor, editor-heights, editor-selected. Editor poses open a starter room unless
// --map=BASEGAME:SLOT names a base game map to remix (needs GZDoom's data file).
// Every run uses a throwaway portable profile under OUTPUT_DIR; nothing touches the real one.

using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using WadSmith.App;
using WadSmith.App.ViewModels;
using WadSmith.App.Views;
using WadSmith.Editor.App;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: WadSmith.Capture OUTPUT_DIR [WIDTHxHEIGHT] [--games=FOLDER] [--map=BASEGAME:SLOT] [--large] [pose ...]");
    return 2;
}

var output = Path.GetFullPath(args[0]);
Directory.CreateDirectory(output);
var size = args.Skip(1).FirstOrDefault(a => a.Contains('x', StringComparison.Ordinal) && !a.StartsWith("--", StringComparison.Ordinal)) ?? "1440x900";
var width = double.Parse(size.Split('x')[0], CultureInfo.InvariantCulture);
var height = double.Parse(size.Split('x')[1], CultureInfo.InvariantCulture);
var games = args.FirstOrDefault(a => a.StartsWith("--games=", StringComparison.Ordinal))?["--games=".Length..];
var map = args.FirstOrDefault(a => a.StartsWith("--map=", StringComparison.Ordinal))?["--map=".Length..];
var large = args.Contains("--large");
var poses = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal) && a != size).ToList();
if (poses.Count == 0) poses = ["welcome", "editor", "editor-heights", "editor-selected"];

var profile = Path.Combine(output, "profile");
if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
var paths = AppPaths.Resolve(profile, Environment.GetEnvironmentVariable);
paths.EnsureCreated();
Log.Initialize("capture", paths.Logs);
var settings = SettingsStore.Load(paths.SettingsFile);
if (games is not null) settings.Current.GameFolders.Add(games);
settings.Current.LargeInterface = large;
App.Services = new AppServices(paths, settings, new LaunchOptions(profile, null));

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var vm = new MainViewModel(App.Services);
var window = new MainWindow { DataContext = vm, Width = width, Height = height };
window.Show();

void Pump(double seconds)
{
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < until)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(15);
    }
}

// The app's continuations run on its dispatcher, so the tool pumps until a task is done instead of awaiting it.
void Wait(Task task, double timeoutSeconds = 60)
{
    var until = DateTime.UtcNow.AddSeconds(timeoutSeconds);
    while (!task.IsCompleted)
    {
        if (DateTime.UtcNow > until) throw new TimeoutException("A step of the capture did not finish.");
        Pump(0.02);
    }

    task.GetAwaiter().GetResult();
}

void Save(string name)
{
    Pump(0.3);
    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Nothing was rendered.");
    var file = Path.Combine(output, name + ".png");
    frame.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    Console.WriteLine(file);
}

// Wait for the game scan and the tiles' pictures.
for (var i = 0; i < 400 && vm.Welcome.Scanning; i++) Pump(0.05);
Pump(1.5);

foreach (var pose in poses)
{
    switch (pose)
    {
        case "welcome":
            Save("welcome");
            break;
        case "editor" or "editor-heights" or "editor-selected":
            if (vm.Page is not EditorViewModel)
            {
                var tile = vm.Welcome.Games.FirstOrDefault(g => map is null || g.Location.EndsWith(map.Split(':')[0], StringComparison.OrdinalIgnoreCase));
                if (tile is null)
                {
                    Console.Error.WriteLine("No game tile to start from; pass --games=FOLDER.");
                    return 1;
                }

                if (map is not null)
                {
                    for (var i = 0; i < 200 && tile.Maps.Count == 0; i++) Pump(0.05);
                    var choice = tile.Maps.First(m => m.Slot == map.Split(':')[1]);
                    Wait(tile.Remix!.ExecuteAsync(choice));
                }
                else
                {
                    Wait(tile.NewMap!.ExecuteAsync(tile));
                }

                Pump(1.0);
            }

            var editor = (EditorViewModel)vm.Page;
            editor.ShowHeights = pose == "editor-heights";
            if (pose == "editor-selected")
            {
                var sector = editor.Session.Map.Sectors.OrderByDescending(s => s.CeilingHeight - s.FloorHeight).First();
                editor.Session.Selection.Set(sector);
            }
            else
            {
                editor.Session.Selection.Clear();
            }

            Save(pose);
            break;
        default:
            Console.Error.WriteLine($"Unknown pose {pose}");
            break;
    }
}

Wait(vm.CloseEditorAsync());
App.Services.Shutdown();
settings.Flush(TimeSpan.FromSeconds(2));
Log.Flush(TimeSpan.FromSeconds(1));
return 0;
