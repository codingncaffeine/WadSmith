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
        case "editor" or "editor-heights" or "editor-ceilings" or "editor-lines" or "editor-selected" or "editor-zoomed" or "editor-versions" or "editor-3d" or "editor-walk" or "editor-3d-selected" or "editor-face" or "editor-draw" or "editor-move" or "editor-join" or "editor-delete" or "editor-box" or "editor-door" or "editor-make-door":
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

            if (pose is "editor-draw" or "editor-move" or "editor-join" or "editor-delete" or "editor-box" or "editor-make-door")
            {
                // Real key and pointer events: R picks the Room tool, a drag beside the starter room draws a
                // room sharing its east wall (captured mid-drag too), then a drag inside the new room draws
                // one inside it.
                view.ZoomBy(pose == "editor-join" ? 0.3 : 0.45);
                if (pose == "editor-join") view.PanBy(-90, 0);
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

            if (pose == "editor-door")
            {
                // The first door of the map, selected: the Inspector shows it as a door. Then its lock is
                // changed as the row does it, and undone.
                var geometry = editor.Session.Geometry;
                var area = editor.Session.Map.Sectors.FirstOrDefault(s => WadSmith.Editor.Smart.Doors.Recognise(geometry, s) is not null);
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
