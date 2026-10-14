// Renders poses of WadSmith to PNG files, headlessly, with the application's own styles.
//
//   dotnet run --project tools/WadSmith.Capture -c Release -- OUTPUT_DIR [WIDTHxHEIGHT] [--games=FOLDER] [pose ...]
//
// Poses: welcome, editor, editor-heights, editor-selected, editor-face, perf. Editor poses open a starter room unless
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
var size = args.Skip(1).FirstOrDefault(a => System.Text.RegularExpressions.Regex.IsMatch(a, "^[0-9]+x[0-9]+$")) ?? "1440x900";
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

    // The 3D view beside the plan: the camera turning round the whole map, every picture timed on its render thread.
    var view3d = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().FirstOrDefault();
    if (view3d is not null)
    {
        for (var i = 0; i < 400 && !view3d.HasPicture; i++) await Task.Delay(50).ConfigureAwait(true);
        await Task.Delay(500).ConfigureAwait(true);
        WadSmith.App.Canvas.FrameStats.Reset();
        var turning = System.Diagnostics.Stopwatch.StartNew();
        for (var f = 0; f < 240; f++)
        {
            view3d.TurnBy(1.5);
            await Task.Delay(16).ConfigureAwait(true);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"perf, 3D turning round the whole map: {WadSmith.App.Canvas.FrameStats.Report3D()} (render thread); {240 / turning.Elapsed.TotalSeconds:0} turns a second"));
    }

    // Soak: keep panning (floors view, zooming in and out a little) and watch resident memory.
    var soak = double.Parse(args.FirstOrDefault(a => a.StartsWith("--soak=", StringComparison.Ordinal))?["--soak=".Length..] ?? "0", CultureInfo.InvariantCulture);
    if (soak > 0)
    {
        ((EditorViewModel)vm.Page).ViewMode = WadSmith.App.Canvas.MapViewMode.Floors;
        canvas.FitMap();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var next = 0.0;
        var frame = 0;
        while (clock.Elapsed.TotalSeconds < soak)
        {
            var dir = frame % 40 < 20 ? 1 : -1;
            canvas.PanBy(6 * dir, 4 * dir);
            if (frame % 400 == 0) canvas.ZoomBy(frame % 800 == 0 ? 3 : 1 / 3.0);
            frame++;
            if (clock.Elapsed.TotalSeconds >= next)
            {
                var rss = File.ReadLines("/proc/self/status").First(l => l.StartsWith("VmRSS:", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"soak: {clock.Elapsed.TotalSeconds:0} s, resident {long.Parse(rss, CultureInfo.InvariantCulture) / 1024} MB, managed heap {GC.GetTotalMemory(false) / (1024 * 1024)} MB, {frame} frames"));
                next += 30;
            }

            await Task.Delay(16).ConfigureAwait(true);
        }
    }

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

// Clicks the button showing <content> with real pointer events at its middle, as the user would; false when none shows.
bool ClickButton(string content, Avalonia.Visual? within = null)
{
    var button = (within ?? window).GetVisualDescendants().OfType<Avalonia.Controls.Button>().FirstOrDefault(b => b.IsEffectivelyVisible && (Equals(b.Content, content) || Avalonia.Automation.AutomationProperties.GetName(b) == content));
    if (button is null) return false;
    Avalonia.Controls.ControlExtensions.BringIntoView(button);
    Pump(0.3);
    var middle = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window) ?? throw new InvalidOperationException($"{content} is not in the window.");
    window.MouseMove(middle, Avalonia.Input.RawInputModifiers.None);
    window.MouseDown(middle, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
    window.MouseUp(middle, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
    Pump(0.3);
    return true;
}

// Wait for the game scan and the tiles' pictures.
for (var i = 0; i < 400 && vm.Welcome.Scanning; i++) Pump(0.05);
Pump(1.5);

foreach (var pose in poses)
{
    switch (pose)
    {
        case "make-map":
        {
            // Make me a map (D50), as the user goes: the card's button, the page, Make it, the editor.
            var tile = vm.Welcome.Games.FirstOrDefault(g => g.CanMakeMap && (map is null || g.Location.EndsWith(map.Split(':')[0], StringComparison.OrdinalIgnoreCase)));
            if (tile is not null) vm.Welcome.SelectedGame = tile;
            Pump(0.3);
            var card = tile is null ? null : window.GetVisualDescendants().OfType<Avalonia.Controls.ContentControl>().FirstOrDefault(p => p.IsEffectivelyVisible && ReferenceEquals(p.Content, tile));
            if (card is null || !ClickButton("Make me a map", card))
            {
                Console.Error.WriteLine("No game card with Make me a map; pass --games=FOLDER.");
                return 1;
            }

            if (vm.Page is not MakeMapViewModel page) throw new InvalidOperationException("The page did not open.");
            page.Seed = args.FirstOrDefault(a => a.StartsWith("--seed=", StringComparison.Ordinal))?["--seed=".Length..] ?? "misty-cavern";
            if (args.FirstOrDefault(a => a.StartsWith("--style=", StringComparison.Ordinal)) is { } chosenStyle) page.Style.Index = (int)Enum.Parse<WadSmith.Editor.Generate.MapStyle>(chosenStyle["--style=".Length..], true);
            if (args.FirstOrDefault(a => a.StartsWith("--size=", StringComparison.Ordinal)) is { } chosenSize) page.Size.Index = (int)Enum.Parse<WadSmith.Editor.Generate.MapSize>(chosenSize["--size=".Length..], true);
            if (args.Contains("--borrow"))
            {
                // Every other game's boxes ticked by clicking them, as the user would.
                foreach (var box in window.GetVisualDescendants().OfType<Avalonia.Controls.CheckBox>().Where(b => b.DataContext is SourceRow { IsBase: false } && b.IsEffectivelyVisible).ToList())
                {
                    var at = box.TranslatePoint(new Avalonia.Point(box.Bounds.Height / 2, box.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.1);
                }

                // The rows' games are read on a worker: wait for them before making.
                for (var i = 0; i < 400 && page.Sources.Any(s => s.Resources is null); i++) Pump(0.05);
            }

            for (var i = 0; i < 100 && (page.Plan is null || page.Facts.Length == 0 || !page.Facts.StartsWith(WadSmith.Editor.Generate.SeedWords.Title(page.Seed), StringComparison.Ordinal)); i++) Pump(0.05);
            Pump(1.0);
            Console.WriteLine($"make-map: {page.Facts}\n{page.Verdict}\nsources: {string.Join("; ", page.Sources.Select(s => $"{s.Title} ({s.Note})"))}");
            Save("make-map");
            if (args.Contains("--make"))
            {
                ClickButton("Make it");
                var making = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < 6000 && vm.Page is not EditorViewModel; i++) Pump(0.05);
                Console.WriteLine($"make-map: made in {making.Elapsed.TotalSeconds:0.0} s");
                if (vm.Page is not EditorViewModel made) throw new InvalidOperationException($"The map was not made: {page.Status}");
                Pump(3.0);
                Console.WriteLine($"make-map: opened {made.Session.Project.Folder}, {made.Session.Map.Sectors.Count(s => s.OnMap)} rooms, {made.Session.Map.Things.Count(t => t.OnMap)} things");
                Save("make-map-editor");
            }

            break;
        }

        case "welcome":
            // After an editor pose, back to the start screen first: it then shows the map just made on its game's tile.
            if (vm.Page is EditorViewModel)
            {
                Wait(vm.CloseEditorAsync());
                for (var i = 0; i < 100 && vm.Welcome.Games.All(t => t.Projects.Count == 0); i++) Pump(0.05);
                Pump(0.3);
            }

            // The start screen scrolled to its end: the lower part of the cards, where the maps are.
            if (args.Contains("--bottom"))
            {
                foreach (var scroll in window.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>()) scroll.ScrollToEnd();
                Pump(0.3);
            }

            Save("welcome");
            break;
        case "crash-edit":
        {
            // Phase 2's crash proof: an edit, then the process killed outright after --crash-after seconds.
            var after = double.Parse(args.FirstOrDefault(a => a.StartsWith("--crash-after=", StringComparison.Ordinal))?["--crash-after=".Length..] ?? "1.0", CultureInfo.InvariantCulture);
            var project = WadSmith.Core.Projects.Project.Create(Path.Combine(profile, "crash-project"), "Crash", "doom2");
            project.SaveMap(WadSmith.Editor.StarterRoom.Create("MAP01", "doom"));
            Wait(vm.OpenEditorAsync(project, "MAP01"));
            var session = ((EditorViewModel)vm.Page).Session;
            Pump(1.5);
            var room = session.Map.Sectors[0];
            session.History.Run(new WadSmith.Editor.Editing.SetProperty<WadSmith.Editor.Model.Sector, double>(room, "Change floor height", x => x.FloorHeight, (x, v) => x.FloorHeight = v, 777));
            Console.WriteLine($"crash-edit: edited; killing in {after} s; map file {Path.Combine(project.Folder, "maps", "MAP01.wad")}");
            Pump(after);
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            break;
        }

        case "preferences":
            vm.OpenPreferencesCommand.Execute(null);
            Pump(0.3);
            Save("preferences");
            vm.Preferences!.LargeInterface = true;
            Pump(0.3);
            Save("preferences-large");
            vm.Preferences!.LargeInterface = false;
            vm.Preferences!.CloseCommand.Execute(null);
            Pump(0.2);
            break;
        case "editor" or "editor-find" or "editor-brightness" or "editor-heights" or "editor-ceilings" or "editor-lines" or "editor-selected" or "editor-zoomed" or "editor-versions" or "editor-3d" or "editor-walk" or "editor-3d-selected" or "editor-face" or "editor-draw" or "editor-move" or "editor-join" or "editor-delete" or "editor-box" or "editor-door" or "editor-make-door" or "editor-shelves" or "editor-make-lift" or "editor-make-teleport" or "editor-problems" or "editor-draw-shape" or "editor-copy" or "editor-make-stairs" or "editor-turn" or "editor-make-window" or "editor-make-secret" or "editor-make-switch" or "editor-make-exit" or "editor-rooms" or "editor-key" or "editor-make-light" or "editor-measure" or "editor-looks" or "editor-prefab" or "editor-fit" or "editor-borrow" or "editor-arrange" or "editor-guides" or "editor-round" or "editor-mirror" or "editor-door-drag" or "editor-groups" or "editor-flatcal" or "editor-play" or "editor-map-settings" or "editor-3d-drop" or "editor-3d-model" or "editor-3d-picture" or "editor-3d-heights" or "editor-3d-align" or "editor-engine-view" or "editor-fogcal" or "editor-slope" or "editor-climb" or "editor-platform" or "editor-layout" or "editor-palette" or "editor-veteran" or "editor-guide" or "editor-whatisthis" or "sound-check" or "editor-landmark" or "landmark-sheet" or "editor-explain" or "editor-export" or "editor-play-in" or "editor-tour" or "editor-library":
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
            if (pose == "editor-play")
            {
                // The player start taken away and Play pressed: the pre-flight says so; "Fix them and play" adds a start and plays.
                // The engine is WS_ENGINE_PATH (a stand-in) or, only inside the nested session, the one installed: a real engine
                // must never open on the desktop.
                var stand = Environment.GetEnvironmentVariable("WS_ENGINE_PATH");
                if (stand is null && Environment.GetEnvironmentVariable("WADSMITH_NESTED_WORK") is null)
                {
                    Console.Error.WriteLine("editor-play needs WS_ENGINE_PATH (a stand-in engine) outside the nested session.");
                    return 1;
                }

                if (stand is not null) settings.Current.EnginePath = stand;
                var session = editor.Session;
                var start = session.Map.Things.First(t => t.OnMap && t.Type == 1);
                session.History.Record("Delete", e => e.Remove(start));

                // The first wall given a texture the game lacks (what the stand-in engine reports, as GZDoom does).
                var first = session.Map.Lines.First(l => l.OnMap);
                session.History.Record("Plant", e => e.Set(first.Front, x => x.Middle, (x, v) => x.Middle = v, "NOSUCHTX"));
                Pump(1.0);
                editor.PlayCommand.Execute(null);
                for (var i = 0; i < 100 && editor.Preflight is null; i++) Pump(0.05);
                Console.WriteLine($"play preflight: {editor.Preflight}");
                Save("editor-play-preflight");
                editor.FixAndPlayCommand.Execute(null);
                for (var i = 0; i < 100 && !editor.Playing; i++) Pump(0.05);
                Console.WriteLine($"play started: {editor.Playing}; starts {session.Map.Things.Count(t => t.OnMap && t.Type == 1)}; status '{editor.Status}'");
                for (var i = 0; i < 1200 && editor.Playing; i++) Pump(0.05);
                for (var i = 0; i < 100 && session.LastPlay.Count == 0; i++) Pump(0.05);
                Pump(1.0);
                Console.WriteLine($"play back: {session.LastPlay.Count} findings; problems {editor.Problems.Summary}: {string.Join(" | ", editor.Problems.Rows.Select(r => r.Sentence))}");
                Console.WriteLine($"play versions: {string.Join(", ", WadSmith.Editor.MapVersions.List(session.Project, session.Slot).Select(v => v.Reason))}");
                Save("editor-play-back");

                // Play From Here: the 3D view walking from the start, then Shift+F9's command.
                editor.CameraMode = WadSmith.App.ThreeD.CameraMode.Walk;
                Pump(1.0);
                editor.PlayFromHereCommand.Execute(null);
                for (var i = 0; i < 100 && !editor.Playing && editor.Preflight is null; i++) Pump(0.05);
                if (editor.Preflight is not null)
                {
                    // The planted texture is still there: play anyway, from here still.
                    Console.WriteLine($"play from here: pre-flight '{editor.Preflight[..Math.Min(60, editor.Preflight.Length)]}…', playing anyway");
                    editor.PlayAnywayCommand.Execute(null);
                    for (var i = 0; i < 100 && !editor.Playing; i++) Pump(0.05);
                }

                for (var i = 0; i < 1200 && editor.Playing; i++) Pump(0.05);
                var spot = editor.PlaySpot?.Invoke();
                Console.WriteLine($"play from here: the view at {spot?.X:0} {spot?.Y:0} facing {spot?.Angle:0}; status '{editor.Status}'");
                break;
            }

            if (pose == "editor-flatcal")
            {
                // WadSmith's 3D view of the floor-texture maps (WS_FLATCAL: plain, turn30, pan, turnpan, scale .wad and GZDoom's
                // view.png of each), from where GZDoom stood (600 above the middle, looking down), and how far apart they are.
                var folder = Environment.GetEnvironmentVariable("WS_FLATCAL") ?? throw new InvalidOperationException("Set WS_FLATCAL.");
                var resources = editor.Session.Resources;
                WadSmith.Editor.Scene.MapScene Scene(string name)
                {
                    var lumps = WadSmith.Core.Maps.MapLocator.Find(WadSmith.Core.Containers.WadContainer.Open(Path.Combine(folder, name + ".wad"))).First();
                    var calMap = WadSmith.Editor.Model.EditorMap.FromModel(WadSmith.Core.Maps.MapConversion.Read(lumps, "doom"));
                    return WadSmith.Editor.Scene.MapScene.Build(WadSmith.Editor.Geometry.MapGeometry.Build(calMap.Frame), n => resources?.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, WadSmith.App.ThreeD.SpriteShapes.For(resources));
                }

                double Difference(byte[] png, string name)
                {
                    using var ours = SkiaSharp.SKBitmap.Decode(png);
                    using var theirs = SkiaSharp.SKBitmap.Decode(Path.Combine(folder, name, "view.png"));
                    double total = 0;
                    var count = 0;
                    for (var y = 190; y < 430; y += 2)
                    {
                        for (var x = 385; x < 640; x += 2)
                        {
                            var (a, b) = (ours.GetPixel(x, y), theirs.GetPixel(x, y));
                            total += (Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue)) / 3.0;
                            count++;
                        }
                    }

                    return total / count;
                }

                // GZDoom's eye stands 41 above where it put the player (600); its picture's field of view is found on the plain floor.
                var plain = Scene("plain");
                var (bestFov, bestDiff) = (73.75, double.MaxValue);
                for (var fov = 70.0; fov <= 86.0; fov += 0.25)
                {
                    var d = Difference(WadSmith.App.ThreeD.Offscreen3D.RenderPng(plain, resources, new WadSmith.App.ThreeD.Camera3D(0, 0, 641, 90, -89.9, fov), 1024, 640, inside: true), "plain");
                    if (d < bestDiff) (bestFov, bestDiff) = (fov, d);
                }

                Console.WriteLine($"flatcal field of view {bestFov:0.00}: plain floor differs {bestDiff:0.0} per channel");
                foreach (var name in new[] { "plain", "turn30", "pan", "turnpan", "scale", "mirror" })
                {
                    var png = WadSmith.App.ThreeD.Offscreen3D.RenderPng(Scene(name), resources, new WadSmith.App.ThreeD.Camera3D(0, 0, 641, 90, -89.9, bestFov), 1024, 640, inside: true);
                    File.WriteAllBytes(Path.Combine(output, $"ours-{name}.png"), png);
                    Console.WriteLine($"flatcal {name}: mean difference {Difference(png, name):0.0} per channel over the floor");
                }

                // Controls: the plain floor held against GZDoom's turned, moved and scaled ones must differ (the measure can see it).
                var ourPlain = WadSmith.App.ThreeD.Offscreen3D.RenderPng(plain, resources, new WadSmith.App.ThreeD.Camera3D(0, 0, 641, 90, -89.9, bestFov), 1024, 640, inside: true);
                foreach (var other in new[] { "turn30", "pan", "scale", "mirror" }) Console.WriteLine($"flatcal control, plain against {other}: {Difference(ourPlain, other):0.0} per channel");

                // The plan too: the starter room's floor turned 30 through the Inspector.
                editor.Session.Selection.Set(editor.Session.Map.Sectors[0]);
                Pump(0.4);
                editor.Inspector.Rows.OfType<NumberRow>().Single(r => r.Label == "Floor texture turn (°)").Value = 30;
                Pump(1.5);
                Console.WriteLine($"flatcal plan: floor fields {string.Join(", ", editor.Session.Map.Sectors[0].Extra.Select(kv => $"{kv.Key}={kv.Value.ToUdmf()}"))}");
                Save("editor-flatcal-plan");

                break;
            }

            if (pose == "editor-groups")
            {
                // A room east of the starter room, named Courtyard in the Inspector and locked; a click on it picks nothing; a
                // drag of the starter room (sharing its wall) is refused; with nothing selected the Groups rows list it; hidden.
                var session = editor.Session;
                session.DrawRoom([new(256, -256), new(256, 256), new(512, 256), new(512, -256)]);
                Pump(1.0);
                var plan = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
                plan.FitMap();
                Pump(0.4);
                Avalonia.Point Spot(double x, double y) => plan.TranslatePoint(plan.ToScreen(new WadSmith.Editor.Geometry.MapPoint(x, y)), window)!.Value;
                void Click(double x, double y)
                {
                    window.MouseMove(Spot(x, y), Avalonia.Input.RawInputModifiers.None);
                    window.MouseDown(Spot(x, y), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(Spot(x, y), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.4);
                }

                plan.Focus();
                Click(384, 0);
                var name = editor.Inspector.Rows.OfType<TextRow>().Single(r => r.Label == "Group");
                name.Value = "Courtyard";
                name.Commit();
                Pump(0.5);
                Console.WriteLine($"groups named: status '{editor.Status}'");
                editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Lock it").Command.Execute(null);
                Pump(0.5);
                Click(384, 0);
                Console.WriteLine($"groups click on locked: {session.Selection.Count} selected, '{editor.Inspector.Heading}'");
                Click(0, 0);
                window.MouseDown(Spot(0, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                for (var k = 1; k <= 8; k++)
                {
                    window.MouseMove(Spot(0, -16 * k), Avalonia.Input.RawInputModifiers.LeftMouseButton);
                    Pump(0.05);
                }

                window.MouseUp(Spot(0, -128), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                Pump(0.6);
                Console.WriteLine($"groups drag neighbour: status '{editor.Status}'; starter room's corner at {session.Map.Vertices.Where(v => v.OnMap).Min(v => v.Y)}");
                window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, "");
                session.Selection.Clear();
                Pump(1.0);
                Console.WriteLine($"groups overview: rows {string.Join(" | ", editor.Inspector.Rows.Select(r => r.Label))}");
                Save("editor-groups");
                editor.Inspector.Rows.OfType<FlagRow>().Single(r => r.Label == "Courtyard is hidden").Value = true;
                Pump(1.5);
                Save("editor-groups-hidden");
                break;
            }

            if (pose == "editor-3d-picture")
            {
                // A picture of the user's own (WS_PICTURE: a GIF, say) brought in, put on the starter room's north wall and fitted
                // to it, then seen in the real 3D view: one copy across the wall.
                var file = Environment.GetEnvironmentVariable("WS_PICTURE");
                if (file is null || !File.Exists(file))
                {
                    Console.Error.WriteLine("editor-3d-picture needs WS_PICTURE=PATH to a picture.");
                    return 1;
                }

                var session = editor.Session;
                editor.Layout = WadSmith.Editor.App.MapLayout.Split;
                var view3d = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().First();
                for (var i = 0; i < 200 && (view3d.SceneVersion < 0 || !view3d.HasPicture); i++) Pump(0.05);
                var walls = session.Resources!.Textures.Walls.Count();
                session.ImportPicture(file);
                for (var i = 0; i < 400 && session.Resources.Textures.Walls.Count() == walls; i++) Pump(0.05);
                Pump(0.5);
                var texture = session.Project.CopiedNow().First(c => c.Kind == "wall" && c.Game == "picture").Name;
                var north = session.Map.Lines.Where(l => l.OnMap && l.Back is null).MaxBy(l => l.Start.Y + l.End.Y)!;
                var at = new WadSmith.Editor.Geometry.MapPoint((north.Start.X + north.End.X) / 2, (north.Start.Y + north.End.Y) / 2 - 4);
                // A wall already showing the picture across it, fitted (what an earlier build left): WS_BEFORE=fitted.
                if (Environment.GetEnvironmentVariable("WS_BEFORE") == "fitted")
                {
                    session.History.Record("picture", e => e.Set(north.Front, s => s.Middle, (s, v) => s.Middle = v, texture));
                    session.FitWall(north.Front);
                }

                // As the user drops it: the plan's drop, no Shift (WS_DROP=left drops near the wall's west end).
                if (Environment.GetEnvironmentVariable("WS_DROP") == "left") at = new WadSmith.Editor.Geometry.MapPoint(Math.Min(north.Start.X, north.End.X) + 40, at.Y);
                Console.WriteLine($"3d picture: {texture} {session.Resources.Textures.GetWall(texture)?.Width}×{session.Resources.Textures.GetWall(texture)?.Height}; paint '{session.Paint(texture, at, 8, false)}'; status '{editor.Status}'");
                foreach (var l in session.Map.Lines.Where(l => l.OnMap && l.Back is null && Math.Abs(l.Start.Y - north.Start.Y) < 1e-6 && Math.Abs(l.End.Y - north.Start.Y) < 1e-6))
                {
                    Console.WriteLine($"3d piece ({l.Start.X},{l.Start.Y})→({l.End.X},{l.End.Y}) {l.Front.Middle} offsetx {l.Front.OffsetX} fields {string.Join(" ", l.Front.Extra.Select(f => $"{f.Key}={f.Value.AsFloat():0.###}"))}");
                }

                var floor = north.Front.Sector.FloorHeight;
                // Head-on, from inside the room facing north, as the game would show it, once the shapes have caught up.
                for (var i = 0; i < 200 && session.Geometry.Version != session.Map.Version; i++) Pump(0.05);
                var straight = WadSmith.App.ThreeD.Offscreen3D.RenderPng(WadSmith.Editor.Scene.MapScene.Build(session.Geometry, n => session.Resources?.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null), session.Resources, new WadSmith.App.ThreeD.Camera3D(at.X, at.Y - 250, floor + 64, 90, 0), 1280, 800, inside: true);
                File.WriteAllBytes(Path.Combine(output, "editor-3d-picture-straight.png"), straight);
                view3d.LookAt(new WadSmith.Editor.Geometry.MapPoint(at.X, at.Y - 160), floor + 64, 300);
                for (var i = 0; i < 100 && view3d.SceneVersion < session.Map.Version; i++) Pump(0.05);
                Pump(1.5);
                Save("editor-3d-picture");
                // A moving picture moves in the 3D view: the same view a moment later.
                Pump(0.4);
                Save("editor-3d-picture-later");
                break;
            }

            if (pose == "editor-3d-model")
            {
                // A 3D model (WS_MODEL names the file) brought in and placed as a drop on the map does, then seen in the real
                // 3D view beside the plan: the model's own triangles in the view, its baked picture on the plan.
                var file = Environment.GetEnvironmentVariable("WS_MODEL");
                if (file is null || !File.Exists(file))
                {
                    Console.Error.WriteLine("editor-3d-model needs WS_MODEL=PATH to a model file.");
                    return 1;
                }

                var session = editor.Session;
                editor.Layout = WadSmith.Editor.App.MapLayout.Split;
                var view3d = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().First();
                for (var i = 0; i < 200 && (view3d.SceneVersion < 0 || !view3d.HasPicture); i++) Pump(0.05);
                var (x0, y0, x1, y1) = session.Geometry.Extent();
                var spot = new WadSmith.Editor.Geometry.MapPoint(Math.Round((x0 + x1) / 2), Math.Round((y0 + y1) / 2 + 64));
                var before = session.Map.Things.Count(t => t.OnMap);
                session.ImportModel(file, spot);
                for (var i = 0; i < 400 && session.Map.Things.Count(t => t.OnMap) == before; i++) Pump(0.05);
                var prop = session.Map.Things.Last(t => t.OnMap);
                Pump(1.0);
                Console.WriteLine($"3d model: placed {session.Resources?.Things.Find(prop.Type)?.Name} ({prop.Type}) at {prop.X},{prop.Y}; status '{editor.Status}'; problems: {string.Join(" / ", editor.Problems.Rows.Select(r => r.Sentence))}");
                var floor = session.Geometry.AreaAt(spot)?.FloorHeight ?? 0;
                view.FitTo([prop]);
                view.ZoomBy(0.6);
                view3d.LookAt(spot, floor + 28, 160);
                for (var i = 0; i < 100 && view3d.SceneVersion < session.Map.Version; i++) Pump(0.05);
                Pump(1.5);
                Save("editor-3d-model");
                break;
            }

            if (pose == "editor-3d-drop")
            {
                // A wall texture dropped on the starter room's north wall in the 3D view, with real drag events over the
                // view, then an Imp dropped on its floor: the map (both views share it) and the saved file show the same.
                var session = editor.Session;
                var view3d = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().First();
                editor.ShowTextures = true;
                for (var i = 0; i < 200 && (editor.TextureShelf.Tiles.Count == 0 || view3d.SceneVersion < 0 || !view3d.HasPicture); i++) Pump(0.05);
                Pump(0.5);
                Avalonia.Point On(double x, double y, double z) => view3d.TranslatePoint(view3d.ScreenPoint(x, y, z)!.Value, window)!.Value;
                void DropOn(Avalonia.Point at, WadSmith.App.ViewModels.ShelfDrop drop)
                {
                    var data = new Avalonia.Input.DataTransfer();
                    data.Add(Avalonia.Input.DataTransferItem.Create(WadSmith.App.ViewModels.ShelfDrop.Format, drop));
                    foreach (var kind in new[] { Avalonia.Input.Raw.RawDragEventType.DragEnter, Avalonia.Input.Raw.RawDragEventType.DragOver })
                    {
                        window.DragDrop(at, kind, data, Avalonia.Input.DragDropEffects.Copy, Avalonia.Input.RawInputModifiers.None);
                        Pump(0.3);
                    }

                    Console.WriteLine($"3d drag over: '{editor.Status}'; hovered {view3d.Hovered?.Words ?? "nothing"}");
                    window.DragDrop(at, Avalonia.Input.Raw.RawDragEventType.Drop, data, Avalonia.Input.DragDropEffects.Copy, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.8);
                }

                var brick = editor.TextureShelf.Tiles.First(t => t.Group == "Walls" && t.Label.StartsWith("BRICK", StringComparison.Ordinal));
                DropOn(On(0, 256, 64), brick.Drop);
                var north = session.Map.Lines.Single(l => l.OnMap && l.Start.Y == 256 && l.End.Y == 256);
                Console.WriteLine($"3d drop wall: north middle {north.Front.Middle} (dropped {brick.Label}); undo '{session.History.UndoLabel}'; selected '{editor.Inspector.Heading}'");
                Pump(0.5);
                Save("editor-3d-drop-wall");

                editor.ShowObjects = true;
                for (var i = 0; i < 200 && editor.ObjectShelf.Tiles.Count == 0; i++) Pump(0.05);
                var imp = editor.ObjectShelf.Tiles.First(t => t.Label == "Imp");
                DropOn(On(60, -68, 0), imp.Drop);
                var placed = session.Map.Things.Where(t => t.OnMap && t.Type == imp.Drop.Thing).ToList();
                Console.WriteLine($"3d drop thing: {placed.Count} imp(s) at {string.Join(" ", placed.Select(t => $"({t.X},{t.Y})"))}; undo '{session.History.UndoLabel}'");
                Wait(session.FlushAsync());
                var saved = session.Project.LoadMap(session.Slot);
                Console.WriteLine($"3d drop saved: middles {string.Join(",", saved.Sides.Select(s => s.Middle).Distinct())}; things {saved.Things.Count}");
                Pump(0.5);
                Save("editor-3d-drop");
                break;
            }

            if (pose == "editor-3d-heights")
            {
                // Walking in the starter room: Ctrl+scroll three times over the floor ahead raises it to 24 (one undo step);
                // Ctrl+drag on the ceiling ahead, 48 px up, raises the ceiling; the plan's Heights view shows both.
                var session = editor.Session;
                var view3d = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().First();
                for (var i = 0; i < 200 && (view3d.SceneVersion < 0 || !view3d.HasPicture); i++) Pump(0.05);
                editor.CameraMode = WadSmith.App.ThreeD.CameraMode.Walk;
                Pump(1.0);
                var (sx, sy) = view3d.PlaySpot() is var spot ? (spot.X, spot.Y) : (0.0, 0.0);
                var (fx, fy) = (Math.Cos(spot.Angle * Math.PI / 180), Math.Sin(spot.Angle * Math.PI / 180));
                var room = session.Map.Sectors[0];
                Avalonia.Point On(double x, double y, double z) => view3d.TranslatePoint(view3d.ScreenPoint(x, y, z)!.Value, window)!.Value;
                var floorAt = On(sx + fx * 160, sy + fy * 160, room.FloorHeight);
                window.MouseMove(floorAt, Avalonia.Input.RawInputModifiers.None);
                Pump(0.5);
                Console.WriteLine($"3d heights hover: {view3d.Hovered?.Words ?? "nothing"}");
                for (var i = 0; i < 3; i++)
                {
                    window.MouseWheel(floorAt, new Avalonia.Vector(0, 1), Avalonia.Input.RawInputModifiers.Control);
                    Pump(0.3);
                }

                Console.WriteLine($"3d heights wheel: floor {room.FloorHeight}; undo '{session.History.UndoLabel}'; steps {session.History.Steps.Count}; status '{editor.Status}'");
                var ceilingAt = On(sx + fx * 160, sy + fy * 160, room.CeilingHeight);
                window.MouseMove(ceilingAt, Avalonia.Input.RawInputModifiers.None);
                Pump(0.5);
                Console.WriteLine($"3d heights hover: {view3d.Hovered?.Words ?? "nothing"}");
                var before = room.CeilingHeight;
                window.MouseDown(ceilingAt, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
                for (var k = 1; k <= 6; k++)
                {
                    window.MouseMove(new Avalonia.Point(ceilingAt.X, ceilingAt.Y - 8 * k), Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.LeftMouseButton);
                    Pump(0.1);
                }

                window.MouseUp(new Avalonia.Point(ceilingAt.X, ceilingAt.Y - 48), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
                Pump(0.8);
                Console.WriteLine($"3d heights drag: ceiling {room.CeilingHeight} (was {before}); undo '{session.History.UndoLabel}'; steps {session.History.Steps.Count}; status '{editor.Status}'");
                editor.ViewMode = WadSmith.App.Canvas.MapViewMode.Heights;
                Pump(0.5);
                Save("editor-3d-heights");
                session.History.Undo();
                session.History.Undo();
                Console.WriteLine($"3d heights undone twice: floor {room.FloorHeight}, ceiling {room.CeilingHeight}");
                break;
            }

            if (pose == "editor-3d-align")
            {
                // Walking in the starter room with the pointer on the wall ahead: Ctrl+Right twice shifts its texture 16
                // across (one undo step), Ctrl+drag slides it further, Ctrl+A lines the other walls up with it.
                var session = editor.Session;
                var view3d = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().First();
                for (var i = 0; i < 200 && (view3d.SceneVersion < 0 || !view3d.HasPicture); i++) Pump(0.05);
                editor.CameraMode = WadSmith.App.ThreeD.CameraMode.Walk;
                Pump(1.0);
                var spot = view3d.PlaySpot();
                var (fx, fy) = (Math.Cos(spot.Angle * Math.PI / 180), Math.Sin(spot.Angle * Math.PI / 180));
                var t = (256 - spot.Y) / fy;
                Avalonia.Point On(double x, double y, double z) => view3d.TranslatePoint(view3d.ScreenPoint(x, y, z)!.Value, window)!.Value;
                var wallAt = On(spot.X + fx * t, 256, 64);
                window.MouseMove(wallAt, Avalonia.Input.RawInputModifiers.None);
                Pump(0.5);
                var face = view3d.Hovered?.Element as WadSmith.Editor.Model.Side;
                Console.WriteLine($"3d align hover: {view3d.Hovered?.Words ?? "nothing"}; face offsets {face?.OffsetX},{face?.OffsetY}");
                view3d.Focus();
                for (var i = 0; i < 2; i++)
                {
                    window.KeyPress(Avalonia.Input.Key.Right, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.ArrowRight, null);
                    Pump(0.2);
                }

                Console.WriteLine($"3d align keys: offsets {face?.OffsetX},{face?.OffsetY}; undo '{session.History.UndoLabel}'; steps {session.History.Steps.Count}");
                window.MouseDown(wallAt, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
                for (var k = 1; k <= 6; k++)
                {
                    window.MouseMove(new Avalonia.Point(wallAt.X + 10 * k, wallAt.Y), Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.LeftMouseButton);
                    Pump(0.1);
                }

                window.MouseUp(new Avalonia.Point(wallAt.X + 60, wallAt.Y), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
                Pump(0.5);
                Console.WriteLine($"3d align drag: offsets {face?.OffsetX},{face?.OffsetY}; undo '{session.History.UndoLabel}'; steps {session.History.Steps.Count}; status '{editor.Status}'");
                window.MouseMove(wallAt, Avalonia.Input.RawInputModifiers.None);
                Pump(0.5);
                window.KeyPress(Avalonia.Input.Key.A, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.A, null);
                Pump(0.5);
                Console.WriteLine($"3d align all: {string.Join(" ", session.Map.Lines.Where(l => l.OnMap).Select(l => $"{l.Front.OffsetX},{l.Front.OffsetY}"))}; undo '{session.History.UndoLabel}'; status '{editor.Status}'");
                Save("editor-3d-align");
                break;
            }

            if (pose == "editor-engine-view")
            {
                // Walking in the starter room, the game's view turned on: GZDoom (the stand-in outside the nested session) draws
                // its picture from the camera's spot on a private screen, shown over the 3D view; with the real engine, the
                // engine's picture is held against our own render from the same camera (and a control: ours turned 90°).
                var stand = Environment.GetEnvironmentVariable("WS_ENGINE_PATH");
                if (stand is null && Environment.GetEnvironmentVariable("WADSMITH_NESTED_WORK") is null)
                {
                    Console.Error.WriteLine("editor-engine-view needs WS_ENGINE_PATH (a stand-in engine) outside the nested session.");
                    return 1;
                }

                if (stand is not null) settings.Current.EnginePath = stand;
                var session = editor.Session;
                var view3d = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().First();
                for (var i = 0; i < 200 && (view3d.SceneVersion < 0 || !view3d.HasPicture); i++) Pump(0.05);
                editor.CameraMode = WadSmith.App.ThreeD.CameraMode.Walk;
                Pump(1.0);
                var started = System.Diagnostics.Stopwatch.StartNew();
                editor.EngineView = true;
                for (var i = 0; i < 1800 && editor.EngineViewPicture is null && !editor.EngineViewNote.Contains("not", StringComparison.Ordinal) && !editor.EngineViewNote.Contains("stopped", StringComparison.Ordinal); i++) Pump(0.1);
                Console.WriteLine($"engine view: picture {(editor.EngineViewPicture is { } b ? $"{b.PixelSize.Width}x{b.PixelSize.Height}" : "none")} after {started.Elapsed.TotalSeconds:0.0} s; note '{editor.EngineViewNote}'");
                Pump(0.5);
                Save("editor-engine-view");
                if (editor.EngineViewPicture is not null && stand is null && view3d.Scene is { } scene)
                {
                    var cam = view3d.CurrentCamera;
                    var theirsPath = Path.Combine(paths.Data, "engine", "view", "view.png");
                    using var theirs = SkiaSharp.SKBitmap.Decode(theirsPath);
                    var (w, h) = (theirs.Width, theirs.Height);
                    var ours = WadSmith.App.ThreeD.Offscreen3D.RenderPng(scene, session.Resources, cam, w, h, inside: true);
                    var turned = WadSmith.App.ThreeD.Offscreen3D.RenderPng(scene, session.Resources, cam with { Yaw = cam.Yaw + 90 }, w, h, inside: true);
                    File.WriteAllBytes(Path.Combine(output, "editor-engine-view-ours.png"), ours);
                    File.Copy(theirsPath, Path.Combine(output, "editor-engine-view-theirs.png"), overwrite: true);
                    double Against(byte[] png)
                    {
                        using var mine = SkiaSharp.SKBitmap.Decode(png);
                        double total = 0;
                        var count = 0;
                        for (var y = 0; y < h; y += 2)
                        {
                            for (var x = 0; x < w; x += 2)
                            {
                                var (a, c) = (mine.GetPixel(x, y), theirs.GetPixel(x, y));
                                total += (Math.Abs(a.Red - c.Red) + Math.Abs(a.Green - c.Green) + Math.Abs(a.Blue - c.Blue)) / 3.0;
                                count++;
                            }
                        }

                        return total / count;
                    }

                    Console.WriteLine($"engine view measured: ours against the engine {Against(ours):0.0} per channel; control (ours turned 90°) {Against(turned):0.0}");
                }

                editor.EngineView = false;
                Pump(0.5);
                Console.WriteLine($"engine view off: picture {(editor.EngineViewPicture is null ? "gone" : "still there")}");
                break;
            }

            if (pose == "editor-fogcal")
            {
                // Our 3D view of the fog-calibration corridors (WS_FOGCAL: L{light}F{fade}.wad with GZDoom's view.png beside each,
                // the player at (0, −1900) looking north) from the same spot, searching the lighting constants that bring ours
                // nearest the engine's: globVis on the unfogged corridors, then the fog density's base and darkness terms.
                var folder = Environment.GetEnvironmentVariable("WS_FOGCAL") ?? throw new InvalidOperationException("Set WS_FOGCAL.");
                var resources = editor.Session.Resources;
                WadSmith.Editor.Scene.MapScene Scene(string name)
                {
                    var lumps = WadSmith.Core.Maps.MapLocator.Find(WadSmith.Core.Containers.WadContainer.Open(Path.Combine(folder, name + ".wad"))).First();
                    var calMap = WadSmith.Editor.Model.EditorMap.FromModel(WadSmith.Core.Maps.MapConversion.Read(lumps, "doom"));
                    return WadSmith.Editor.Scene.MapScene.Build(WadSmith.Editor.Geometry.MapGeometry.Build(calMap.Frame), n => resources?.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, WadSmith.App.ThreeD.SpriteShapes.For(resources));
                }

                var camera = new WadSmith.App.ThreeD.Camera3D(0, -1900, 41, 90, 0);
                var cache = new Dictionary<string, SkiaSharp.SKBitmap>();

                // Pictures compared as 8-pixel blocks (their mean colour), so the engine's texture filtering against our
                // nearest-pixel drawing does not drown what is measured: how bright each part of the corridor is.
                double Diff(byte[] png, string name)
                {
                    using var ours = SkiaSharp.SKBitmap.Decode(png);
                    if (!cache.TryGetValue(name, out var theirs)) cache[name] = theirs = SkiaSharp.SKBitmap.Decode(Path.Combine(folder, name, "view.png"));
                    double total = 0;
                    var count = 0;
                    for (var y = 60; y < 380; y += 8)
                    {
                        for (var x = 200; x < 824; x += 8)
                        {
                            double ar = 0, ag = 0, ab = 0, br = 0, bg = 0, bb = 0;
                            for (var j = 0; j < 8; j++)
                            {
                                for (var i = 0; i < 8; i++)
                                {
                                    var (a, b) = (ours.GetPixel(x + i, y + j), theirs.GetPixel(x + i, y + j));
                                    (ar, ag, ab, br, bg, bb) = (ar + a.Red, ag + a.Green, ab + a.Blue, br + b.Red, bg + b.Green, bb + b.Blue);
                                }
                            }

                            total += (Math.Abs(ar - br) + Math.Abs(ag - bg) + Math.Abs(ab - bb)) / 3.0 / 64;
                            count++;
                        }
                    }

                    return total / count;
                }

                // The engine's field of view first, on the bright corridor (no light falls off at 255): the vertical angle that frames it as the engine does.
                var brightScene = Scene("L255F0");
                var (bestFov, bestFovDiff) = (73.75, double.MaxValue);
                for (var fov = 66.0; fov <= 92; fov += 1)
                {
                    var d = Diff(WadSmith.App.ThreeD.Offscreen3D.RenderPng(brightScene, resources, camera with { Fov = fov }, 1024, 640, true, new WadSmith.App.ThreeD.Lighting(64, 10, 1)), "L255F0");
                    if (d < bestFovDiff) (bestFov, bestFovDiff) = (fov, d);
                }

                for (var fov = bestFov - 1; fov <= bestFov + 1; fov += 0.25)
                {
                    var d = Diff(WadSmith.App.ThreeD.Offscreen3D.RenderPng(brightScene, resources, camera with { Fov = fov }, 1024, 640, true, new WadSmith.App.ThreeD.Lighting(64, 10, 1)), "L255F0");
                    if (d < bestFovDiff) (bestFov, bestFovDiff) = (fov, d);
                }

                Console.WriteLine($"fogcal fov best {bestFov:0.00}: {bestFovDiff:0.0} per channel on the bright corridor (73.75, the default: {Diff(WadSmith.App.ThreeD.Offscreen3D.RenderPng(brightScene, resources, camera, 1024, 640, true, new WadSmith.App.ThreeD.Lighting(64, 10, 1)), "L255F0"):0.0})");
                camera = camera with { Fov = bestFov };

                int[] levels = [255, 192, 128, 64];
                var plain = levels.Select(l => (Name: $"L{l}F0", Scene: Scene($"L{l}F0"))).ToList();
                var fogged = levels.Select(l => (Name: $"L{l}F6324416", Scene: Scene($"L{l}F6324416"))).ToList();
                double Mean(IEnumerable<(string Name, WadSmith.Editor.Scene.MapScene Scene)> set, WadSmith.App.ThreeD.Lighting light) => set.Average(s => Diff(WadSmith.App.ThreeD.Offscreen3D.RenderPng(s.Scene, resources, camera, 1024, 640, inside: true, light), s.Name));

                var (bestVis, bestPlain) = (0.0, double.MaxValue);
                for (var vis = 4.0; vis <= 2048; vis *= Math.Sqrt(2))
                {
                    var d = Mean(plain, new WadSmith.App.ThreeD.Lighting(vis, 100, 2));
                    Console.WriteLine($"fogcal globVis {vis:0}: {d:0.0} per channel");
                    if (d < bestPlain) (bestVis, bestPlain) = (vis, d);
                }

                for (var vis = bestVis / 1.35; vis <= bestVis * 1.35; vis *= 1.07)
                {
                    var d = Mean(plain, new WadSmith.App.ThreeD.Lighting(vis, 100, 2));
                    if (d < bestPlain) (bestVis, bestPlain) = (vis, d);
                }

                Console.WriteLine($"fogcal globVis best {bestVis:0}: {bestPlain:0.0} per channel; each: {string.Join(", ", plain.Select(s => $"{s.Name} {Diff(WadSmith.App.ThreeD.Offscreen3D.RenderPng(s.Scene, resources, camera, 1024, 640, true, new WadSmith.App.ThreeD.Lighting(bestVis, 100, 2)), s.Name):0.0}"))}");
                Console.WriteLine($"fogcal control: L255 ours against L64 theirs {Diff(WadSmith.App.ThreeD.Offscreen3D.RenderPng(plain[0].Scene, resources, camera, 1024, 640, true, new WadSmith.App.ThreeD.Lighting(bestVis, 100, 2)), "L64F0"):0.0}; globVis ×4 {Mean(plain, new WadSmith.App.ThreeD.Lighting(bestVis * 4, 100, 2)):0.0}");

                var (bestBase, bestDark, bestFog) = (0.0, 0.0, double.MaxValue);
                foreach (var b in new[] { 0.0, 25, 50, 100, 150, 200, 300, 400 })
                {
                    foreach (var k in new[] { 0.0, 0.25, 0.5, 1, 1.5, 2, 3, 4, 6 })
                    {
                        var d = Mean(fogged, new WadSmith.App.ThreeD.Lighting(bestVis, b, k));
                        if (d < bestFog) (bestBase, bestDark, bestFog) = (b, k, d);
                    }
                }

                for (var b = Math.Max(0, bestBase - 40); b <= bestBase + 40; b += 10)
                {
                    for (var k = Math.Max(0, bestDark - 0.5); k <= bestDark + 0.5; k += 0.1)
                    {
                        var d = Mean(fogged, new WadSmith.App.ThreeD.Lighting(bestVis, b, k));
                        if (d < bestFog) (bestBase, bestDark, bestFog) = (b, k, d);
                    }
                }

                var fit = new WadSmith.App.ThreeD.Lighting(bestVis, bestBase, bestDark);
                Console.WriteLine($"fogcal fog best base {bestBase:0} dark {bestDark:0.0}: {bestFog:0.0} per channel; each: {string.Join(", ", fogged.Select(s => $"{s.Name} {Diff(WadSmith.App.ThreeD.Offscreen3D.RenderPng(s.Scene, resources, camera, 1024, 640, true, fit), s.Name):0.0}"))}");
                Console.WriteLine($"fogcal fog control: no fog term {Mean(fogged, new WadSmith.App.ThreeD.Lighting(bestVis, 0, 0)):0.0}; density ×3 {Mean(fogged, new WadSmith.App.ThreeD.Lighting(bestVis, bestBase * 3, bestDark * 3)):0.0}");
                foreach (var s in plain.Concat(fogged)) File.WriteAllBytes(Path.Combine(output, $"fogcal-{s.Name}-ours.png"), WadSmith.App.ThreeD.Offscreen3D.RenderPng(s.Scene, resources, camera, 1024, 640, true, fit));
                Console.WriteLine($"fogcal measured: {fit}");
                break;
            }

            if (pose == "editor-climb")
            {
                // D27: a corridor drawn east of the starter room (floor 0) to a room raised to 64. The Inspector offers steps and a
                // ramp; the ramp is made and seen, taken back, and steps made instead.
                var session = editor.Session;
                var view3d = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().First();
                for (var i = 0; i < 200 && (view3d.SceneVersion < 0 || !view3d.HasPicture); i++) Pump(0.05);
                var told = new List<string>();
                session.Notice += told.Add;
                session.DrawRoom([new(256, -64), new(256, 64), new(384, 64), new(384, -64)]);
                Pump(0.5);
                session.DrawRoom([new(384, -192), new(384, 192), new(640, 192), new(640, -192)]);
                Pump(0.5);
                var high = session.Geometry.SectorAt(new WadSmith.Editor.Geometry.MapPoint(512, 0))!;
                session.History.Record("Raise", e =>
                {
                    e.Set(high, x => x.FloorHeight, (x, v) => x.FloorHeight = v, 64);
                    e.Set(high, x => x.CeilingHeight, (x, v) => x.CeilingHeight = v, 192);
                });
                Pump(0.8);
                var corridor = session.Geometry.SectorAt(new WadSmith.Editor.Geometry.MapPoint(320, 0))!;
                session.Selection.Set(corridor);
                Pump(0.8);
                string Offer() => string.Join(" | ", editor.Inspector.Rows.Where(r => r is ActionRow { Label: "Add steps" or "Make a ramp" } || r is InfoRow { Label: "Joins floors" }).Select(r => r is InfoRow info ? $"{r.Label}: {info.Value}" : r.Label));
                Console.WriteLine($"climb offered: {Offer()}");
                Save("editor-climb");
                editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Make a ramp").Command.Execute(null);
                Pump(1.0);
                var plane = WadSmith.Editor.Scene.SlopePlane.Floor(corridor);
                Console.WriteLine($"ramp: floor {plane?.Z(256, 0):0.##} at the low room, {plane?.Z(384, 0):0.##} at the high one, ceiling {corridor.CeilingHeight}; undo '{session.History.UndoLabel}'; offered now '{Offer()}'; told '{told.LastOrDefault()}'");
                Save("editor-climb-ramp");
                session.History.Undo();
                Pump(0.8);
                session.Selection.Set(corridor);
                Pump(0.5);
                editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Add steps").Command.Execute(null);
                Pump(1.0);
                Console.WriteLine($"steps: {string.Join(" ", session.Selection.Items.OfType<WadSmith.Editor.Model.Sector>().Select(x => x.FloorHeight).Order())}; undo '{session.History.UndoLabel}'; {editor.Problems.Summary}; told '{told.LastOrDefault()}'");
                Save("editor-climb-steps");
                break;
            }

            if (pose == "editor-slope")
            {
                // The Ramp tool through the Inspector on the starter room (floor sloped from the south wall to 64 at the north), seen
                // walking; then our view of the ramp corridor (WS_SLOPECAL: ramp.wad, flat.wad with GZDoom's view.png beside each)
                // against the engine's, with the flat corridor as the control.
                var session = editor.Session;
                var view3d = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().First();
                for (var i = 0; i < 200 && (view3d.SceneVersion < 0 || !view3d.HasPicture); i++) Pump(0.05);
                var room = session.Map.Sectors[0];
                session.Selection.Set(room);
                Pump(0.5);
                var from = editor.Inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Floor slopes from");
                Console.WriteLine($"slope choices: {string.Join(" | ", from.Options)}");
                from.Value = from.Options.ToList().IndexOf("the south wall");
                Pump(0.5);
                editor.Inspector.Rows.OfType<NumberRow>().Single(r => r.Label == "Floor reaches").Value = 64;
                Pump(0.5);
                var plane = WadSmith.Editor.Scene.SlopePlane.Floor(room);
                Console.WriteLine($"slope set: floor at the south wall {plane?.Z(0, -256):0.##}, at the north {plane?.Z(0, 256):0.##}; undo '{session.History.UndoLabel}'; rows say from '{from.Options[from.Value]}' reaching {editor.Inspector.Rows.OfType<NumberRow>().Single(r => r.Label == "Floor reaches").Value}");
                editor.CameraMode = WadSmith.App.ThreeD.CameraMode.Walk;
                Pump(1.5);
                Save("editor-slope");

                if (Environment.GetEnvironmentVariable("WS_SLOPECAL") is { } folder)
                {
                    var resources = session.Resources;
                    (WadSmith.Editor.Scene.MapScene Scene, WadSmith.Editor.Geometry.MapGeometry Geometry) Scene(string name)
                    {
                        var lumps = WadSmith.Core.Maps.MapLocator.Find(WadSmith.Core.Containers.WadContainer.Open(Path.Combine(folder, name + ".wad"))).First();
                        var calMap = WadSmith.Editor.Model.EditorMap.FromModel(WadSmith.Core.Maps.MapConversion.Read(lumps, "doom"));
                        var geometry = WadSmith.Editor.Geometry.MapGeometry.Build(calMap.Frame);
                        return (WadSmith.Editor.Scene.MapScene.Build(geometry, n => resources?.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, WadSmith.App.ThreeD.SpriteShapes.For(resources)), geometry);
                    }

                    double Diff(byte[] png, string name)
                    {
                        using var ours = SkiaSharp.SKBitmap.Decode(png);
                        using var theirs = SkiaSharp.SKBitmap.Decode(Path.Combine(folder, name, "view.png"));
                        double total = 0;
                        var count = 0;
                        for (var y = 60; y < 380; y += 8)
                        {
                            for (var x = 200; x < 824; x += 8)
                            {
                                double ar = 0, ag = 0, ab = 0, br = 0, bg = 0, bb = 0;
                                for (var j = 0; j < 8; j++)
                                {
                                    for (var i = 0; i < 8; i++)
                                    {
                                        var (a, b) = (ours.GetPixel(x + i, y + j), theirs.GetPixel(x + i, y + j));
                                        (ar, ag, ab, br, bg, bb) = (ar + a.Red, ag + a.Green, ab + a.Blue, br + b.Red, bg + b.Green, bb + b.Blue);
                                    }
                                }

                                total += (Math.Abs(ar - br) + Math.Abs(ag - bg) + Math.Abs(ab - bb)) / 3.0 / 64;
                                count++;
                            }
                        }

                        return total / count;
                    }

                    // The player stands at (0, −200) on the floor there (on the ramp, higher than on the flat), eye 41 above.
                    var (ramp, rampGeometry) = Scene("ramp");
                    var (flat, flatGeometry) = Scene("flat");
                    var spot = new WadSmith.Editor.Geometry.MapPoint(0, -200);
                    WadSmith.App.ThreeD.Camera3D Eye(WadSmith.Editor.Geometry.MapGeometry g) => new(0, -200, WadSmith.Editor.Scene.SlopePlane.FloorAt(g.AreaAt(spot)!.Value, spot) + WadSmith.Editor.Scene.Walker.EyeHeight, 90, 0);
                    Console.WriteLine($"slope eyes: ramp {Eye(rampGeometry).Z:0.##}, flat {Eye(flatGeometry).Z:0.##}");
                    var rampOurs = WadSmith.App.ThreeD.Offscreen3D.RenderPng(ramp, resources, Eye(rampGeometry), 1024, 640, inside: true);
                    var flatOurs = WadSmith.App.ThreeD.Offscreen3D.RenderPng(flat, resources, Eye(flatGeometry), 1024, 640, inside: true);
                    File.WriteAllBytes(Path.Combine(output, "slope-ramp-ours.png"), rampOurs);
                    File.WriteAllBytes(Path.Combine(output, "slope-flat-ours.png"), flatOurs);
                    Console.WriteLine($"slope measured: ramp ours against the engine's ramp {Diff(rampOurs, "ramp"):0.0} per channel; flat against flat {Diff(flatOurs, "flat"):0.0}; controls: flat ours against the engine's ramp {Diff(flatOurs, "ramp"):0.0}, ramp ours against the engine's flat {Diff(rampOurs, "flat"):0.0}");
                }

                break;
            }

            if (pose == "editor-guide")
            {
                // The guided first map: its card and outline on step 1, then step 3 after a room and the 3D view, then the end.
                editor.StartGuideCommand.Execute(null);
                Pump(0.8);
                Console.WriteLine($"guide: {editor.Guide?.StepLabel} {editor.Guide?.Title}; marks room {editor.Marks.Room}");
                Save("editor-guide-1");
                var session = editor.Session;
                var xs = session.Map.Vertices.Where(v => v.OnMap).Select(v => v.X).ToList();
                var ys = session.Map.Vertices.Where(v => v.OnMap).Select(v => v.Y).ToList();
                var (x1, y0, y1) = (xs.Max(), ys.Min(), ys.Max());
                session.DrawRoom([new(x1, y0), new(x1 + 256, y0), new(x1 + 256, y1), new(x1, y1)]);
                Pump(0.8);
                editor.Layout = WadSmith.Editor.App.MapLayout.ThreeD;
                Pump(0.5);
                editor.Layout = WadSmith.Editor.App.MapLayout.Split;
                Pump(0.8);
                Console.WriteLine($"guide: {editor.Guide?.StepLabel} {editor.Guide?.Title}; marks door {editor.Marks.Door}");
                Save("editor-guide-3");
                for (var i = 0; i < 4; i++) editor.Guide!.SkipCommand.Execute(null);
                Pump(0.5);
                Save("editor-guide-7");
                editor.Guide!.SkipCommand.Execute(null);
                editor.Guide!.SkipCommand.Execute(null);
                editor.Guide = null;
                editor.StartGuideCommand.Execute(null);
                for (var i = 0; i < 3; i++) editor.Guide!.SkipCommand.Execute(null);
                Pump(0.5);
                Save("editor-guide-4");
                editor.Guide = null;
                editor.StartGuideCommand.Execute(null);
                for (var i = 0; i < 6; i++) editor.Guide!.SkipCommand.Execute(null);
                Pump(0.3);
                Pump(0.3);
                editor.Guide!.Happened(WadSmith.Editor.App.GuideEvents.Played);
                Pump(0.8);
                Console.WriteLine($"guide: {editor.Guide?.StepLabel} {editor.Guide?.Title}");
                Save("editor-guide-done");

                // A chapter from the finished card, on its second step (the Inspector outlined), then its own finished card.
                editor.Guide!.Next.First(l => l.Title == "Doors, keys and switches").Open.Execute(null);
                editor.Guide!.SkipCommand.Execute(null);
                Pump(0.5);
                Console.WriteLine($"guide: {editor.Guide?.StepLabel} {editor.Guide?.Title}; marks inspector {editor.Marks.Inspector}");
                Save("editor-guide-chapter");
                for (var i = 0; i < 3; i++) editor.Guide!.SkipCommand.Execute(null);
                Pump(0.5);
                Console.WriteLine($"guide: {editor.Guide?.StepLabel} {editor.Guide?.Title}; next {string.Join(", ", editor.Guide!.Next.Select(l => l.Title))}");
                Save("editor-guide-chapter-done");

                // The Guide menu, opened from its button.
                editor.Guide = null;
                var guideButton = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().First(b => b.Content is "Guide");
                guideButton.Flyout!.ShowAt(guideButton);
                Pump(0.5);
                Save("editor-guide-menu");
                guideButton.Flyout.Hide();
                break;
            }

            if (pose == "editor-whatisthis")
            {
                // "What is this?" turned on with Shift+F1, then a real pointer over the plan's room and over the 3D view's wall.
                var plan = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
                var view3 = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().First();
                Avalonia.Point At(Avalonia.Visual v, double fx, double fy) => v.TranslatePoint(new Avalonia.Point(v.Bounds.Width * fx, v.Bounds.Height * fy), window)!.Value;
                plan.Focus();
                window.KeyPress(Avalonia.Input.Key.F1, Avalonia.Input.RawInputModifiers.Shift, Avalonia.Input.PhysicalKey.F1, null);
                window.MouseMove(At(plan, 0.5, 0.5), Avalonia.Input.RawInputModifiers.None);
                Pump(0.8);
                Console.WriteLine($"what is this: {editor.Explained?.Heading}");
                Save("editor-whatisthis-plan");
                window.MouseMove(At(view3, 0.85, 0.5), Avalonia.Input.RawInputModifiers.None);
                Pump(0.8);
                Console.WriteLine($"what is this: {editor.Explained?.Heading}");
                Save("editor-whatisthis-3d");
                break;
            }

            if (pose == "sound-check")
            {
                // The SDL player end to end, silently: SDL's dummy driver consumes the samples and nothing reaches a speaker.
                // Refuses to run without the dummy driver and dead sound servers in the environment.
                if (Environment.GetEnvironmentVariable("SDL_AUDIODRIVER") != "dummy" || Environment.GetEnvironmentVariable("SDL_AUDIO_DRIVER") != "dummy"
                    || Environment.GetEnvironmentVariable("PULSE_SERVER") is not { Length: > 0 } || Environment.GetEnvironmentVariable("PIPEWIRE_REMOTE") is not { Length: > 0 })
                {
                    Console.WriteLine("sound-check refuses: set SDL_AUDIODRIVER=dummy SDL_AUDIO_DRIVER=dummy PULSE_SERVER=unix:/nonexistent PIPEWIRE_REMOTE=/nonexistent");
                    break;
                }

                using var player = WadSmith.App.Audio.SdlSoundPlayer.TryCreate();
                var wav = editor.Session.Resources?.SoundFor(3001);
                if (player is null || wav is null)
                {
                    Console.WriteLine($"sound-check: player {(player is null ? "missing" : "loaded")}, imp sound {(wav is null ? "missing" : "found")}");
                    break;
                }

                player.Play(wav);
                var queued = 0u;
                for (var i = 0; i < 50 && player.Driver.Length == 0; i++) Thread.Sleep(20);
                queued = player.QueuedBytes;
                Thread.Sleep(1500);
                Console.WriteLine($"sound-check: driver '{player.Driver}', queued {queued} bytes of {WadSmith.App.Audio.SdlSoundPlayer.Samples(wav).Length * 2}, left after 1.5 s {player.QueuedBytes}");
                break;
            }

            if (pose == "editor-landmark")
            {
                // See it in a real map (D28): the Door tool's "See one" button, the game's own map opened at a door, an edit
                // refused there, and back to the map it came from.
                editor.Tool = WadSmith.App.Canvas.EditorTool.Door;
                Pump(0.5);
                Console.WriteLine($"landmark: button '{editor.SeeOne?.Title}'");
                Save("editor-landmark-button");
                editor.SeeOne!.Open.Execute(null);
                for (var i = 0; i < 300 && vm.Page == editor; i++) Pump(0.05);
                Pump(2.0);
                var peek = (WadSmith.App.ViewModels.EditorViewModel)vm.Page!;
                Console.WriteLine($"landmark: {peek.Peek?.Heading} · {peek.Peek?.Landmark?.Kind} at {peek.Peek?.Landmark?.X}, {peek.Peek?.Landmark?.Y}; selected {peek.Session.Selection.Items.FirstOrDefault()?.GetType().Name}; read-only {peek.Session.History.ReadOnly}");
                Save("editor-landmark-peek");
                peek.Session.Paint("BROWN1", peek.Peek!.Landmark!.At, 8, ceiling: false);
                Pump(0.3);
                Console.WriteLine($"landmark: after an edit, undo {peek.Session.History.CanUndo}; status '{peek.Status}'");
                peek.BackFromPeekCommand.Execute(null);
                Pump(1.0);
                Console.WriteLine($"landmark: back on {((WadSmith.App.ViewModels.EditorViewModel)vm.Page!).Session.Project.Info.Name}, same editor {ReferenceEquals(vm.Page, editor)}");
                Save("editor-landmark-back");
                break;
            }

            if (pose == "landmark-sheet")
            {
                // Every landmark (D28) of the games found, opened as the editor opens it, one picture each: the eyeball pass
                // of the phase proof. Pictures are named game-map-kind.
                var services = WadSmith.App.App.Services!;
                foreach (var l in WadSmith.Editor.Smart.Landmarks.All)
                {
                    if ((services.Games ?? []).FirstOrDefault(g => WadSmith.Editor.Smart.Landmarks.IndexId(g.Game.Id) == l.Game && !g.Game.Id.EndsWith("-shareware", StringComparison.Ordinal)) is not { } game) continue;
                    var opening = vm.PeekAsync(l, game, editor);
                    while (!opening.IsCompleted) Pump(0.05);
                    Pump(1.5);
                    if (vm.Page is WadSmith.App.ViewModels.EditorViewModel { Peek: not null } peek)
                    {
                        Save($"landmark-{l.Game}-{l.Map}-{l.Kind.Replace(' ', '-')}");
                        vm.BackFromPeek(peek);
                        Pump(0.2);
                    }
                    else
                    {
                        Console.WriteLine($"landmark-sheet: {l.Game} {l.Map} {l.Kind} did not open");
                    }
                }

                break;
            }

            if (pose == "editor-play-in")
            {
                // Play's arrow (Phase 12) with its Play in list open: GZDoom and the classic ports installed for this game.
                for (var i = 0; i < 100 && editor.PlayInMenu.Count < 2; i++) Pump(0.05);
                var split = window.GetVisualDescendants().OfType<Avalonia.Controls.SplitButton>().First();
                split.Flyout!.ShowAt(split);
                Pump(0.3);
                var playIn = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(Avalonia.Controls.TopLevel.GetTopLevel(split)!).OfType<Avalonia.Controls.MenuItem>()
                    .Concat(window.GetVisualDescendants().OfType<Avalonia.Controls.MenuItem>())
                    .FirstOrDefault(m => m.Header as string == "Play in");
                foreach (var popup in Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(split)?.GetVisualDescendants().OfType<Avalonia.Controls.MenuItem>() ?? [])
                {
                    if (popup.Header as string == "Play in") playIn = popup;
                }

                playIn?.Open();
                Pump(0.5);
                Console.WriteLine($"play in: {string.Join(" | ", editor.PlayInMenu.Select(c => c.Header.Trim()))}; menu found {playIn is not null}");
                Save("editor-play-in");
                break;
            }

            if (pose == "editor-export")
            {
                // Export (Phase 12) from the Inspector with nothing selected: first for GZDoom, then for vanilla with a room's
                // light coloured (vanilla has no coloured light) and a dynamic light, so the card lists what would be lost.
                editor.Session.Selection.Clear();
                var lit = editor.Session.Map.Sectors.First(s => s.OnMap);
                editor.Session.History.Record("Colour the light", e => e.Field(lit, "lightcolor", WadSmith.Core.Maps.Udmf.UdmfValue.Int(0xFF6040)));
                // And one of GZDoom's dynamic lights, an object vanilla does not know: it stops the game, said in the danger colour.
                editor.Session.History.Record("Add a light", e => e.Add(new WadSmith.Editor.Model.Thing { X = 64, Y = 64, Type = 9830, Angle = 90 }));
                Pump(0.3);
                editor.Inspector.Rows.OfType<WadSmith.App.ViewModels.ActionRow>().Single(r => r.Label == "Export…").Command.Execute(null);
                for (var i = 0; i < 200 && (editor.Export is null || editor.Export.Busy); i++) Pump(0.05);
                Pump(0.5);
                Console.WriteLine($"export: {editor.Export?.Selected.Title}: {editor.Export?.Summary}");
                Save("editor-export");
                editor.Export!.Selected = editor.Export.Choices.Single(c => c.Id == "vanilla");
                for (var i = 0; i < 200 && editor.Export.Busy; i++) Pump(0.05);
                Pump(0.5);
                Console.WriteLine($"export: {editor.Export.Selected.Title}: {editor.Export.Summary} {string.Join(" | ", editor.Export.Lines.Select(l => l.Title))}");
                Save("editor-export-vanilla");
                break;
            }

            if (pose == "editor-explain")
            {
                // Explain this map (D32) from the Inspector with nothing selected, then one of its Show buttons.
                editor.Session.Selection.Clear();
                Pump(0.3);
                editor.Inspector.Rows.OfType<WadSmith.App.ViewModels.ActionRow>().Single(r => r.Label == "Explain this map").Command.Execute(null);
                for (var i = 0; i < 200 && editor.Report is null; i++) Pump(0.05);
                Pump(0.5);
                Console.WriteLine($"explain: {editor.Report?.Summary}");
                foreach (var s in editor.Report?.Sections ?? []) Console.WriteLine($"explain: {s.Heading}: {string.Join(" | ", s.Items.Select(i => i.Text))}");
                Save("editor-explain");
                foreach (var sv in window.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().Where(s => s.FindAncestorOfType<Avalonia.Controls.ItemsControl>() is null && s.Content is Avalonia.Controls.ItemsControl { ItemsSource: System.Collections.Generic.IReadOnlyList<WadSmith.App.ViewModels.ReportSectionView> })) sv.ScrollToEnd();
                Pump(0.3);
                Save("editor-explain-end");
                editor.Report!.Sections.SelectMany(s => s.Items).First(i => i.Show is not null && i.Text.Contains("secret", StringComparison.Ordinal)).Show!.Execute(null);
                Pump(1.5);
                Save("editor-explain-show");
                break;
            }

            if (pose == "editor-tour")
            {
                // The feature tour (D32) from the Guide menu, read-only, its signs on the plan's rooms.
                editor.OpenTourCommand.Execute(null);
                for (var i = 0; i < 300 && vm.Page == editor; i++) Pump(0.05);
                Pump(2.0);
                var tour = (WadSmith.App.ViewModels.EditorViewModel)vm.Page!;
                Console.WriteLine($"tour: {tour.Peek?.Heading}; {tour.Session.Map.Sectors.Count(s => s.OnMap)} rooms");
                Save("editor-tour");
                break;
            }

            if (pose == "editor-veteran")
            {
                // Preferences → classic names and Doom Builder keys, then the editor in them: a room selected, S (sectors
                // only), W (the 3D view alone) and back, pressed as real keys.
                vm.OpenPreferencesCommand.Execute(null);
                Pump(0.3);
                vm.Preferences!.ClassicNames = true;
                vm.Preferences!.DoomBuilderKeys = true;
                Save("editor-veteran-preferences");
                vm.Preferences!.CloseCommand.Execute(null);
                Pump(0.3);
                var canvas = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
                canvas.Focus();
                editor.Session.Selection.Set(editor.Session.Map.Sectors[0]);
                window.KeyPress(Avalonia.Input.Key.S, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.S, "s");
                Pump(0.5);
                Console.WriteLine($"veteran: heading '{editor.Inspector.Heading}'; select '{editor.SelectLabel}'; room tool '{editor.Faces.Room.Label}' tip '{editor.Faces.Room.Tip}'; play '{editor.Faces.Play.Label}'");
                Save("editor-veteran");
                window.KeyPress(Avalonia.Input.Key.W, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.W, "w");
                Pump(0.4);
                Console.WriteLine($"veteran W: layout {editor.Layout}");
                window.KeyPress(Avalonia.Input.Key.W, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.W, "w");
                Pump(0.4);
                Console.WriteLine($"veteran W again: layout {editor.Layout}; status '{editor.Status}'");
                break;
            }

            if (pose == "editor-palette")
            {
                // The command palette driven with real keys: Ctrl+K opens it, letters narrow it, Enter does the top command.
                var canvas = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
                canvas.Focus();
                Pump(0.3);
                window.KeyPress(Avalonia.Input.Key.K, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.K, null);
                Pump(0.5);
                Console.WriteLine($"palette open: {editor.Palette is not null}; {editor.Palette?.Results.Count} commands; first '{editor.Palette?.Results.FirstOrDefault()?.Name}'");
                Save("editor-palette");
                window.KeyTextInput("sector");
                Pump(0.4);
                Console.WriteLine($"palette 'sector': {string.Join(", ", editor.Palette?.Results.Take(4).Select(c => c.Name) ?? [])}");
                Save("editor-palette-sector");
                foreach (var _ in "sector") window.KeyPress(Avalonia.Input.Key.Back, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Backspace, null);
                window.KeyTextInput("wnd");
                Pump(0.4);
                window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
                Pump(0.4);
                Console.WriteLine($"palette 'wnd' + Enter: tool {editor.Tool}; palette open {editor.Palette is not null}; keys with the plan {canvas.IsFocused}");
                break;
            }

            if (pose == "editor-layout")
            {
                // The map area's three layouts (D46) driven with real keys and a real click: Tab from the plan fills the area
                // with the 3D view and Tab brings back the layout before; the 2D segment gives the plan the whole area. A Tab in
                // a text field moves between fields and leaves the layout alone (the control).
                var canvas = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
                var view3d = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().First();
                var views = window.GetVisualDescendants().OfType<Avalonia.Controls.Grid>().First(g => g.Name == "MapViews");
                for (var i = 0; i < 200 && (view3d.SceneVersion < 0 || !view3d.HasPicture); i++) Pump(0.05);
                string Shown() => $"layout {editor.Layout}; plan {(canvas.IsEffectivelyVisible ? $"{canvas.Bounds.Width:0}" : "hidden")}, 3D {(view3d.IsEffectivelyVisible ? $"{view3d.Bounds.Width:0}" : "hidden")} of {views.Bounds.Width:0}; keys with {(canvas.IsFocused ? "plan" : view3d.IsFocused ? "3D" : "neither")}";
                void Tab()
                {
                    window.KeyPress(Avalonia.Input.Key.Tab, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Tab, "\t");
                    Pump(0.6);
                }

                canvas.Focus();
                Pump(0.3);
                Console.WriteLine($"layout start: {Shown()}");
                Tab();
                Console.WriteLine($"layout tab: {Shown()}");
                Save("editor-layout-3d");
                Tab();
                Console.WriteLine($"layout tab again: {Shown()}");
                var twoD = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => b.Content as string == "2D");
                var at = twoD.TranslatePoint(new Avalonia.Point(twoD.Bounds.Width / 2, twoD.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                window.MouseUp(at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                Pump(0.6);
                Console.WriteLine($"layout 2D clicked: {Shown()}");
                Save("editor-layout-2d");
                Tab();
                Console.WriteLine($"layout tab from 2D: {Shown()}");
                Tab();
                Console.WriteLine($"layout tab back: {Shown()}");
                editor.Layout = WadSmith.Editor.App.MapLayout.Split;
                Pump(0.6);
                var field = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBox>().FirstOrDefault(t => t.IsEffectivelyVisible);
                field?.Focus();
                Pump(0.2);
                var hadKeys = field?.IsFocused == true;
                Tab();
                Console.WriteLine($"layout tab in a text field ({(hadKeys ? "it had the keys" : "it did NOT have the keys")}, {field?.Name ?? field?.PlaceholderText ?? (field is null ? "none" : "unnamed")}): {Shown()}");
                Save("editor-layout-split");
                break;
            }

            if (pose == "editor-platform")
            {
                // A floating platform added to the starter room through the Inspector, seen walking under it; then our view of
                // the platform calibration room (WS_PLATCAL: platform.wad, with GZDoom's view.png in below/ and above/) against the
                // engine's from the same two spots, with the room drawn without its platform as the control.
                var session = editor.Session;
                var view3d = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().First();
                for (var i = 0; i < 200 && (view3d.SceneVersion < 0 || !view3d.HasPicture); i++) Pump(0.05);
                var told = new List<string>();
                session.Notice += told.Add;
                var room = session.Map.Sectors[0];
                session.Selection.Set(room);
                Pump(0.5);
                editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Add a floating platform").Command.Execute(null);
                Pump(1.0);
                var added = WadSmith.Editor.Smart.Platforms.Of(session.Geometry, room);
                Console.WriteLine($"platform added: {added.Count} ({string.Join(", ", added.Select(p => $"{p.Kind} {p.Bottom:0}–{p.Top:0}, control wall args {string.Join(",", p.ControlLine.Args)}"))}); undo '{session.History.UndoLabel}'; told '{told.LastOrDefault()}'");
                Console.WriteLine($"platform rows: {string.Join(" | ", editor.Inspector.Rows.Select(r => r.Label))}");
                Console.WriteLine($"platform problems {editor.Problems.Summary}: {string.Join(" | ", editor.Problems.Rows.Select(r => r.Sentence))}");
                editor.CameraMode = WadSmith.App.ThreeD.CameraMode.Walk;
                Pump(1.5);
                Save("editor-platform");
                if (added.Count == 1)
                {
                    session.Selection.Set(added[0].Control);
                    Pump(0.5);
                    Console.WriteLine($"platform control selected: heading '{editor.Inspector.Heading}'");
                }

                if (Environment.GetEnvironmentVariable("WS_PLATCAL") is { } folder)
                {
                    var resources = session.Resources;
                    var lumps = WadSmith.Core.Maps.MapLocator.Find(WadSmith.Core.Containers.WadContainer.Open(Path.Combine(folder, "platform.wad"))).First();
                    var calMap = WadSmith.Editor.Model.EditorMap.FromModel(WadSmith.Core.Maps.MapConversion.Read(lumps, "doom"));
                    WadSmith.Editor.Scene.MapScene Scene() => WadSmith.Editor.Scene.MapScene.Build(WadSmith.Editor.Geometry.MapGeometry.Build(calMap.Frame), n => resources?.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, WadSmith.App.ThreeD.SpriteShapes.For(resources));
                    var with = Scene();
                    Console.WriteLine($"platcal platforms: {WadSmith.Editor.Geometry.MapGeometry.Build(calMap.Frame).Platforms.Count}");

                    // The control: the same room with the platform's special taken off.
                    var controlWall = calMap.Lines.Single(l => l.Special == WadSmith.Editor.Smart.Platforms.Special);
                    new WadSmith.Editor.Editing.History(calMap).Record("control", e => e.Set(controlWall, l => l.Special, (l, v) => l.Special = v, 0));
                    var without = Scene();

                    double Diff(byte[] png, string name)
                    {
                        using var ours = SkiaSharp.SKBitmap.Decode(png);
                        using var theirs = SkiaSharp.SKBitmap.Decode(Path.Combine(folder, name, "view.png"));
                        double total = 0;
                        var count = 0;
                        for (var y = 60; y < 580; y += 8)
                        {
                            for (var x = 200; x < 824; x += 8)
                            {
                                double ar = 0, ag = 0, ab = 0, br = 0, bg = 0, bb = 0;
                                for (var j = 0; j < 8; j++)
                                {
                                    for (var i = 0; i < 8; i++)
                                    {
                                        var (a, b) = (ours.GetPixel(x + i, y + j), theirs.GetPixel(x + i, y + j));
                                        (ar, ag, ab, br, bg, bb) = (ar + a.Red, ag + a.Green, ab + a.Blue, br + b.Red, bg + b.Green, bb + b.Blue);
                                    }
                                }

                                total += (Math.Abs(ar - br) + Math.Abs(ag - bg) + Math.Abs(ab - bb)) / 3.0 / 64;
                                count++;
                            }
                        }

                        return total / count;
                    }

                    // The engine's two spots: standing at (0, −200) looking north (eye 41), and held with the feet at 150 looking
                    // down 45° (GZDoom's pitch down is positive, ours negative).
                    var below = new WadSmith.App.ThreeD.Camera3D(0, -200, WadSmith.Editor.Scene.Walker.EyeHeight, 90, 0);
                    var above = new WadSmith.App.ThreeD.Camera3D(0, -200, 150 + WadSmith.Editor.Scene.Walker.EyeHeight, 90, -45);
                    var shots = new Dictionary<string, byte[]>
                    {
                        ["below"] = WadSmith.App.ThreeD.Offscreen3D.RenderPng(with, resources, below, 1024, 640, inside: true),
                        ["above"] = WadSmith.App.ThreeD.Offscreen3D.RenderPng(with, resources, above, 1024, 640, inside: true),
                        ["below-without"] = WadSmith.App.ThreeD.Offscreen3D.RenderPng(without, resources, below, 1024, 640, inside: true),
                        ["above-without"] = WadSmith.App.ThreeD.Offscreen3D.RenderPng(without, resources, above, 1024, 640, inside: true),
                        ["below-unstretched"] = WadSmith.App.ThreeD.Offscreen3D.RenderPng(with, resources, below with { Stretch = 1 }, 1024, 640, inside: true),
                        ["above-unstretched"] = WadSmith.App.ThreeD.Offscreen3D.RenderPng(with, resources, above with { Stretch = 1 }, 1024, 640, inside: true),
                    };
                    foreach (var (name, png) in shots) File.WriteAllBytes(Path.Combine(output, $"platcal-{name}-ours.png"), png);
                    Console.WriteLine($"platcal measured: below ours against the engine's {Diff(shots["below"], "below"):0.0} per channel, above {Diff(shots["above"], "above"):0.0}; controls without the platform: below {Diff(shots["below-without"], "below"):0.0}, above {Diff(shots["above-without"], "above"):0.0}; drawn without the pixel stretch: below {Diff(shots["below-unstretched"], "below"):0.0}, above {Diff(shots["above-unstretched"], "above"):0.0}");
                }

                break;
            }

            if (pose == "editor-map-settings")
            {
                // With nothing selected the Inspector shows the map's settings: the game's own values named, the map named
                // "Gateway" and its exit set to end the game; the project's zmapinfo.txt then says so.
                var session = editor.Session;
                for (var i = 0; i < 200 && session.Defaults is null; i++) Pump(0.05);
                session.Selection.Clear();
                Pump(0.5);
                Console.WriteLine($"map settings rows: {string.Join(" | ", editor.Inspector.Rows.Select(r => r.Label))}");
                var name = editor.Inspector.Rows.OfType<TextRow>().Single(r => r.Label == "Name");
                var music = editor.Inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Music");
                var next = editor.Inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "After the exit");
                Console.WriteLine($"map settings defaults: name hint '{name.HintText}'; music '{music.Options[0]}' of {music.Options.Count}; next '{next.Options[0]}' … '{next.Options[^1]}' of {next.Options.Count}");
                name.Value = "Gateway";
                name.Commit();
                next.Value = next.Options.Count - 1;
                Wait(session.FlushAsync());
                Pump(0.5);
                var file = Path.Combine(session.Project.Folder, "zmapinfo.txt");
                Console.WriteLine($"map settings record: title '{session.MapEntry.Title}' next '{session.MapEntry.Next}'");
                Console.WriteLine($"map settings file: {(File.Exists(file) ? File.ReadAllText(file).ReplaceLineEndings(" / ") : "MISSING")}");
                Save("editor-map-settings");
                break;
            }

            if (pose == "editor-door-drag")
            {
                // A tall room east of the starter room sharing its 512-long wall; the Door tool dragged along it from y −192 to
                // −64: a door 128 wide there. Then a click on the same wall's middle: a standard 64 there.
                var session = editor.Session;
                session.DrawRoom([new(256, -256), new(256, 256), new(512, 256), new(512, -256)]);
                Pump(1.0);
                var plan = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
                plan.FitMap();
                Pump(0.4);
                Avalonia.Point Spot(double x, double y) => plan.TranslatePoint(plan.ToScreen(new WadSmith.Editor.Geometry.MapPoint(x, y)), window)!.Value;
                plan.Focus();
                window.KeyPress(Avalonia.Input.Key.D, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.D, "d");
                Pump(0.2);
                window.MouseMove(Spot(256, -192), Avalonia.Input.RawInputModifiers.None);
                window.MouseDown(Spot(256, -192), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                for (var k = 1; k <= 8; k++)
                {
                    window.MouseMove(Spot(256, -192 + 16 * k), Avalonia.Input.RawInputModifiers.LeftMouseButton);
                    Pump(0.02);
                }

                Console.WriteLine($"door drag: status '{editor.Status}'");
                Save("editor-door-dragging");
                window.MouseUp(Spot(256, -64), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                Pump(1.0);
                string Doors() => string.Join(" ", session.Map.Sectors.Where(s => s.OnMap && s.CeilingHeight == s.FloorHeight).Select(s => session.Geometry.WallBoundsAt(s.Slot) is { } b ? $"[{b.MinX:0},{b.MinY:0} {b.MaxX:0},{b.MaxY:0}]" : "?"));
                Console.WriteLine($"door drag: doors {Doors()}; undo '{session.History.UndoLabel}'");
                window.MouseMove(Spot(256, 128), Avalonia.Input.RawInputModifiers.None);
                window.MouseDown(Spot(256, 128), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                window.MouseUp(Spot(256, 128), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                Pump(1.0);
                Console.WriteLine($"door click: doors {Doors()}; status '{editor.Status}'");
                Save("editor-door-drag");
                break;
            }

            if (pose == "editor-mirror")
            {
                // Four-way symmetry about the starter room's middle; a room dragged north-east of it, seen mid-drag with its three
                // copies, then drawn; an imp placed in it comes with three, each facing as its image.
                var session = editor.Session;
                editor.Session.Selection.Clear();
                editor.MirrorChoice = (int)WadSmith.Editor.Editing.MirrorMode.FourWays;
                Console.WriteLine($"mirror: {session.Mirror}; status '{editor.Status}'");
                var plan = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
                plan.FitMap();
                plan.ZoomBy(0.45);
                Pump(0.4);
                Avalonia.Point Spot(double x, double y) => plan.TranslatePoint(plan.ToScreen(new WadSmith.Editor.Geometry.MapPoint(x, y)), window)!.Value;
                plan.Focus();
                window.KeyPress(Avalonia.Input.Key.R, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.R, "r");
                Pump(0.2);
                window.MouseMove(Spot(320, 128), Avalonia.Input.RawInputModifiers.None);
                window.MouseDown(Spot(320, 128), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                for (var k = 1; k <= 10; k++)
                {
                    window.MouseMove(Spot(320 + 128 * k / 10.0, 128 + 192 * k / 10.0), Avalonia.Input.RawInputModifiers.LeftMouseButton);
                    Pump(0.02);
                }

                Save("editor-mirror-drawing");
                window.MouseUp(Spot(448, 320), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                for (var i = 0; i < 100 && session.Map.Sectors.Count(s => s.OnMap) < 5; i++) Pump(0.05);
                Pump(0.5);
                Console.WriteLine($"mirror rooms: {session.Map.Sectors.Count(s => s.OnMap)} areas, {session.Selection.Count} selected, undo '{session.History.UndoLabel}'");
                session.PlaceThing(3001, new WadSmith.Editor.Geometry.MapPoint(384, 224));
                Pump(0.5);
                Console.WriteLine($"mirror imps: {string.Join(" ", session.Map.Things.Where(t => t.OnMap && t.Type == 3001).Select(t => $"({t.X:0},{t.Y:0} {t.Angle}°)"))}");
                Save("editor-mirror");
                session.History.Undo();
                session.History.Undo();
                Pump(0.5);
                Console.WriteLine($"mirror undone twice: {session.Map.Sectors.Count(s => s.OnMap)} areas, {session.Map.Things.Count(t => t.OnMap)} things");
                break;
            }

            if (pose == "editor-round")
            {
                // The starter room's north-east corner clicked, the Inspector's reach 128 and 8 sides, "Round this corner".
                var session = editor.Session;
                var plan = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
                plan.FitMap();
                Pump(0.4);
                var corner = session.Map.Vertices.Single(v => v.OnMap && v.X == 256 && v.Y == 256);
                var spot = plan.TranslatePoint(plan.ToScreen(new WadSmith.Editor.Geometry.MapPoint(256, 256)), window)!.Value;
                plan.Focus();
                window.MouseMove(spot, Avalonia.Input.RawInputModifiers.None);
                window.MouseDown(spot, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                window.MouseUp(spot, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                Pump(0.5);
                Console.WriteLine($"round: selected '{editor.Inspector.Heading}'; rows {string.Join(" | ", editor.Inspector.Rows.Select(r => r.Label))}");
                editor.Inspector.Rows.OfType<NumberRow>().Single(r => r.Label == "Curve reach").Value = 128;
                editor.Inspector.Rows.OfType<NumberRow>().Single(r => r.Label == "Curve sides").Value = 8;
                editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Round this corner").Command.Execute(null);
                Pump(1.5);
                var room = session.Map.Sectors.First(s => s.OnMap);
                Console.WriteLine($"round: {session.Map.Lines.Count(l => l.OnMap)} walls, undo '{session.History.UndoLabel}', status '{editor.Status}', problems {editor.Problems.Summary}");
                Save("editor-round");
                break;
            }

            if (pose == "editor-guides")
            {
                // A room with corners off the grid (x 300 and 420) beside the starter room, then the Room tool corner by corner:
                // the pointer near x 420 and y 0 lines up with both (two guides); then a side close to 45° settles on it.
                var session = editor.Session;
                session.DrawRoom([new(300, 100), new(300, 180), new(420, 180), new(420, 100)]);
                Pump(1.0);
                var plan = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
                plan.FitTo([.. session.Map.Sectors.Where(s => s.OnMap)]);
                plan.ZoomBy(0.7);
                plan.PanBy(-120, 0);
                Pump(0.5);
                Avalonia.Point Spot(double x, double y) => plan.TranslatePoint(plan.ToScreen(new WadSmith.Editor.Geometry.MapPoint(x, y)), window)!.Value;
                Console.WriteLine($"guides view: (640, 0) at {Spot(640, 0)}, plan {plan.Bounds.Width:0} wide");
                plan.Focus();
                Pump(0.1);
                window.KeyPress(Avalonia.Input.Key.R, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.R, "r");
                Pump(0.2);
                void Click(double x, double y)
                {
                    window.MouseMove(Spot(x, y), Avalonia.Input.RawInputModifiers.None);
                    window.MouseDown(Spot(x, y), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(Spot(x, y), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.2);
                }

                Click(640, 0);
                window.MouseMove(Spot(422.5, 2), Avalonia.Input.RawInputModifiers.None);
                Pump(0.4);
                Console.WriteLine($"guides align: status '{editor.Status}'");
                Save("editor-guides-align");
                Click(422.5, 2);
                window.MouseMove(Spot(520, -104), Avalonia.Input.RawInputModifiers.None);
                Pump(0.4);
                Save("editor-guides-45");
                Click(520, -104);
                Click(640, -100);
                Click(640, 0);
                Pump(1.0);
                var room = session.Map.Sectors.Where(s => s.OnMap).Last();
                var corners = session.Map.Lines.Where(l => l.OnMap && (ReferenceEquals(l.Front.Sector, room) || ReferenceEquals(l.Back?.Sector, room))).SelectMany(l => new[] { (l.Start.X, l.Start.Y), (l.End.X, l.End.Y) }).Distinct().OrderBy(c => c.X).ThenBy(c => c.Y);
                Console.WriteLine($"guides room: {string.Join(" ", corners.Select(c => $"({c.X:0.#},{c.Y:0.#})"))}; undo '{session.History.UndoLabel}'");
                Save("editor-guides");
                break;
            }

            if (pose == "editor-arrange")
            {
                // Eight imps dropped about the starter room, all selected, "Arrange in a ring, facing in"; then one turned by
                // dragging its facing knob with the pointer (one undo step).
                var session = editor.Session;
                var rng = new Random(7);
                for (var i = 0; i < 8; i++) session.PlaceThing(3001, new WadSmith.Editor.Geometry.MapPoint(rng.Next(-200, 200), rng.Next(-200, 200)));
                Pump(0.5);
                var imps = session.Map.Things.Where(t => t.OnMap && t.Type == 3001).ToList();
                session.Selection.Set([.. imps]);
                Pump(0.5);
                Console.WriteLine($"arrange: '{editor.Inspector.Heading}'; rows {string.Join(" | ", editor.Inspector.Rows.SkipWhile(r => r.Label != "ARRANGE THE OBJECTS").Select(r => r.Label))}");
                editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Arrange in a ring, facing in").Command.Execute(null);
                Pump(1.0);
                Console.WriteLine($"arrange ring: {string.Join(" ", imps.Select(t => $"({t.X:0},{t.Y:0} {t.Angle}°)"))}; undo '{session.History.UndoLabel}'");
                Save("editor-arrange-ring");

                var one = imps[0];
                session.Selection.Set(one);
                var plan = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
                plan.FitTo([one]);
                plan.ZoomBy(0.5);
                Pump(0.6);
                Avalonia.Point Screen(double x, double y) => plan.TranslatePoint(plan.ToScreen(new WadSmith.Editor.Geometry.MapPoint(x, y)), window)!.Value;
                var centre = Screen(one.X, one.Y);
                var ringPx = Math.Max(3, (session.Resources?.Things.Find(3001)?.Radius ?? 20) * (Screen(one.X + 1, one.Y).X - centre.X));
                var a = one.Angle * Math.PI / 180;
                var knob = new Avalonia.Point(centre.X + Math.Cos(a) * (ringPx * 1.35 + 14), centre.Y - Math.Sin(a) * (ringPx * 1.35 + 14));
                var before = session.History.UndoLabel;
                window.MouseMove(knob, Avalonia.Input.RawInputModifiers.None);
                Pump(0.3);
                Console.WriteLine($"arrange knob hover: status '{editor.Status}'");
                window.MouseDown(knob, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                for (var k = 1; k <= 12; k++)
                {
                    var t = a - Math.PI / 2 * k / 12;
                    window.MouseMove(new Avalonia.Point(centre.X + Math.Cos(t) * 80, centre.Y - Math.Sin(t) * 80), Avalonia.Input.RawInputModifiers.LeftMouseButton);
                    Pump(0.03);
                }

                Console.WriteLine($"arrange turning: status '{editor.Status}'");
                Save("editor-arrange-turning");
                window.MouseUp(new Avalonia.Point(centre.X + Math.Cos(a - Math.PI / 2) * 80, centre.Y - Math.Sin(a - Math.PI / 2) * 80), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                Pump(0.5);
                session.History.Undo();
                Console.WriteLine($"arrange turned: facing after one undo {one.Angle}° (undo label was '{before}' before the drag)");
                session.History.Redo();
                Pump(0.5);
                Console.WriteLine($"arrange turned: facing {one.Angle}°");
                Save("editor-arrange");
                break;
            }

            if (pose == "editor-borrow")
            {
                // The Textures shelf's "From your other games", and Heretic's GRSKULL1 dropped on the starter room's north wall:
                // copied into the project, painted, shown in 3D from the player's start.
                editor.ShowTextures = true;
                var filling = System.Diagnostics.Stopwatch.StartNew();
                editor.TextureShelf.Open();
                for (var i = 0; i < 600 && editor.TextureShelf.Tiles.Count == 0; i++) Pump(0.05);
                Console.WriteLine($"borrow shelf: this game's {editor.TextureShelf.Tiles.Count} in {filling.ElapsedMilliseconds} ms");
                // The chooser shows one game at a time: the shelf's own total (its note's "of N") says what has arrived.
                int Total() => int.TryParse(System.Text.RegularExpressions.Regex.Match(editor.TextureShelf.Note, @"of (\d+)").Groups[1].Value, out var n) ? n : 0;
                var own = Total();
                for (var i = 0; i < 2000 && editor.TextureShelf.Games.Count < 2; i++) Pump(0.01);
                Console.WriteLine($"borrow shelf: the first other game ({editor.TextureShelf.Games.LastOrDefault()}) in {filling.ElapsedMilliseconds} ms");
                var last = Total();
                var still = 0;
                for (var i = 0; i < 4000 && still < 100; i++)
                {
                    Pump(0.01);
                    var now = Total();
                    still = now == last ? still + 1 : 0;
                    last = now;
                }

                Console.WriteLine($"borrow shelf: all {editor.TextureShelf.Games.Count - 1} other games in {filling.ElapsedMilliseconds - 1000} ms ({own} own, {last} in all)");
                editor.TextureShelf.GameIndex = editor.TextureShelf.Games.ToList().FindIndex(g => g.StartsWith("Heretic", StringComparison.Ordinal));
                editor.TextureShelf.Search = "grskull";
                Pump(0.5);
                Console.WriteLine($"borrow shelf: {editor.TextureShelf.Note}; {string.Join(", ", editor.TextureShelf.Tiles.Take(6).Select(t => t.Label))}");
                Save("editor-borrow-shelf");
                var tile = editor.TextureShelf.Tiles.First(t => t.Label == "GRSKULL1");
                var north = editor.Session.Map.Lines.Where(l => l.OnMap).MaxBy(l => l.Start.Y + l.End.Y)!;
                editor.Session.PaintBorrowed(tile.Drop.From!, "GRSKULL1", false, new WadSmith.Editor.Geometry.MapPoint((north.Start.X + north.End.X) / 2, (north.Start.Y + north.End.Y) / 2 - 2), 8, false);
                for (var i = 0; i < 200 && north.Front.Middle != "GRSKULL1"; i++) Pump(0.05);
                Pump(1.5);
                Console.WriteLine($"borrow: north wall {north.Front.Middle}; status '{editor.Status}'; project {editor.Session.Project.Folder}");
                editor.CameraMode = WadSmith.App.ThreeD.CameraMode.Walk;
                Pump(2.0);
                Save("editor-borrow");
                break;
            }

            if (pose == "editor-fit")
            {
                // Where the player cannot pass, marked in red; close to the first gaps of each kind with the player's box on
                // each; then the box at the player's start (fits) and against the wall nearest it (does not).
                Avalonia.Point Spot(WadSmith.Editor.Geometry.MapPoint m) => view.TranslatePoint(view.ToScreen(m), window)!.Value;
                Pump(2.5);
                Save("editor-fit");
                var geometry = editor.Session.Geometry;
                var gaps = WadSmith.Editor.Checks.Fit.Gaps(geometry);
                Console.WriteLine($"fit problems: {string.Join(" | ", editor.Problems.Rows.Select(r => r.Sentence))}");
                Console.WriteLine($"fit: {gaps.Count} gaps ({string.Join(", ", gaps.GroupBy(g => g.Kind).Select(g => $"{g.Count()} {g.Key}"))}), {WadSmith.Editor.Checks.Fit.LowPassages(geometry).Count} low");
                var shots = int.TryParse(Environment.GetEnvironmentVariable("WS_FIT_SHOTS"), out var wanted) ? wanted : 2;
                var k = 0;
                foreach (var gap in gaps.GroupBy(g => g.Kind).SelectMany(g => g.Take(shots)))
                {
                    view.FitTo([geometry.Frame.Lines[gap.WallA].Element!, geometry.Frame.Lines[gap.WallB].Element!]);
                    view.ZoomBy(0.5);
                    Pump(0.6);
                    var centre = new WadSmith.Editor.Geometry.MapPoint(gap.Band.Average(c => c.X), gap.Band.Average(c => c.Y));
                    window.MouseMove(Spot(centre), Avalonia.Input.RawInputModifiers.None);
                    Pump(0.8);
                    Console.WriteLine($"fit {k}: {gap.Kind} {gap.Width:0.#} of {gap.Needed:0.#} at {centre.X:0},{centre.Y:0}; status '{editor.Status}'");
                    Save($"editor-fit-{k++}");
                }

                var start = editor.Session.Map.Things.FirstOrDefault(t => t.OnMap && t.Type == 1) ?? editor.Session.Map.Things.First(t => t.OnMap);
                var here = new WadSmith.Editor.Geometry.MapPoint(start.X, start.Y);
                var wall = editor.Session.Map.Lines.Where(l => l.OnMap && l.Back is null).MinBy(l => WadSmith.Editor.Geometry.MapGeometry.DistanceToSegment(here, new(l.Start.X, l.Start.Y), new(l.End.X, l.End.Y)))!;
                var view3d = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().First();
                var floor = geometry.AreaAt(here)?.FloorHeight ?? 0;
                view.FitTo([start]);
                view.ZoomBy(0.5);
                view3d.LookAt(here, floor + 28, 360);
                Pump(1.0);
                window.MouseMove(Spot(here), Avalonia.Input.RawInputModifiers.None);
                Pump(1.5);
                Console.WriteLine($"fit start: status '{editor.Status}'");
                Save("editor-fit-start");
                var (wa, wb) = (new WadSmith.Editor.Geometry.MapPoint(wall.Start.X, wall.Start.Y), new WadSmith.Editor.Geometry.MapPoint(wall.End.X, wall.End.Y));
                var (mx, my) = ((wa.X + wb.X) / 2, (wa.Y + wb.Y) / 2);
                var toward = Math.Sqrt((here.X - mx) * (here.X - mx) + (here.Y - my) * (here.Y - my));
                var against = new WadSmith.Editor.Geometry.MapPoint(mx + (here.X - mx) / toward * 6, my + (here.Y - my) / toward * 6);
                view.FitTo([start, wall]);
                view3d.LookAt(against, floor + 28, 360);
                Pump(1.0);
                window.MouseMove(Spot(against), Avalonia.Input.RawInputModifiers.None);
                Pump(1.5);
                Console.WriteLine($"fit wall: status '{editor.Status}'");
                Save("editor-fit-wall");
                break;
            }

            if (pose == "editor-3d-selected")
            {
                // A room and a monster selected: outlined on the plan, tinted in the 3D view.
                editor.CameraMode = WadSmith.App.ThreeD.CameraMode.Orbit;
                var room = editor.Session.Map.Sectors.OrderByDescending(s => s.CeilingHeight - s.FloorHeight).First();
                var monster = editor.Session.Map.Things.FirstOrDefault(t => t.Type == 9) ?? editor.Session.Map.Things.First();
                editor.Session.Selection.Set(room, monster);
                Pump(2.5);
                Save("editor-3d-selected");
                break;
            }

            if (pose is "editor-draw" or "editor-move" or "editor-join" or "editor-delete" or "editor-box" or "editor-make-door" or "editor-shelves" or "editor-library" or "editor-make-lift" or "editor-make-teleport" or "editor-draw-shape" or "editor-copy" or "editor-make-stairs" or "editor-turn" or "editor-make-window" or "editor-make-secret" or "editor-make-switch" or "editor-make-exit" or "editor-rooms" or "editor-key" or "editor-make-light" or "editor-measure" or "editor-looks" or "editor-prefab")
            {
                // Real key and pointer events: R picks the Room tool, a drag beside the starter room draws a
                // room sharing its east wall (captured mid-drag too), then a drag inside the new room draws
                // one inside it.
                var wide = pose is "editor-join" or "editor-make-lift" or "editor-make-teleport" or "editor-draw-shape" or "editor-copy" or "editor-make-stairs" or "editor-turn" or "editor-make-window" or "editor-make-secret" or "editor-make-switch" or "editor-make-exit" or "editor-rooms" or "editor-key" or "editor-measure" or "editor-looks" or "editor-prefab";
                view.ZoomBy(wide ? 0.3 : 0.45);
                if (wide) view.PanBy(-90, 0);
                Pump(0.3);
                view.Focus();
                window.KeyPress(Avalonia.Input.Key.R, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.R, "r");
                Pump(0.2);
                Avalonia.Point At(double x, double y) => view.TranslatePoint(view.ToScreen(new WadSmith.Editor.Geometry.MapPoint(x, y)), window)!.Value;
                void Drag((double X, double Y) a, (double X, double Y) b, string? midShot)
                {
                    window.MouseMove(At(a.X, a.Y), Avalonia.Input.RawInputModifiers.None);
                    window.MouseDown(At(a.X, a.Y), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    for (var k = 1; k <= 10; k++)
                    {
                        window.MouseMove(At(a.X + (b.X - a.X) * k / 10, a.Y + (b.Y - a.Y) * k / 10), Avalonia.Input.RawInputModifiers.LeftMouseButton);
                        Pump(0.02);
                    }

                    if (midShot is not null) Save(midShot);
                    window.MouseUp(At(b.X, b.Y), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(1.0);
                }

                void Report(string step)
                {
                    var m = editor.Session.Map;
                    Console.WriteLine($"draw {step}: tool {editor.Tool}, {m.Sectors.Count(s => s.OnMap)} areas, {m.Lines.Count(l => l.OnMap)} walls, {m.Lines.Count(l => l.OnMap && l.Back is not null)} between areas, {editor.Session.Selection.Count} selected, undo '{editor.Session.History.UndoLabel}', status '{editor.Status}'");
                }

                Report("before");
                if (pose == "editor-library")
                {
                    // The free sources (D61), live: Realm667 on the Objects chooser with its previews and the Box dragged onto the
                    // plan; its monsters; the texture packs on the Textures chooser, The Darkening E2 opened and one of its
                    // textures on the south wall; a free game to get.
                    void DropAt((double X, double Y) at, WadSmith.App.ViewModels.ShelfDrop drop)
                    {
                        var data = new Avalonia.Input.DataTransfer();
                        data.Add(Avalonia.Input.DataTransferItem.Create(WadSmith.App.ViewModels.ShelfDrop.Format, drop));
                        foreach (var kind in new[] { Avalonia.Input.Raw.RawDragEventType.DragEnter, Avalonia.Input.Raw.RawDragEventType.DragOver, Avalonia.Input.Raw.RawDragEventType.Drop })
                        {
                            window.DragDrop(At(at.X, at.Y), kind, data, Avalonia.Input.DragDropEffects.Copy, Avalonia.Input.RawInputModifiers.None);
                            Pump(0.1);
                        }

                        Pump(0.5);
                    }

                    void Pictures(WadSmith.App.ViewModels.ShelfViewModel shelf)
                    {
                        for (var i = 0; i < 300 && shelf.Tiles.Take(12).Any(t => t.Picture is null && t.LoadPicture is not null); i++) Pump(0.05);
                    }

                    editor.ShowObjects = true;
                    var objects = editor.ObjectShelf;
                    for (var i = 0; i < 400 && objects.Tiles.Count == 0; i++) Pump(0.05);
                    Console.WriteLine($"library chooser: {string.Join(" | ", objects.Games)}");
                    objects.GameIndex = objects.Games.IndexOf(WadSmith.App.ViewModels.LibrarySources.Realm667Name);
                    for (var i = 0; i < 2400 && !objects.Note.StartsWith("Realm667:", StringComparison.Ordinal) && !objects.Note.Contains("could not", StringComparison.Ordinal); i++) Pump(0.05);
                    Console.WriteLine($"library objects: {objects.Note}");
                    objects.Group = objects.Groups.ToList().IndexOf("Decorations");
                    objects.Search = "box";
                    Pump(0.5);
                    Pictures(objects);
                    Save("editor-library-objects");
                    var box = objects.Tiles.First(t => t.Label == "Box");
                    var things = editor.Session.Map.Things.Count;
                    DropAt((0, 0), box.Drop);
                    for (var i = 0; i < 600 && editor.Session.Map.Things.Count == things; i++) Pump(0.05);
                    Pump(0.5);
                    Console.WriteLine($"library placed: {string.Join(",", editor.Session.Map.Things.Skip(things).Select(t => $"{editor.Session.Resources?.Things.Find(t.Type)?.ClassName}#{t.Type}"))}; status '{editor.Status}'");
                    Save("editor-library-placed");
                    objects.Search = "";
                    objects.Group = objects.Groups.ToList().IndexOf("Monsters");
                    Pump(0.5);
                    Pictures(objects);
                    Save("editor-library-monsters");

                    editor.ShowTextures = true;
                    var textures = editor.TextureShelf;
                    for (var i = 0; i < 400 && textures.Tiles.Count == 0; i++) Pump(0.05);
                    textures.GameIndex = textures.Games.IndexOf(WadSmith.App.ViewModels.LibrarySources.PacksName);
                    for (var i = 0; i < 2400 && !textures.Note.StartsWith("Texture packs:", StringComparison.Ordinal) && !textures.Note.Contains("could not", StringComparison.Ordinal); i++) Pump(0.05);
                    Console.WriteLine($"library packs: {textures.Note}; {string.Join(", ", textures.Tiles.Take(8).Select(t => $"{t.Label}[{t.Badge}]"))}");
                    Pictures(textures);
                    Save("editor-library-packs");
                    var pack = textures.Tiles.First(t => t.Label.StartsWith("The Darkening", StringComparison.Ordinal));
                    pack.Activate!();
                    for (var i = 0; i < 2400 && !textures.Tiles.Any(t => t.Game == pack.Label && t.Group == "Walls"); i++) Pump(0.05);
                    Pump(0.5);
                    Console.WriteLine($"library pack: shows {textures.Games[Math.Max(0, textures.GameIndex)]}; {textures.Note}");
                    Save("editor-library-pack");
                    var wallTile = textures.Tiles.First(t => t.Group == "Walls");
                    DropAt((0, -250), wallTile.Drop);
                    var southWall = editor.Session.Map.Lines.Single(l => l.OnMap && l.Start.Y == -256 && l.End.Y == -256);
                    for (var i = 0; i < 400 && southWall.Front.Middle != wallTile.Drop.Texture; i++) Pump(0.05);
                    Console.WriteLine($"library wall: {southWall.Front.Middle} (dropped {wallTile.Label}); credits {string.Join(",", Directory.EnumerateFiles(Path.Combine(editor.Session.Project.Folder, "credits")).Select(Path.GetFileName))}");
                    Save("editor-library-wall");
                    if (textures.Games.FirstOrDefault(g => g.EndsWith("(free)", StringComparison.Ordinal) && textures.Sources.Any(s => s.Name == g && s.Gone is not null)) is { } free)
                    {
                        textures.GameIndex = textures.Games.IndexOf(free);
                        Pump(0.5);
                        Console.WriteLine($"library free game: {textures.Note}");
                        Save("editor-library-free-game");
                    }
                }

                if (pose == "editor-shelves")
                {
                    // The Textures shelf, then a wall texture dropped on the starter room's south wall with real
                    // drag events; the Objects shelf, then an Imp dropped in the room.
                    void DropOn((double X, double Y) at, WadSmith.App.ViewModels.ShelfDrop drop)
                    {
                        var data = new Avalonia.Input.DataTransfer();
                        data.Add(Avalonia.Input.DataTransferItem.Create(WadSmith.App.ViewModels.ShelfDrop.Format, drop));
                        foreach (var kind in new[] { Avalonia.Input.Raw.RawDragEventType.DragEnter, Avalonia.Input.Raw.RawDragEventType.DragOver, Avalonia.Input.Raw.RawDragEventType.Drop })
                        {
                            window.DragDrop(At(at.X, at.Y), kind, data, Avalonia.Input.DragDropEffects.Copy, Avalonia.Input.RawInputModifiers.None);
                            Pump(0.1);
                        }

                        Pump(0.5);
                    }

                    editor.ShowTextures = true;
                    for (var i = 0; i < 200 && editor.TextureShelf.Tiles.Count == 0; i++) Pump(0.05);
                    Console.WriteLine($"shelf textures: {editor.TextureShelf.Note}");
                    Save("editor-shelf-textures");
                    var brick = editor.TextureShelf.Tiles.First(t => t.Group == "Walls" && t.Label.StartsWith("BRICK", StringComparison.Ordinal));
                    DropOn((0, -250), brick.Drop);
                    var south = editor.Session.Map.Lines.Single(l => l.OnMap && l.Start.Y == -256 && l.End.Y == -256);
                    Console.WriteLine($"shelf wall: {south.Front.Middle} (dropped {brick.Label}), undo '{editor.Session.History.UndoLabel}'");

                    editor.ShowObjects = true;
                    for (var i = 0; i < 200 && editor.ObjectShelf.Tiles.Count == 0; i++) Pump(0.05);
                    Console.WriteLine($"shelf objects: {editor.ObjectShelf.Note}; groups {string.Join(", ", editor.ObjectShelf.Groups)}");
                    editor.ObjectShelf.Group = 1;
                    Pump(0.3);
                    Save("editor-shelf-objects");
                    var imp = editor.ObjectShelf.Tiles.First(t => t.Label == "Imp");
                    DropOn((128, 128), imp.Drop);
                    Console.WriteLine($"shelf imps on the shelf: {string.Join(", ", editor.ObjectShelf.Tiles.Where(t => t.Label == "Imp").Select(t => t.Drop.Thing))}; dropped {imp.Drop.Thing}");
                    var placed = editor.Session.Map.Things.Where(t => t.OnMap && t.Type == imp.Drop.Thing).ToList();
                    Console.WriteLine($"shelf thing: {placed.Count} imp(s) at {string.Join(" ", placed.Select(t => $"({t.X},{t.Y})"))}, undo '{editor.Session.History.UndoLabel}'");
                    Save("editor-shelves");
                    break;
                }

                if (pose == "editor-make-stairs")
                {
                    // A room east of the starter room raised to 64 in the Inspector, then S and a drag from the
                    // starter room's east wall to the new room: steps between them.
                    window.KeyPress(Avalonia.Input.Key.R, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.R, "r");
                    Drag((512, -128), (768, 128), null);
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    window.MouseDown(At(640, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(640, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.5);
                    editor.Inspector.Rows.OfType<NumberRow>().First(r => r.Label == "Floor height").Value = 64;
                    editor.Inspector.Rows.OfType<NumberRow>().First(r => r.Label == "Ceiling height").Value = 192;
                    Pump(0.5);
                    window.KeyPress(Avalonia.Input.Key.S, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.S, "s");
                    Pump(0.2);
                    Drag((256, -64), (512, 64), "editor-making-stairs");
                    Report("stairs");
                    var floors = editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Sector>().Select(x => x.FloorHeight).Order();
                    Console.WriteLine($"draw steps: {string.Join(" ", floors)}; {editor.Problems.Summary}");
                    editor.CameraMode = WadSmith.App.ThreeD.CameraMode.Walk;
                    Pump(1.5);
                    Save("editor-make-stairs");

                    // The middle step selected: the Inspector knows the flight; steps of 20, then evened out again.
                    editor.CameraMode = WadSmith.App.ThreeD.CameraMode.Orbit;
                    view.Focus();
                    Pump(0.3);
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    window.MouseDown(At(384, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(384, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.6);
                    Console.WriteLine($"stairs inspector: '{editor.Inspector.Heading}' / {editor.Inspector.Summary} / rows {string.Join(" | ", editor.Inspector.Rows.Select(r => r.Label).Take(4))}");
                    editor.Inspector.Rows.OfType<NumberRow>().Single(r => r.Label == "Each step rises").Value = 20;
                    Pump(0.6);
                    var all = WadSmith.Editor.Smart.StairsFinder.Recognise(editor.Session.Geometry, editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Sector>().Single())!;
                    Console.WriteLine($"stairs at 20: {string.Join(" ", all.Heights)}; {editor.Inspector.Summary}; undo '{editor.Session.History.UndoLabel}'");
                    Save("editor-stairs-inspector");
                    editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Even the steps out").Command.Execute(null);
                    Pump(0.6);
                    editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Select all the steps").Command.Execute(null);
                    Pump(0.6);
                    Pump(1.0);
                    Console.WriteLine($"stairs evened: {string.Join(" ", editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Sector>().Select(x => x.FloorHeight).Order())}; {editor.Problems.Summary}: {string.Join(" | ", editor.Problems.Rows.Select(r => r.Sentence))}");

                    // Cut again into five steps from the Inspector (one step selected).
                    editor.Session.Selection.Set(editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Sector>().First());
                    Pump(0.4);
                    editor.Inspector.Rows.OfType<NumberRow>().Single(r => r.Label == "Steps").Value = 5;
                    for (var i = 0; i < 60 && editor.Session.Selection.Count != 5; i++) Pump(0.05);
                    Pump(0.6);
                    Console.WriteLine($"stairs five: {string.Join(" ", editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Sector>().Select(x => x.FloorHeight).Order())}; '{editor.Inspector.Heading}' / {editor.Inspector.Summary}; undo '{editor.Session.History.UndoLabel}'");
                    Save("editor-stairs-five");
                    break;
                }

                if (pose == "editor-prefab")
                {
                    // A room east of the starter room with an imp in it, the Inspector's "Save as a prefab"; then the
                    // Objects panel's Prefabs and the prefab dragged onto the map north of the starter room.
                    Drag((384, -128), (640, 128), null);
                    editor.Session.PlaceThing(3001, new WadSmith.Editor.Geometry.MapPoint(512, 0));
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    window.MouseDown(At(448, 64), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(448, 64), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.6);
                    editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Save as a prefab").Command.Execute(null);
                    Pump(1.0);
                    Console.WriteLine($"prefab saved: status '{editor.Status}'");
                    editor.ShowObjects = true;
                    for (var i = 0; i < 200 && editor.ObjectShelf.Tiles.Count == 0; i++) Pump(0.05);
                    editor.ObjectShelf.Group = editor.ObjectShelf.Groups.ToList().IndexOf("Prefabs");
                    Pump(0.5);
                    var tile = editor.ObjectShelf.Tiles.FirstOrDefault();
                    Console.WriteLine($"prefab shelf: {editor.ObjectShelf.Note}; first tile {tile?.Label}, picture {tile?.Picture is not null}");
                    Save("editor-prefab-shelf");
                    if (tile is not null)
                    {
                        var data = new Avalonia.Input.DataTransfer();
                        data.Add(Avalonia.Input.DataTransferItem.Create(WadSmith.App.ViewModels.ShelfDrop.Format, tile.Drop));
                        foreach (var kind in new[] { Avalonia.Input.Raw.RawDragEventType.DragEnter, Avalonia.Input.Raw.RawDragEventType.DragOver, Avalonia.Input.Raw.RawDragEventType.Drop })
                        {
                            window.DragDrop(At(512, 640), kind, data, Avalonia.Input.DragDropEffects.Copy, Avalonia.Input.RawInputModifiers.None);
                            Pump(0.1);
                        }

                        Pump(1.5);
                    }

                    Report("prefab");
                    Console.WriteLine($"prefab placed: {editor.Session.Map.Sectors.Count(x => x.OnMap)} areas, {editor.Session.Map.Things.Count(t => t.OnMap && t.Type == 3001)} imps, undo '{editor.Session.History.UndoLabel}'");
                    Save("editor-prefab");
                    break;
                }

                if (pose == "editor-looks")
                {
                    // The Textures panel, its "New rooms look like" set to Hell, then a room drawn east of the starter room.
                    editor.ShowTextures = true;
                    Pump(0.5);
                    Console.WriteLine($"looks offered: {string.Join(", ", editor.LookNames)}");
                    editor.LookIndex = editor.LookNames.ToList().IndexOf("Hell");
                    Pump(0.2);
                    Save("editor-looks-picked");
                    window.KeyPress(Avalonia.Input.Key.R, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.R, "r");
                    Drag((384, -128), (640, 128), null);
                    var made = editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Sector>().SingleOrDefault();
                    Console.WriteLine($"looks drawn: floor {made?.FloorTexture}, ceiling {made?.CeilingTexture}, light {made?.Light}, walls {string.Join(" ", editor.Session.Map.Lines.Where(l => l.OnMap && l.Front.Sector == made).Select(l => l.Front.Middle).Distinct())}");
                    var session = editor.Session;
                    var scene = WadSmith.Editor.Scene.MapScene.Build(session.Geometry, n => session.Resources?.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, WadSmith.App.ThreeD.SpriteShapes.For(session.Resources));
                    File.WriteAllBytes(Path.Combine(output, "editor-looks-3d.png"), WadSmith.App.ThreeD.Offscreen3D.RenderPng(scene, session.Resources, new WadSmith.App.ThreeD.Camera3D(420, -96, 41, 45, 0), 960, 600, inside: true));
                    break;
                }

                if (pose == "editor-measure")
                {
                    // A room east of the starter room, its floor raised to 40 in the Inspector; then M and a drag from the
                    // starter room into it: the profile below the map shows the step the player can't take.
                    Drag((256, -128), (512, 128), null);
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    window.MouseDown(At(384, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(384, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.5);
                    editor.Inspector.Rows.OfType<NumberRow>().First(r => r.Label == "Floor height").Value = 40;
                    editor.Inspector.Rows.OfType<NumberRow>().First(r => r.Label == "Ceiling height").Value = 168;
                    Pump(0.5);
                    window.KeyPress(Avalonia.Input.Key.M, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.M, "m");
                    Pump(0.2);
                    Drag((-192, 0), (448, 0), "editor-measuring");
                    Pump(0.8);
                    Console.WriteLine($"measure: tool {editor.Tool}, profile shown {editor.ShowProfile}: '{editor.ProfileText}'");
                    Save("editor-measure");
                    break;
                }

                if (pose == "editor-make-light")
                {
                    // L and a click in the starter room; the Inspector's colour red and a reach of 256; the room
                    // darkened to 96 so the light shows. Then the 3D view from the south of the room, facing north.
                    window.KeyPress(Avalonia.Input.Key.L, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.L, "l");
                    Pump(0.2);
                    Console.WriteLine($"light tool: {editor.Tool}, status '{editor.Status}'");
                    window.MouseMove(At(0, 64), Avalonia.Input.RawInputModifiers.None);
                    window.MouseDown(At(0, 64), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(0, 64), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.8);
                    var light = editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Thing>().SingleOrDefault();
                    Console.WriteLine($"light placed: {light?.Type} at ({light?.X},{light?.Y}) height {light?.Height}, heading '{editor.Inspector.Heading}', '{editor.Inspector.Summary}'");
                    editor.Inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Colour").Value = 2;
                    editor.Inspector.Rows.OfType<NumberRow>().Single(r => r.Label == "Reach").Value = 256;
                    Pump(0.5);
                    Console.WriteLine($"light changed: '{editor.Inspector.Summary}', args {string.Join(",", light?.Args ?? [])}, undo '{editor.Session.History.UndoLabel}'");
                    var room = editor.Session.Map.Sectors[0];
                    editor.Session.History.Record("Darken", e => e.Set(room, x => x.Light, (x, v) => x.Light = v, 96));
                    Pump(1.0);
                    Wait(editor.Session.FlushAsync());
                    Save("editor-make-light");
                    var session = editor.Session;
                    var scene = WadSmith.Editor.Scene.MapScene.Build(session.Geometry, n => session.Resources?.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, WadSmith.App.ThreeD.SpriteShapes.For(session.Resources));
                    Console.WriteLine($"light scene: {scene.Lights.Count} lights");
                    File.WriteAllBytes(Path.Combine(output, "editor-light-3d.png"), WadSmith.App.ThreeD.Offscreen3D.RenderPng(scene, session.Resources, new WadSmith.App.ThreeD.Camera3D(0, -200, 41, 90, 0), 960, 600, inside: true));
                    break;
                }

                if (pose == "editor-key")
                {
                    // A door into a small room east of the starter room, locked with the red key in the Inspector;
                    // "Place the red key…" and a click in the starter room; then the key put behind its own door.
                    Drag((256, -64), (512, 64), null);
                    window.KeyPress(Avalonia.Input.Key.D, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.D, "d");
                    Pump(0.2);
                    window.MouseDown(At(256, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(256, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(1.0);
                    editor.Inspector.Rows.OfType<ChoiceRow>().Single(r => r.Label == "Locked with").Value = 1;
                    Pump(0.6);
                    editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Place the red key…").Command.Execute(null);
                    Pump(0.3);
                    Console.WriteLine($"key waiting: tool {editor.Tool}, status '{editor.Status}'");
                    window.MouseMove(At(-128, -128), Avalonia.Input.RawInputModifiers.None);
                    window.MouseDown(At(-128, -128), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(-128, -128), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(1.0);
                    var key = editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Thing>().SingleOrDefault();
                    Console.WriteLine($"key placed: {key?.Type} at ({key?.X},{key?.Y}), heading '{editor.Inspector.Heading}', '{editor.Inspector.Summary}', tool {editor.Tool}; {editor.Problems.Summary}");
                    Save("editor-key");
                    if (key is not null) editor.Session.History.Record("Move", e => e.Set(key, t => t.X, (t, v) => t.X = v, 480.0));
                    if (key is not null) editor.Session.History.Record("Move", e => e.Set(key, t => t.Y, (t, v) => t.Y = v, 0.0));
                    Pump(1.5);
                    Console.WriteLine($"key behind its door: {editor.Problems.Summary}; {string.Join(" | ", editor.Problems.Rows.Select(p => p.Sentence))}");
                    break;
                }

                if (pose == "editor-rooms")
                {
                    // A room east of the starter room sharing its wall; V, a click on each with Shift, J: one room.
                    // Then a small room drawn inside it, and the Inspector's "Make it a pool".
                    Drag((256, -256), (512, 256), null);
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    window.MouseDown(At(-128, 64), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(-128, 64), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseDown(At(384, 64), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Shift);
                    window.MouseUp(At(384, 64), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Shift);
                    Pump(0.6);
                    Console.WriteLine($"rooms selected: heading '{editor.Inspector.Heading}', rows {string.Join(" | ", editor.Inspector.Rows.Select(r => r.Label).Take(2))}");
                    window.KeyPress(Avalonia.Input.Key.J, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.J, "j");
                    Pump(1.0);
                    Report("joined");
                    window.KeyPress(Avalonia.Input.Key.R, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.R, "r");
                    Drag((64, -128), (320, 128), null);
                    Pump(0.6);
                    Console.WriteLine($"rooms inside: heading '{editor.Inspector.Heading}', island rows {string.Join(" | ", editor.Inspector.Rows.Where(r => r.Label.StartsWith("Make it a p", StringComparison.Ordinal)).Select(r => r.Label))}");
                    editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Make it a pool").Command.Execute(null);
                    Pump(1.0);
                    var pool = editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Sector>().SingleOrDefault();
                    Console.WriteLine($"rooms pool: floor {pool?.FloorHeight} {pool?.FloorTexture}, undo '{editor.Session.History.UndoLabel}'");
                    Report("pool");
                    Save("editor-rooms");
                    var session = editor.Session;
                    var scene = WadSmith.Editor.Scene.MapScene.Build(session.Geometry, n => session.Resources?.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, WadSmith.App.ThreeD.SpriteShapes.For(session.Resources));
                    File.WriteAllBytes(Path.Combine(output, "editor-rooms-3d.png"), WadSmith.App.ThreeD.Offscreen3D.RenderPng(scene, session.Resources, new WadSmith.App.ThreeD.Camera3D(-200, 0, 80, 0, -12), 960, 600, inside: true));
                    break;
                }

                if (pose == "editor-make-switch")
                {
                    // A small room against the starter room's east wall behind a door (D and a click), the door
                    // selected, the Inspector's "Add a switch that opens it", then a click on the starter room's west
                    // wall: the switch, joined to the door by a dashed line on the plan.
                    Drag((256, -64), (384, 64), null);
                    window.KeyPress(Avalonia.Input.Key.D, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.D, "d");
                    Pump(0.2);
                    window.MouseMove(At(256, 0), Avalonia.Input.RawInputModifiers.None);
                    window.MouseDown(At(256, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(256, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(1.0);
                    Console.WriteLine($"switch door: heading '{editor.Inspector.Heading}', undo '{editor.Session.History.UndoLabel}'");
                    editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Add a switch that opens it").Command.Execute(null);
                    Pump(0.3);
                    Console.WriteLine($"switch waiting: tool {editor.Tool}, status '{editor.Status}'");
                    window.MouseMove(At(-256, 0), Avalonia.Input.RawInputModifiers.None);
                    window.MouseDown(At(-256, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(-256, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(1.0);
                    var made = editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Line>().SingleOrDefault();
                    Console.WriteLine($"switch made: heading '{editor.Inspector.Heading}', '{editor.Inspector.Summary}', wall {made?.Slot} {made?.Front.Middle} {made?.Length}, tool {editor.Tool}, undo '{editor.Session.History.UndoLabel}'");
                    Report("switch");
                    Wait(editor.Session.FlushAsync());
                    Save("editor-make-switch");
                    break;
                }

                if (pose == "editor-make-exit")
                {
                    // The Guide's exit step the user's way: a click on the starter room's west wall three quarters of
                    // the way up selects the wall, then the Inspector's "Make it the exit switch": the switch is the
                    // 64 round the click, and the rest of the wall stays wall.
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    Pump(0.3);
                    window.MouseMove(At(-256, 128), Avalonia.Input.RawInputModifiers.None);
                    window.MouseDown(At(-256, 128), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(-256, 128), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(1.0);
                    Console.WriteLine($"exit wall: heading '{editor.Inspector.Heading}', clicked at {editor.Session.Selection.At}");
                    editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Make it the exit switch").Command.Execute(null);
                    Pump(1.0);
                    var exit = editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Line>().SingleOrDefault();
                    var west = editor.Session.Map.Lines.Where(l => l.OnMap && l.Start.X == -256 && l.End.X == -256).OrderBy(l => Math.Min(l.Start.Y, l.End.Y)).Select(l => $"{l.Length} {l.Front.Middle}{(l.Special == 0 ? "" : " exit")}");
                    Console.WriteLine($"exit made: heading '{editor.Inspector.Heading}', '{editor.Inspector.Summary}', switch {exit?.Slot} {exit?.Front.Middle} {exit?.Length} long; west wall {string.Join(" · ", west)}");
                    Report("exit");
                    Wait(editor.Session.FlushAsync());
                    Save("editor-make-exit");
                    break;
                }

                if (pose == "editor-make-secret")
                {
                    // A small room against the starter room's east wall, D and a click on the wall they share (a
                    // door), then the room selected: the Inspector's "Make it a secret" and "Make its door look like
                    // the wall".
                    Drag((256, -64), (384, 64), null);
                    window.KeyPress(Avalonia.Input.Key.D, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.D, "d");
                    Pump(0.2);
                    window.MouseMove(At(256, 0), Avalonia.Input.RawInputModifiers.None);
                    window.MouseDown(At(256, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(256, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(1.0);
                    Console.WriteLine($"secret door: heading '{editor.Inspector.Heading}', undo '{editor.Session.History.UndoLabel}'");
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    window.MouseDown(At(352, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(352, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.6);
                    editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Make it a secret").Command.Execute(null);
                    Pump(1.0);
                    Console.WriteLine($"secret made: heading '{editor.Inspector.Heading}', '{editor.Inspector.Summary}', undo '{editor.Session.History.UndoLabel}'");
                    editor.Inspector.Rows.OfType<ActionRow>().Single(r => r.Label == "Make its door look like the wall").Command.Execute(null);
                    Pump(1.0);
                    Console.WriteLine($"secret door hidden: undo '{editor.Session.History.UndoLabel}', rows {string.Join(" | ", editor.Inspector.Rows.Select(r => r.Label).Take(4))}");
                    Report("secret");
                    Wait(editor.Session.FlushAsync());
                    Save("editor-make-secret");
                    var session = editor.Session;
                    var scene = WadSmith.Editor.Scene.MapScene.Build(session.Geometry, n => session.Resources?.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, WadSmith.App.ThreeD.SpriteShapes.For(session.Resources));
                    File.WriteAllBytes(Path.Combine(output, "editor-secret-door.png"), WadSmith.App.ThreeD.Offscreen3D.RenderPng(scene, session.Resources, new WadSmith.App.ThreeD.Camera3D(96, 0, 41, 0, 0), 960, 600, inside: true));
                    break;
                }

                if (pose == "editor-make-window")
                {
                    // A room east of the starter room with 64 between them; W and a drag along the starter room's east
                    // wall (captured mid-drag): a window through to it. Then a room against the starter room's north
                    // wall and a click on the wall they share: a window in a thin wall.
                    Drag((320, -128), (576, 128), null);
                    Drag((-128, 256), (128, 448), null);
                    window.KeyPress(Avalonia.Input.Key.W, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.W, "w");
                    Pump(0.2);
                    Drag((256, -64), (256, 64), "editor-making-window");
                    var through = editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Sector>().SingleOrDefault();
                    var span = through is null ? null : editor.Session.Geometry.WallBoundsAt(through.Slot);
                    Console.WriteLine($"window through: area {through?.Slot} {(span is { } sp ? $"({sp.MinX:0},{sp.MinY:0})-({sp.MaxX:0},{sp.MaxY:0})" : "")}, heading '{editor.Inspector.Heading}', '{editor.Inspector.Summary}', undo '{editor.Session.History.UndoLabel}', status '{editor.Status}'");
                    window.MouseMove(At(0, 256), Avalonia.Input.RawInputModifiers.None);
                    window.MouseDown(At(0, 256), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(0, 256), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(1.0);
                    var thin = editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Sector>().SingleOrDefault();
                    var bounds = thin is null ? null : editor.Session.Geometry.WallBoundsAt(thin.Slot);
                    Console.WriteLine($"window thin: area {thin?.Slot} {(bounds is { } b ? $"({b.MinX:0},{b.MinY:0})-({b.MaxX:0},{b.MaxY:0})" : "")}, heading '{editor.Inspector.Heading}', sill {thin?.FloorHeight} top {thin?.CeilingHeight}, undo '{editor.Session.History.UndoLabel}'");
                    Report("windows");
                    Console.WriteLine($"window problems: {editor.Problems.Summary}");
                    Wait(editor.Session.FlushAsync());
                    Console.WriteLine($"window areas for the engine: {through?.Slot} {thin?.Slot}");
                    Save("editor-make-window");
                    // Each window as the player sees it from the starter room: eye height 41, facing it.
                    var session = editor.Session;
                    var scene = WadSmith.Editor.Scene.MapScene.Build(session.Geometry, n => session.Resources?.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, WadSmith.App.ThreeD.SpriteShapes.For(session.Resources));
                    File.WriteAllBytes(Path.Combine(output, "editor-window-east.png"), WadSmith.App.ThreeD.Offscreen3D.RenderPng(scene, session.Resources, new WadSmith.App.ThreeD.Camera3D(96, 0, 41, 0, 0), 960, 600, inside: true));
                    File.WriteAllBytes(Path.Combine(output, "editor-window-north.png"), WadSmith.App.ThreeD.Offscreen3D.RenderPng(scene, session.Resources, new WadSmith.App.ThreeD.Camera3D(0, 96, 41, 90, 0), 960, 600, inside: true));
                    break;
                }

                if (pose == "editor-turn")
                {
                    // A room east of the starter room, selected; its turning knob dragged a quarter turn to the left
                    // round its middle (captured halfway), its north-east handle dragged 64 east, then Shift+H.
                    Drag((512, -64), (768, 64), null);
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    window.MouseDown(At(640, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(640, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.6);
                    var room = editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Sector>().Single();
                    string Box()
                    {
                        var b = editor.Session.Geometry.WallBoundsAt(room.Slot);
                        return b is { } x ? $"({x.MinX:0},{x.MinY:0})-({x.MaxX:0},{x.MaxY:0})" : "?";
                    }

                    Avalonia.Point OnWindow(Avalonia.Point p) => view.TranslatePoint(p, window)!.Value;
                    void Hold(Avalonia.Point from, Func<double, Avalonia.Point> path, string? midShot)
                    {
                        window.MouseMove(OnWindow(from), Avalonia.Input.RawInputModifiers.None);
                        Pump(0.1);
                        window.MouseDown(OnWindow(from), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                        for (var k = 1; k <= 12; k++)
                        {
                            window.MouseMove(OnWindow(path(k / 12.0)), Avalonia.Input.RawInputModifiers.LeftMouseButton);
                            Pump(0.03);
                            if (k == 6 && midShot is not null) Save(midShot);
                        }

                        window.MouseUp(OnWindow(path(1)), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                        Pump(1.0);
                    }

                    // The knob sits above the middle of the handles' box (10 pixels outside the room, 26 above that).
                    var (nw, se) = (view.ToScreen(new WadSmith.Editor.Geometry.MapPoint(512, 64)), view.ToScreen(new WadSmith.Editor.Geometry.MapPoint(768, -64)));
                    var pivot = view.ToScreen(new WadSmith.Editor.Geometry.MapPoint(640, 0));
                    var knob = new Avalonia.Point((nw.X + se.X) / 2, nw.Y - 10 - 26);
                    var r = pivot.Y - knob.Y;
                    Hold(knob, t => new Avalonia.Point(pivot.X + r * Math.Cos(Math.PI / 2 + t * Math.PI / 2), pivot.Y - r * Math.Sin(Math.PI / 2 + t * Math.PI / 2)), "editor-turning");
                    Console.WriteLine($"turn knob: room {Box()}, undo '{editor.Session.History.UndoLabel}', status '{editor.Status}'");

                    var (ne, unit) = (view.ToScreen(new WadSmith.Editor.Geometry.MapPoint(704, 128)), view.ToScreen(new WadSmith.Editor.Geometry.MapPoint(1, 0)).X - view.ToScreen(new WadSmith.Editor.Geometry.MapPoint(0, 0)).X);
                    var grip = new Avalonia.Point(ne.X + 10, ne.Y - 10);
                    Hold(grip, t => new Avalonia.Point(grip.X + t * 64 * unit, grip.Y), "editor-resizing");
                    Console.WriteLine($"turn resize: room {Box()}, undo '{editor.Session.History.UndoLabel}'");

                    window.KeyPress(Avalonia.Input.Key.H, Avalonia.Input.RawInputModifiers.Shift, Avalonia.Input.PhysicalKey.H, "H");
                    Pump(1.0);
                    var wrong = editor.Session.Map.Lines.Count(l => l.OnMap && !ReferenceEquals(editor.Session.Geometry.SectorAt(new WadSmith.Editor.Geometry.MapPoint((l.Start.X + l.End.X) / 2 + (l.End.Y - l.Start.Y) / l.Length / 8, (l.Start.Y + l.End.Y) / 2 - (l.End.X - l.Start.X) / l.Length / 8)), l.Front.Sector));
                    Console.WriteLine($"turn mirror: room {Box()}, undo '{editor.Session.History.UndoLabel}', walls facing away from their room: {wrong}; {editor.Problems.Summary}");
                    Report("turned");
                    Save("editor-turn");
                    break;
                }

                if (pose == "editor-copy")
                {
                    // Real keys: V, a click on the starter room, Ctrl+D (a copy beside it), then Ctrl+C and Ctrl+V with
                    // the pointer further north-east (a third, where the pointer is).
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    window.MouseDown(At(0, 64), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(0, 64), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.5);
                    window.KeyPress(Avalonia.Input.Key.D, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.D, "d");
                    Pump(0.8);
                    Report("duplicated");
                    window.KeyPress(Avalonia.Input.Key.C, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.C, "c");
                    Pump(0.2);
                    window.MouseMove(At(300, 900), Avalonia.Input.RawInputModifiers.None);
                    Pump(0.2);
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.V, "v");
                    Pump(0.8);
                    Report("pasted");
                    var rooms = editor.Session.Map.Sectors.Where(r => r.OnMap).Select(r => editor.Session.Geometry.WallBoundsAt(r.Slot)).ToList();
                    Console.WriteLine($"draw rooms: {string.Join(" ", rooms.Select(b => b is { } x ? $"({x.MinX:0},{x.MinY:0})-({x.MaxX:0},{x.MaxY:0})" : "?"))}; {editor.Problems.Summary}");
                    Save("editor-copy");
                    break;
                }

                if (pose == "editor-draw-shape")
                {
                    // Corner by corner with real clicks: an L against the starter room closed on its first corner
                    // (captured halfway), then a triangle in the starter room closed with Enter.
                    void Click(double x, double y)
                    {
                        window.MouseMove(At(x, y), Avalonia.Input.RawInputModifiers.None);
                        window.MouseDown(At(x, y), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                        window.MouseUp(At(x, y), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                        Pump(0.6);
                    }

                    window.KeyPress(Avalonia.Input.Key.R, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.R, "r");
                    Pump(0.2);
                    var corners = new (double X, double Y)[] { (256, -256), (256, 256), (384, 256), (384, -128), (640, -128), (640, -256) };
                    for (var k = 0; k < corners.Length; k++)
                    {
                        Click(corners[k].X, corners[k].Y);
                        if (k == 3)
                        {
                            window.MouseMove(At(640, -100), Avalonia.Input.RawInputModifiers.None);
                            Pump(0.3);
                            Console.WriteLine($"draw status: '{editor.Status}'");
                            Save("editor-drawing-shape");
                        }
                    }

                    Click(256, -256);
                    Pump(0.8);
                    Report("L");
                    foreach (var (x, y) in new[] { (-128.0, -128.0), (0.0, 128.0), (128.0, -128.0) }) Click(x, y);
                    window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
                    Pump(0.8);
                    Report("triangle");
                    Save("editor-draw-shape");

                    // A round room south of the starter room: Ctrl held from the press, ] twice (20 sides), let go.
                    window.MouseMove(At(-128, -512), Avalonia.Input.RawInputModifiers.Control);
                    window.MouseDown(At(-128, -512), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
                    for (var k = 1; k <= 10; k++)
                    {
                        window.MouseMove(At(-128 + 384 * k / 10.0, -512 + 256 * k / 10.0), Avalonia.Input.RawInputModifiers.LeftMouseButton | Avalonia.Input.RawInputModifiers.Control);
                        Pump(0.02);
                    }

                    window.KeyPress(Avalonia.Input.Key.OemCloseBrackets, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.BracketRight, "]");
                    window.KeyPress(Avalonia.Input.Key.OemCloseBrackets, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.BracketRight, "]");
                    Pump(0.2);
                    Console.WriteLine($"draw round status: '{editor.Status}'");
                    Save("editor-drawing-round");
                    window.MouseUp(At(256, -256), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
                    Pump(0.8);
                    Report("round");
                    var roundRoom = editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Sector>().FirstOrDefault();
                    Console.WriteLine($"draw round: {editor.Session.Map.Lines.Count(l => l.OnMap && (l.Front.Sector == roundRoom || l.Back?.Sector == roundRoom))} walls round the new room");

                    // A line from the starter room's west wall round to its north wall, finished with Enter: the
                    // corner it cuts off becomes a room of its own.
                    foreach (var (x, y) in new[] { (-256.0, 64.0), (-64.0, 64.0), (-64.0, 256.0) }) Click(x, y);
                    window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
                    Pump(0.8);
                    Report("split");
                    var cut = editor.Session.Selection.Items.OfType<WadSmith.Editor.Model.Sector>().SingleOrDefault();
                    Console.WriteLine($"draw split: new room {(cut is null ? "none" : editor.Session.Geometry.WallBoundsAt(cut.Slot) is { } b ? $"({b.MinX:0},{b.MinY:0})-({b.MaxX:0},{b.MaxY:0})" : "?")}, undo '{editor.Session.History.UndoLabel}'; {editor.Problems.Summary}");
                    Save("editor-split");
                    break;
                }

                if (pose == "editor-make-teleport")
                {
                    // A pad in the starter room and a room apart; T, a click on the pad, a click in the far room:
                    // stepping on the pad sends the player there. Saved for the engine check.
                    window.KeyPress(Avalonia.Input.Key.R, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.R, "r");
                    Drag((-64, -64), (64, 64), null);
                    Drag((512, -128), (768, 128), null);
                    window.KeyPress(Avalonia.Input.Key.T, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.T, "t");
                    Pump(0.2);
                    foreach (var (x, y) in new[] { (0.0, 0.0), (640.0, 0.0) })
                    {
                        window.MouseDown(At(x, y), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                        window.MouseUp(At(x, y), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                        Pump(0.6);
                        Console.WriteLine($"draw status: '{editor.Status}'");
                    }

                    Report("teleport");
                    Console.WriteLine($"draw inspector: '{editor.Inspector.Heading}', '{editor.Inspector.Summary}'");
                    Save("editor-make-teleport");
                    Wait(editor.Session.FlushAsync());
                    Console.WriteLine($"draw project: {editor.Session.Project.Folder}");
                    break;
                }

                if (pose == "editor-make-lift")
                {
                    // A platform beside the starter room and a room beyond it, both raised to 64 through the
                    // Inspector's floor height; then Make it a lift on the platform. Saved for the engine check.
                    window.KeyPress(Avalonia.Input.Key.R, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.R, "r");
                    Drag((256, -128), (512, 128), null);
                    Drag((512, -128), (768, 128), null);
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    Pump(0.2);
                    foreach (var x in new[] { 384.0, 640.0 })
                    {
                        window.MouseDown(At(x, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                        window.MouseUp(At(x, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                        Pump(0.4);
                        editor.Inspector.Rows.OfType<NumberRow>().First(r => r.Label == "Floor height").Value = 64;
                        Pump(0.4);
                    }

                    window.MouseDown(At(384, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(384, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.6);
                    editor.Inspector.Rows.OfType<ActionRow>().First(r => r.Label == "Make it a lift").Command.Execute(null);
                    Pump(1.0);
                    Report("lift");
                    Console.WriteLine($"draw inspector: '{editor.Inspector.Heading}', '{editor.Inspector.Summary}'");
                    Save("editor-make-lift");
                    Wait(editor.Session.FlushAsync());
                    Console.WriteLine($"draw project: {editor.Session.Project.Folder}");
                    break;
                }

                if (pose == "editor-make-door")
                {
                    // A corridor 64 wide east of the starter room and a room beyond it, both drawn; then D and
                    // a click on the corridor's west end: a door there. The project is saved for the engine check.
                    window.KeyPress(Avalonia.Input.Key.R, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.R, "r");
                    Drag((256, -64), (384, 64), null);
                    Drag((384, -256), (896, 256), null);
                    Report("rooms");
                    window.KeyPress(Avalonia.Input.Key.D, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.D, "d");
                    Pump(0.2);
                    window.MouseMove(At(256, 0), Avalonia.Input.RawInputModifiers.None);
                    window.MouseDown(At(256, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(256, 0), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(1.0);
                    Report("door");
                    Console.WriteLine($"draw inspector: '{editor.Inspector.Heading}', '{editor.Inspector.Summary}'");
                    Save("editor-make-door");
                    Wait(editor.Session.FlushAsync());
                    Console.WriteLine($"draw project: {editor.Session.Project.Folder}");
                    break;
                }

                if (pose == "editor-join")
                {
                    // A room drawn apart, then dragged west until its corner snaps onto the starter room's
                    // (the ring shows it mid-drag); let go, the two share the wall between them.
                    Drag((512, -256), (1024, 256), null);
                    Report("apart");
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    Pump(0.2);
                    window.MouseDown(At(700, 100), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(700, 100), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.5);
                    Drag((700, 100), (450, 100), "editor-joining");
                    Report("joined");
                    Save("editor-join");
                    break;
                }

                Drag((256, -256), (768, 256), pose == "editor-draw" ? "editor-drawing" : null);
                Report("beside");
                if (pose == "editor-box")
                {
                    // V, a box dragged from empty space round the new room selects it; a double-click on the
                    // starter room's west wall adds a corner there, selected.
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    Pump(0.2);
                    Drag((232, -320), (800, 320), "editor-boxing");
                    Report("boxed");
                    Console.WriteLine($"draw picked: {string.Join(", ", editor.Session.Selection.Items.Select(i => i.GetType().Name))}");
                    window.MouseDown(At(-256, 40), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(-256, 40), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.1);
                    window.MouseDown(At(-256, 40), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(-256, 40), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.8);
                    Report("corner");
                    if (editor.Session.Selection.Items.FirstOrDefault() is WadSmith.Editor.Model.Vertex added) Console.WriteLine($"draw added corner at ({added.X}, {added.Y})");
                    Save("editor-box");
                    break;
                }

                if (pose == "editor-delete")
                {
                    // V, a click in the new room, the Delete key: the room goes and the shared wall is the
                    // starter room's solid wall again; Ctrl+Z brings the room back.
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    Pump(0.2);
                    window.MouseDown(At(500, 100), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(500, 100), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.5);
                    Save("editor-delete-selected");
                    window.KeyPress(Avalonia.Input.Key.Delete, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Delete, null);
                    Pump(0.8);
                    Report("deleted");
                    var m = editor.Session.Map;
                    Console.WriteLine($"draw walls: {string.Join(" ", m.Lines.Where(l => l.OnMap).Select(l => $"{(l.Flag("blocking") ? "solid" : "open")}:{l.Front.Middle}"))}");
                    Save("editor-delete");
                    window.KeyPress(Avalonia.Input.Key.Z, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Z, "z");
                    Pump(0.8);
                    Report("undone");
                    break;
                }

                if (pose == "editor-move")
                {
                    // V for Select, a click in the new room, then a drag of it 128 east (its corners
                    // shared with the starter room go too, stretching it); Ctrl+Z takes it back.
                    window.KeyPress(Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.V, "v");
                    Pump(0.2);
                    window.MouseDown(At(500, 100), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    window.MouseUp(At(500, 100), Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
                    Pump(0.5);
                    Report("selected");
                    Drag((500, 100), (628, 100), "editor-moving");
                    Report("moved");
                    var corners = editor.Session.Map.Vertices.Where(v => v.OnMap).Select(v => $"({v.X:0},{v.Y:0})");
                    Console.WriteLine("draw corners: " + string.Join(" ", corners));
                    Save("editor-move");
                    window.KeyPress(Avalonia.Input.Key.Z, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.Z, "z");
                    Pump(0.8);
                    Report("undone");
                    break;
                }

                Drag((448, -64), (576, 64), null);
                Report("inside");
                Save("editor-draw");
                break;
            }

            if (pose == "editor-problems")
            {
                // The Problems panel open on the map: what it found, then the first fix used (one undo step).
                editor.ShowProblems = true;
                for (var i = 0; i < 200 && editor.Problems.Summary == "Checking…"; i++) Pump(0.05);
                Pump(0.5);
                Console.WriteLine($"problems: {editor.Problems.Summary}");
                foreach (var row in editor.Problems.Rows) Console.WriteLine($"problems row: [{row.Kind}] {row.Sentence} (show {row.CanShow}, fix '{row.FixLabel}')");
                Save("editor-problems");
                if (editor.Problems.Rows.FirstOrDefault(r => r.CanFix) is { } fixable)
                {
                    fixable.ShowCommand.Execute(null);
                    Pump(0.5);
                    Save("editor-problems-shown");
                    fixable.FixCommand.Execute(null);
                    for (var i = 0; i < 100; i++) Pump(0.05);
                    Console.WriteLine($"problems after fixing: {editor.Problems.Summary}, undo '{editor.Session.History.UndoLabel}'");
                }

                break;
            }

            if (pose == "editor-door")
            {
                // The first door of the map, selected: the Inspector shows it as a door. Then its lock is
                // changed as the row does it, and undone.
                var geometry = editor.Session.Geometry;
                var area = editor.Session.Map.Sectors.Where(s => WadSmith.Editor.Smart.Doors.Recognise(geometry, s) is not null).OrderByDescending(s => WadSmith.Editor.Smart.Doors.Recognise(geometry, s)!.Remote.Count()).FirstOrDefault();
                if (area is null)
                {
                    Console.Error.WriteLine("No door in this map.");
                    return 1;
                }

                editor.Session.Selection.Set(area);
                Pump(1.0);
                Console.WriteLine($"door: heading '{editor.Inspector.Heading}', '{editor.Inspector.Summary}'");
                Save("editor-door");
                var locked = editor.Inspector.Rows.OfType<ChoiceRow>().First(r => r.Label == "Locked with");
                locked.Value = 1;
                Pump(0.5);
                var door = WadSmith.Editor.Smart.Doors.Recognise(editor.Session.Geometry, area)!;
                Console.WriteLine($"door locked: '{editor.Inspector.Summary}', openers {string.Join(" ", door.Openers.Select(l => $"{l.Special}({string.Join(",", l.Args)})"))}, undo '{editor.Session.History.UndoLabel}'");
                Save("editor-door-locked");
                editor.Session.History.Undo();
                Pump(0.5);
                Console.WriteLine($"door undone: '{editor.Inspector.Summary}'");
                break;
            }

            if (pose == "editor-face")
            {
                // One face of a wall between two areas, as a click in 3D selects it: a tick into its room on the
                // plan, the face alone tinted in 3D, its parts and offsets in the Inspector.
                editor.CameraMode = WadSmith.App.ThreeD.CameraMode.Orbit;
                var lines = editor.Session.Map.Lines;
                var face = lines.Where(l => l.Back is not null && l.Front.Sector.CeilingHeight > l.Back.Sector.CeilingHeight && !WadSmith.Editor.Scene.MapScene.IsSky(l.Back.Sector.CeilingTexture)).OrderByDescending(l => l.Length).Select(l => l.Front).FirstOrDefault() ?? lines[0].Front;
                editor.Session.Selection.Set(face);
                Pump(2.5);
                Save("editor-face");
                break;
            }

            if (pose == "editor-walk")
            {
                // The 3D view in Walk, as the window shows it, with the camera marked on the plan.
                editor.CameraMode = WadSmith.App.ThreeD.CameraMode.Orbit;
                Pump(2.0);
                editor.CameraMode = WadSmith.App.ThreeD.CameraMode.Walk;
                Pump(1.5);
                Save("editor-walk");
                break;
            }

            if (pose == "editor-3d")
            {
                // The 3D view's picture of the open map, drawn offscreen from above (no window needed).
                var session = editor.Session;
                var scene = WadSmith.Editor.Scene.MapScene.Build(session.Geometry, n => session.Resources?.Textures.GetWall(n) is { } w ? (w.Width, w.Height) : null, WadSmith.App.ThreeD.SpriteShapes.For(session.Resources));
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var png = WadSmith.App.ThreeD.Offscreen3D.RenderPng(scene, session.Resources, WadSmith.App.ThreeD.Offscreen3D.Overview(scene), 1280, 800);
                Console.WriteLine($"3d: {scene.Batches.Count} batches, {scene.Batches.Sum(b => b.VertexCount) / 3} triangles, {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms including start-up");
                File.WriteAllBytes(Path.Combine(output, "editor-3d.png"), png);
                Console.WriteLine(Path.Combine(output, "editor-3d.png"));

                // And as the player sees it from the start: eye height 41, facing the start's direction.
                if (scene.Things.FirstOrDefault(t => t.Type == 1) is { Element: not null } start)
                {
                    var eye = new WadSmith.App.ThreeD.Camera3D(start.X, start.Y, start.Z + 41, start.Angle, 0);
                    File.WriteAllBytes(Path.Combine(output, "editor-3d-start.png"), WadSmith.App.ThreeD.Offscreen3D.RenderPng(scene, session.Resources, eye, 1280, 800, inside: true));
                    Console.WriteLine(Path.Combine(output, "editor-3d-start.png"));
                }

                break;
            }

            if (pose == "editor-find")
            {
                // Find and replace (D58): the start room's floor picture looked for, the panel listing what it found.
                var room = editor.Session.Map.Sectors.First(s => s.OnMap);
                editor.ShowFind = true;
                editor.Find.KindIndex = 1;
                editor.Find.What = room.FloorTexture;
                editor.Find.With = "NUKAGE1";
                for (var i = 0; i < 100 && !editor.Find.AnyFound; i++) Pump(0.05);
                Save("editor-find");
            }

            if (pose == "editor-brightness")
            {
                editor.IsBrightness = true;
                Pump(0.5);
                Save("editor-brightness");
            }

            if (pose == "editor-versions")
            {
                // Raise the biggest room, keep a version, show the list, then go back to the oldest.
                var room = editor.Session.Map.Sectors.OrderByDescending(s => s.CeilingHeight - s.FloorHeight).First();
                editor.Session.History.Run(new WadSmith.Editor.Editing.SetProperty<WadSmith.Editor.Model.Sector, double>(room, "Change floor height", x => x.FloorHeight, (x, v) => x.FloorHeight = v, room.FloorHeight + 64));
                Wait(editor.KeepVersionCommand.ExecuteAsync(null));
                editor.ShowVersions = true;
                for (var i = 0; i < 100 && editor.Versions.Count < 2; i++) Pump(0.05);
                Save("editor-versions");
                Wait(editor.Versions[^1].GoBackCommand.ExecuteAsync(null));
                for (var i = 0; i < 100 && (vm.Page as EditorViewModel)?.Versions.Count < 3; i++) Pump(0.05);
                Pump(0.5);
                Save("editor-went-back");
                break;
            }

            Save(pose);
            break;
        case "perf-edit" or "editor-perf-edit":
        {
            // How long after one small edit (a room's floor raised by 8) the new shapes, the plan's picture and the 3D scene
            // arrive, 20 times: on the 100,000-wall grid (perf-edit) or the map given by --map (editor-perf-edit).
            if (pose == "perf-edit")
            {
                Wait(vm.OpenEditorAsync(StressProject(), "MAP01"));
            }
            else if (vm.Page is not EditorViewModel)
            {
                var tile = vm.Welcome.Games.First(g => map is null || g.Location.EndsWith(map.Split(':')[0], StringComparison.OrdinalIgnoreCase));
                for (var i = 0; i < 200 && tile.Maps.Count == 0; i++) Pump(0.05);
                if (map is not null) Wait(tile.Remix!.ExecuteAsync(tile.Maps.First(m => m.Slot == map.Split(':')[1])));
                else Wait(tile.NewMap!.ExecuteAsync(tile));
            }

            Pump(3.0);
            var edited = (EditorViewModel)vm.Page;
            var session = edited.Session;
            var plan = window.GetVisualDescendants().OfType<WadSmith.App.Canvas.MapCanvas>().First();
            var view3d = window.GetVisualDescendants().OfType<WadSmith.App.ThreeD.View3D>().First();
            var rooms = session.Map.Sectors.Where(s => s.OnMap).ToList();
            var (geometryMs, pictureMs, sceneMs) = (new List<double>(), new List<double>(), new List<double>());
            for (var i = 0; i < 20; i++)
            {
                var room = rooms[(int)((long)i * rooms.Count / 20)];
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                session.History.Run(new WadSmith.Editor.Editing.SetProperty<WadSmith.Editor.Model.Sector, double>(room, "Change floor height", x => x.FloorHeight, (x, v) => x.FloorHeight = v, room.FloorHeight + 8));
                var version = session.Map.Version;
                var (g, p, s3) = (-1.0, -1.0, -1.0);
                while (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds < 10_000 && (g < 0 || p < 0 || s3 < 0))
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(1);
                    var ms = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    if (g < 0 && session.Geometry.Frame.Version >= version) g = ms;
                    if (p < 0 && plan.ShownVersion >= version) p = ms;
                    if (s3 < 0 && view3d.SceneVersion >= version) s3 = ms;
                }

                (geometryMs, pictureMs, sceneMs) = ([.. geometryMs, g], [.. pictureMs, p], [.. sceneMs, s3]);
            }

            string Band(List<double> v)
            {
                var o = v.Order().ToList();
                return string.Create(CultureInfo.InvariantCulture, $"p50 {o[o.Count / 2]:0} ms, p95 {o[(int)(o.Count * 0.95)]:0} ms, max {o[^1]:0} ms");
            }

            Console.WriteLine($"perf-edit: {session.Map.Lines.Count(l => l.OnMap)} walls, {rooms.Count} areas");
            Console.WriteLine($"perf-edit, shapes: {Band(geometryMs)}");
            Console.WriteLine($"perf-edit, plan picture: {Band(pictureMs)}");
            Console.WriteLine($"perf-edit, 3D scene: {Band(sceneMs)}");
            break;
        }

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
