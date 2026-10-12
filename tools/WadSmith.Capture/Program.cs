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
        case "editor" or "editor-heights" or "editor-ceilings" or "editor-lines" or "editor-selected" or "editor-zoomed" or "editor-versions" or "editor-3d" or "editor-walk" or "editor-3d-selected" or "editor-face" or "editor-draw" or "editor-move" or "editor-join" or "editor-delete" or "editor-box" or "editor-door" or "editor-make-door" or "editor-shelves" or "editor-make-lift" or "editor-make-teleport" or "editor-problems" or "editor-draw-shape" or "editor-copy" or "editor-make-stairs" or "editor-turn" or "editor-make-window" or "editor-make-secret" or "editor-make-switch" or "editor-rooms" or "editor-key" or "editor-make-light" or "editor-measure" or "editor-looks" or "editor-prefab" or "editor-fit":
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

            if (pose is "editor-draw" or "editor-move" or "editor-join" or "editor-delete" or "editor-box" or "editor-make-door" or "editor-shelves" or "editor-make-lift" or "editor-make-teleport" or "editor-draw-shape" or "editor-copy" or "editor-make-stairs" or "editor-turn" or "editor-make-window" or "editor-make-secret" or "editor-make-switch" or "editor-rooms" or "editor-key" or "editor-make-light" or "editor-measure" or "editor-looks" or "editor-prefab")
            {
                // Real key and pointer events: R picks the Room tool, a drag beside the starter room draws a
                // room sharing its east wall (captured mid-drag too), then a drag inside the new room draws
                // one inside it.
                var wide = pose is "editor-join" or "editor-make-lift" or "editor-make-teleport" or "editor-draw-shape" or "editor-copy" or "editor-make-stairs" or "editor-turn" or "editor-make-window" or "editor-make-secret" or "editor-make-switch" or "editor-rooms" or "editor-key" or "editor-measure" or "editor-looks" or "editor-prefab";
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
