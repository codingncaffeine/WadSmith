// Renders poses of WadSmith to PNG files, headlessly, with the application's own styles.
//
//   dotnet run --project tools/WadSmith.Capture -c Release -- OUTPUT_DIR [WIDTHxHEIGHT] [--games=FOLDER] [pose ...]
//
// Poses: welcome, editor, editor-heights, editor-selected, perf. Editor poses open a starter room unless
// --map=BASEGAME:SLOT names a base game map to remix (needs GZDoom's data file). perf pans a
// 100,000-wall map and prints frame times. --real runs perf in a real window on the current display
// (use it inside tools/app/perf.sh, never on the desktop).
// Every run uses a throwaway portable profile under OUTPUT_DIR; nothing touches the real one.

using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
var real = args.Contains("--real");
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

if (real)
{
    WadSmith.App.Program.BuildAvaloniaApp().SetupWithoutStarting();
}
else
{
    AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont()
        .SetupWithoutStarting();
}

var vm = new MainViewModel(App.Services);
var window = new MainWindow { DataContext = vm, Width = width, Height = height };
window.Show();

// A grid of 223 by 223 rooms: 99,904 walls.
WadSmith.Core.Projects.Project StressProject()
{
    var project = WadSmith.Core.Projects.Project.Create(Path.Combine(profile, "stress-project"), "Stress", "doom2");
    project.SaveMap(WadSmith.Cli.Commands.StressGenCommand.Build(223));
    return project;
}

// The steps that touch the whole map, timed one by one: the worker steps may grow with the map, the
// UI-thread steps (one edit, one hover) must not.
string MapWork(WadSmith.Core.Projects.Project project)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    double Lap()
    {
        var ms = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        return ms;
    }

    var model = project.LoadMap("MAP01");
    var read = Lap();
    var map = WadSmith.Editor.Model.EditorMap.FromModel(model);
    var build = Lap();
    _ = map.Frame.ToModel();
    var copy = Lap();
    var geometry = WadSmith.Editor.Geometry.MapGeometry.Build(map.Frame);
    var shapes = Lap();
    _ = WadSmith.App.Canvas.CanvasSnapshot.Take(geometry, new WadSmith.App.Canvas.MapLook(null), WadSmith.App.Canvas.MapViewMode.Floors);
    var picture = Lap();

    var history = new WadSmith.Editor.Editing.History(map);
    var sector = map.Sectors[map.Sectors.Count / 2];
    for (var i = 0; i < 50; i++) history.Run(new WadSmith.Editor.Editing.SetProperty<WadSmith.Editor.Model.Sector, double>(sector, "Change floor height", x => x.FloorHeight, (x, v) => x.FloorHeight = v, i));
    Lap();
    for (var i = 0; i < 1000; i++) history.Run(new WadSmith.Editor.Editing.SetProperty<WadSmith.Editor.Model.Sector, double>(sector, "Change floor height", x => x.FloorHeight, (x, v) => x.FloorHeight = v, i));
    var edit = Lap() / 1000;

    var rng = new Random(1);
    var (x0, y0, x1, y1) = geometry.Extent();
    for (var i = 0; i < 1000; i++)
    {
        var q = new WadSmith.Editor.Geometry.MapPoint(x0 + rng.NextDouble() * (x1 - x0), y0 + rng.NextDouble() * (y1 - y0));
        _ = geometry.ThingNear(q, 16);
        _ = geometry.VertexNear(q, 7);
        _ = geometry.LineNear(q, 7);
        _ = geometry.SectorAt(q);
    }

    var hover = Lap() / 1000;
    return string.Create(CultureInfo.InvariantCulture, $"{map.Lines.Count} walls, {map.Sectors.Count} areas. Worker: read {read:0} ms, build {build:0} ms, copy for saving {copy:0} ms, shapes {shapes:0} ms, picture {picture:0} ms. UI thread: one edit {edit * 1000:0} µs, one hover {hover * 1000:0} µs");
}

if (real)
{
    // On a real display the platform's own loop runs, so the measurement is a routine posted to it.
    using var done = new CancellationTokenSource();
    var code = 0;
    Dispatcher.UIThread.Post(async () =>
    {
        try
        {
            await RealPerfAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            code = 1;
        }
        finally
        {
            done.Cancel();
        }
    });
    Dispatcher.UIThread.MainLoop(done.Token);
    App.Services.Shutdown();
    settings.Flush(TimeSpan.FromSeconds(2));
    Log.Flush(TimeSpan.FromSeconds(1));
    return code;
}

async Task RealPerfAsync()
{
    var project = await Task.Run(StressProject).ConfigureAwait(true);
    WadSmith.App.Canvas.FrameStats.Enabled = true;
    await vm.OpenEditorAsync(project, "MAP01").ConfigureAwait(true);
    await Task.Delay(2000).ConfigureAwait(true);
    var canvas = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
    async Task Measure(string label)
    {
        Console.WriteLine($"perf: measuring {label}");
        await Task.Delay(500).ConfigureAwait(true);
        WadSmith.App.Canvas.FrameStats.Reset();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var f = 0; f < 240; f++)
        {
            var dir = f % 40 < 20 ? 1 : -1;
            canvas.PanBy(4 * dir, 3 * dir);
            await Task.Delay(16).ConfigureAwait(true);
        }

        var seconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"perf, {label}: {WadSmith.App.Canvas.FrameStats.Report()}; {WadSmith.App.Canvas.FrameStats.DrawTimes.Count / seconds:0} frames a second"));
    }

    Console.WriteLine($"perf, opening: {WadSmith.App.Canvas.FrameStats.Report()}");
    canvas.FitMap();
    await Measure("whole map in view").ConfigureAwait(true);
    canvas.FitMap();
    canvas.ZoomBy(8);
    await Measure("8x closer").ConfigureAwait(true);
    ((EditorViewModel)vm.Page).ViewMode = WadSmith.App.Canvas.MapViewMode.Heights;
    canvas.FitMap();
    await Measure("heights, whole map").ConfigureAwait(true);
    canvas.FitMap();
    canvas.ZoomBy(8);
    await Measure("heights, 8x closer").ConfigureAwait(true);
    await vm.CloseEditorAsync().ConfigureAwait(true);

    // Close the window and give the render thread time to stop before the process exits, or the
    // graphics driver's exit handlers run while it is still drawing.
    window.Close();
    await Task.Delay(500).ConfigureAwait(true);
}

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
        case "editor" or "editor-heights" or "editor-ceilings" or "editor-lines" or "editor-selected" or "editor-zoomed":
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
            editor.ViewMode = pose switch
            {
                "editor-heights" => WadSmith.App.Canvas.MapViewMode.Heights,
                "editor-ceilings" => WadSmith.App.Canvas.MapViewMode.Ceilings,
                "editor-lines" => WadSmith.App.Canvas.MapViewMode.Lines,
                _ => WadSmith.App.Canvas.MapViewMode.Floors,
            };
            if (pose == "editor-selected")
            {
                var sector = editor.Session.Map.Sectors.OrderByDescending(s => s.CeilingHeight - s.FloorHeight).First();
                editor.Session.Selection.Set(sector);
            }
            else
            {
                editor.Session.Selection.Clear();
            }

            var view = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
            view.FitMap();
            if (pose == "editor-zoomed") view.ZoomBy(3.5);
            Save(pose);
            break;
        case "perf":
        {
            // A 100,000-wall grid, opened as a project, panned frame by frame: whole map in view, then 8x closer.
            var project = StressProject();
            Console.WriteLine($"perf, map work: {MapWork(project)}");
            WadSmith.App.Canvas.FrameStats.Enabled = true;
            Wait(vm.OpenEditorAsync(project, "MAP01"));
            Pump(1.0);
            var canvas = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
            void Measure(string label, string shot)
            {
                WadSmith.App.Canvas.FrameStats.Reset();
                // Back and forth, so the map stays where it started.
                for (var f = 0; f < 120; f++)
                {
                    var dir = f % 40 < 20 ? 1 : -1;
                    canvas.PanBy(4 * dir, 3 * dir);
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                }

                Console.WriteLine($"perf, {label}: {WadSmith.App.Canvas.FrameStats.Report()}");
                Save(shot);
            }

            Console.WriteLine($"perf, opening: {WadSmith.App.Canvas.FrameStats.Report()}");
            canvas.FitMap();
            Measure("whole map in view", "perf");
            canvas.FitMap();
            canvas.ZoomBy(8);
            Measure("8x closer", "perf-zoomed");
            ((EditorViewModel)vm.Page).ViewMode = WadSmith.App.Canvas.MapViewMode.Heights;
            canvas.FitMap();
            Measure("heights, whole map", "perf-heights");
            canvas.FitMap();
            canvas.ZoomBy(8);
            Measure("heights, 8x closer", "perf-heights-zoomed");
            break;
        }

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
