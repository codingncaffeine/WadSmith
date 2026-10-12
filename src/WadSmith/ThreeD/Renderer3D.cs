using Avalonia.Media.Imaging;
using WadSmith.Editor;
using WadSmith.Editor.App;
using WadSmith.Editor.Scene;

namespace WadSmith.App.ThreeD;

/// <summary>
/// The 3D view's render thread: owns the OpenGL context, the uploaded scene and the offscreen picture,
/// and draws the newest scene from the newest camera into the next of the view's bitmaps whenever
/// either changes. The UI thread only hands over small things (a scene built by a worker, a camera,
/// bitmaps) and swaps which finished bitmap it shows.
/// </summary>
internal sealed unsafe class Renderer3D : IDisposable
{
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Lock _gate = new();
    private MapScene? _scene;
    private GameResources? _resources;
    private bool _sceneChanged;
    private Camera3D _camera;
    private bool _inside;
    private WriteableBitmap[]? _bitmaps;
    private int _width;
    private int _height;
    private bool _dirty;
    private (float[] Surfaces, Dictionary<int, float[]> Sprites)? _highlight;
    private (float[] Surfaces, Dictionary<int, float[]> Sprites)? _hover;
    private (float[]? Quad, bool Fits) _ghost;
    private bool _ghostChanged;
    private volatile bool _stopping;

    public Renderer3D()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "WadSmith 3D view" };
        _thread.Start();
    }

    /// <summary>Raised on the render thread with a bitmap holding a finished picture.</summary>
    public event Action<WriteableBitmap>? FrameReady;

    /// <summary>Raised on the render thread, once, when the 3D view cannot run here (in a sentence).</summary>
    public event Action<string>? Failed;

    public void Show(MapScene scene, GameResources? resources)
    {
        lock (_gate)
        {
            _scene = scene;
            _resources = resources;
            _sceneChanged = true;
            _dirty = true;
        }

        _wake.Set();
    }

    /// <summary>The selection's triangles to tint (from <see cref="MapScene.Highlight"/>).</summary>
    public void Highlight((float[] Surfaces, Dictionary<int, float[]> Sprites) selection)
    {
        lock (_gate)
        {
            _highlight = selection;
            _dirty = true;
        }

        _wake.Set();
    }

    /// <summary>The hovered surface's triangles to tint (from <see cref="MapScene.Surface"/>; empty for none).</summary>
    public void Hover((float[] Surfaces, Dictionary<int, float[]> Sprites) surface)
    {
        lock (_gate)
        {
            _hover = surface;
            _dirty = true;
        }

        _wake.Set();
    }

    /// <summary>The player's ghost: its sprite's triangles where the pointer is on the plan (null for none), and whether the player fits.</summary>
    public void Ghost(float[]? quad, bool fits)
    {
        lock (_gate)
        {
            _ghost = (quad, fits);
            _ghostChanged = true;
            _dirty = true;
        }

        _wake.Set();
    }

    public void Look(Camera3D camera, bool inside)
    {
        lock (_gate)
        {
            _camera = camera;
            _inside = inside;
            _dirty = true;
        }

        _wake.Set();
    }

    /// <summary>The bitmaps to draw into, all of the view's size in pixels (three, so the one on screen is never written).</summary>
    public void Target(WriteableBitmap[] bitmaps, int width, int height)
    {
        lock (_gate)
        {
            _bitmaps = bitmaps;
            _width = width;
            _height = height;
            _dirty = true;
        }

        _wake.Set();
    }

    private void Run()
    {
        EglContext context;
        Gl gl;
        SceneRenderer renderer;
        try
        {
            context = EglContext.Create();
            context.MakeCurrent();
            gl = new Gl(EglContext.ProcAddress);
            renderer = new SceneRenderer(gl);
            Log.Info($"3D view: OpenGL on {renderer.RendererName} ({context.Description})");
        }
        catch (InvalidOperationException ex)
        {
            Log.Warn($"3D view unavailable: {ex.Message}");
            Failed?.Invoke(ex.Message);
            return;
        }

        var next = 0;
        byte[]? scratch = null;
        try
        {
            while (!_stopping)
            {
                _wake.WaitOne();
                if (_stopping) break;
                MapScene? scene;
                GameResources? resources;
                bool sceneChanged;
                Camera3D camera;
                bool inside;
                WriteableBitmap[]? bitmaps;
                int width, height;
                (float[] Surfaces, Dictionary<int, float[]> Sprites)? highlight;
                (float[] Surfaces, Dictionary<int, float[]> Sprites)? hover;
                (float[]? Quad, bool Fits)? ghost;
                lock (_gate)
                {
                    if (!_dirty) continue;
                    _dirty = false;
                    (scene, resources, sceneChanged, camera, inside, bitmaps, width, height) = (_scene, _resources, _sceneChanged, _camera, _inside, _bitmaps, _width, _height);
                    _sceneChanged = false;
                    highlight = _highlight;
                    _highlight = null;
                    hover = _hover;
                    _hover = null;
                    ghost = _ghostChanged ? _ghost : null;
                    _ghostChanged = false;
                }

                if (sceneChanged && scene is not null) renderer.Upload(scene, resources);
                if (highlight is { } h) renderer.Highlight(h.Surfaces, h.Sprites);
                if (hover is { } hv) renderer.Hover(hv.Surfaces, hv.Sprites);
                if (ghost is { } gh) renderer.Ghost(gh.Quad, gh.Fits);
                if (scene is null || bitmaps is null || width < 1 || height < 1) continue;

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var bitmap = bitmaps[next];
                next = (next + 1) % bitmaps.Length;
                using (var fb = bitmap.Lock())
                {
                    if (fb.Size.Width != width || fb.Size.Height != height) continue;
                    if (fb.RowBytes == width * 4)
                    {
                        renderer.Render(camera, width, height, (byte*)fb.Address, inside);
                    }
                    else
                    {
                        scratch ??= new byte[width * height * 4];
                        if (scratch.Length != width * height * 4) scratch = new byte[width * height * 4];
                        fixed (byte* s = scratch) renderer.Render(camera, width, height, s, inside);
                        for (var y = 0; y < height; y++) System.Runtime.InteropServices.Marshal.Copy(scratch, y * width * 4, fb.Address + y * fb.RowBytes, width * 4);
                    }
                }

                Canvas.FrameStats.Frame3D(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                FrameReady?.Invoke(bitmap);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or AccessViolationException)
        {
            Log.Error("The 3D view stopped", ex);
            Failed?.Invoke("The 3D view stopped: " + ex.Message);
        }
        finally
        {
            renderer.Dispose();
            context.Dispose();
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _wake.Dispose();
    }
}
