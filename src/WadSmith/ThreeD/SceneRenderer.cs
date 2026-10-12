using WadSmith.Core.Graphics;
using WadSmith.Editor;
using WadSmith.Editor.Scene;

namespace WadSmith.App.ThreeD;

/// <summary>
/// Draws a <see cref="MapScene"/> with OpenGL into an offscreen picture and reads it back. Lives on
/// the 3D view's render thread with its context; nothing here may be touched from another thread.
/// </summary>
internal sealed unsafe class SceneRenderer : IDisposable
{
    private const string SceneVertex = """
        #version 330 core
        in vec3 position;
        in vec2 uv;
        in float light;
        uniform mat4 viewProjection;
        uniform vec2 textureSize;
        uniform vec3 eye;
        out vec2 vUv;
        out float vLight;
        out float vDistance;
        out vec3 vWorld;
        void main()
        {
            gl_Position = viewProjection * vec4(position, 1.0);
            vUv = uv / textureSize;
            vLight = light / 255.0;
            vDistance = distance(position, eye);
            vWorld = position;
        }
        """;

    private const string SceneFragment = """
        #version 330 core
        in vec2 vUv;
        in float vLight;
        in float vDistance;
        in vec3 vWorld;
        uniform sampler2D picture;
        uniform int sky;
        uniform vec3 eye;
        uniform float fade;
        out vec4 colour;
        void main()
        {
            if (sky == 1)
            {
                // The sky wraps four times around the view and hangs from the horizon, as in the game.
                vec3 d = normalize(vWorld - eye);
                float u = atan(d.y, d.x) / 6.2831853 * -4.0;
                float v = clamp(0.62 - d.z * 0.9, 0.0, 0.999);
                colour = vec4(texture(picture, vec2(u, v)).rgb, 1.0);
                return;
            }

            vec4 c = texture(picture, vUv);
            if (c.a < 0.5) discard;

            // Each area at its own brightness; inside the map (fade 1) also darker with distance, more so in
            // dark rooms, as Doom lights. Never black, so the editor stays readable.
            float shade = clamp(0.22 + vLight * 0.9 - fade * vDistance / 3072.0 * (1.15 - vLight), 0.16, 1.0);
            colour = vec4(c.rgb * shade, 1.0);
        }
        """;

    private readonly Gl _gl;
    private readonly int _program;
    private readonly int _uViewProjection;
    private readonly int _uTextureSize;
    private readonly int _uEye;
    private readonly int _uSky;
    private readonly int _uPicture;
    private readonly int _uFade;
    private readonly Dictionary<(string Name, SurfaceKind Kind), (int Id, int Width, int Height)> _textures = [];
    private readonly List<(int Vao, int Vbo, int Count, SurfaceKind Kind, string Texture)> _batches = [];
    private int _framebuffer;
    private int _colour;
    private int _depth;
    private int _width;
    private int _height;
    private GameResources? _resources;

    public SceneRenderer(Gl gl)
    {
        _gl = gl;
        _program = gl.Program(SceneVertex, SceneFragment, "position", "uv", "light");
        _uViewProjection = gl.Uniform(_program, "viewProjection");
        _uTextureSize = gl.Uniform(_program, "textureSize");
        _uEye = gl.Uniform(_program, "eye");
        _uSky = gl.Uniform(_program, "sky");
        _uPicture = gl.Uniform(_program, "picture");
        _uFade = gl.Uniform(_program, "fade");
        RendererName = gl.Text(Gl.Renderer);
    }

    /// <summary>The GPU (or software renderer) OpenGL reports, for the log.</summary>
    public string RendererName { get; }

    /// <summary>Replaces the scene: uploads its triangles; textures load the first time a batch needs them.</summary>
    public void Upload(MapScene scene, GameResources? resources)
    {
        if (!ReferenceEquals(resources, _resources)) ForgetTextures();
        _resources = resources;
        foreach (var b in _batches)
        {
            var vao = b.Vao;
            var vbo = b.Vbo;
            _gl.DeleteVertexArrays(1, &vao);
            _gl.DeleteBuffers(1, &vbo);
        }

        _batches.Clear();
        foreach (var batch in scene.Batches)
        {
            int vao, vbo;
            _gl.GenVertexArrays(1, &vao);
            _gl.BindVertexArray(vao);
            _gl.GenBuffers(1, &vbo);
            _gl.BindBuffer(Gl.ArrayBuffer, vbo);
            var data = batch.Vertices.ToArray();
            fixed (float* p = data) _gl.BufferData(Gl.ArrayBuffer, data.Length * sizeof(float), p, Gl.StaticDraw);
            const int stride = SceneBatch.Stride * sizeof(float);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, Gl.Float, 0, stride, IntPtr.Zero);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 2, Gl.Float, 0, stride, 3 * sizeof(float));
            _gl.EnableVertexAttribArray(2);
            _gl.VertexAttribPointer(2, 1, Gl.Float, 0, stride, 5 * sizeof(float));
            _batches.Add((vao, vbo, batch.VertexCount, batch.Kind, batch.Texture));
        }

        _gl.BindVertexArray(0);
    }

    /// <summary>Draws the scene from the camera into <paramref name="target"/> (BGRA, top row first, width × 4 bytes a row).</summary>
    /// <param name="camera">Where to look from.</param>
    /// <param name="width">The picture's width in pixels.</param>
    /// <param name="height">The picture's height in pixels.</param>
    /// <param name="target">Where the picture goes.</param>
    /// <param name="inside">Looking from inside the map (fly, walk): light falls off with distance as in the game.</param>
    public void Render(Camera3D camera, int width, int height, byte* target, bool inside = false)
    {
        EnsureFramebuffer(width, height);
        _gl.BindFramebuffer(Gl.Framebuffer, _framebuffer);
        _gl.Viewport(0, 0, width, height);
        _gl.ClearColor(0.039f, 0.027f, 0.031f, 1f);
        _gl.Clear(Gl.ColorBufferBit | Gl.DepthBufferBit);
        _gl.Enable(Gl.DepthTest);
        _gl.DepthFunc(Gl.Lequal);
        _gl.Enable(Gl.CullFaceMode);
        _gl.CullFace(Gl.Back);

        // Drawn upside down so the read-back rows come top first; that also turns the winding around.
        _gl.FrontFace(0x0900);
        _gl.UseProgram(_program);
        var matrix = camera.ViewProjection(width / (double)Math.Max(height, 1), flipY: true);
        fixed (float* m = matrix) _gl.UniformMatrix4fv(_uViewProjection, 1, 0, m);
        _gl.Uniform3f(_uEye, (float)camera.X, (float)camera.Y, (float)camera.Z);
        _gl.Uniform1i(_uPicture, 0);
        _gl.Uniform1f(_uFade, inside ? 1f : 0f);
        _gl.ActiveTexture(Gl.Texture0);

        foreach (var (vao, _, count, kind, texture) in _batches)
        {
            var (id, w, h) = TextureFor(texture, kind);
            _gl.BindTexture(Gl.Texture2D, id);
            _gl.Uniform2f(_uTextureSize, w, h);
            _gl.Uniform1i(_uSky, kind == SurfaceKind.Sky ? 1 : 0);
            _gl.BindVertexArray(vao);
            _gl.DrawArrays(Gl.Triangles, 0, count);
        }

        _gl.BindVertexArray(0);
        _gl.PixelStorei(Gl.PackAlignment, 4);
        _gl.ReadPixels(0, 0, width, height, Gl.Bgra, Gl.UnsignedByte, target);
        _gl.BindFramebuffer(Gl.Framebuffer, 0);
    }

    private (int Id, int Width, int Height) TextureFor(string name, SurfaceKind kind)
    {
        var key = (name, kind == SurfaceKind.Wall ? SurfaceKind.Wall : kind == SurfaceKind.Sky ? SurfaceKind.Sky : SurfaceKind.Floor);
        if (_textures.TryGetValue(key, out var t)) return t;
        RgbaImage? image = null;
        if (_resources is { } r)
        {
            image = key.Item2 switch
            {
                SurfaceKind.Wall => r.Textures.GetWall(name),
                SurfaceKind.Sky => r.Textures.GetWall("SKY1"),
                _ => r.Flat(name),
            };
        }

        image ??= Missing();
        int id;
        _gl.GenTextures(1, &id);
        _gl.BindTexture(Gl.Texture2D, id);
        _gl.PixelStorei(Gl.UnpackAlignment, 1);
        fixed (byte* p = image.Pixels) _gl.TexImage2D(Gl.Texture2D, 0, Gl.Rgba8, image.Width, image.Height, 0, Gl.Rgba, Gl.UnsignedByte, p);
        _gl.GenerateMipmap(Gl.Texture2D);

        // Sharp up close, as the game draws it; smoothed far away.
        _gl.TexParameteri(Gl.Texture2D, Gl.TextureMagFilter, Gl.Nearest);
        _gl.TexParameteri(Gl.Texture2D, Gl.TextureMinFilter, Gl.LinearMipmapLinear);
        _gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapS, Gl.Repeat);
        _gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapT, key.Item2 == SurfaceKind.Sky ? Gl.ClampToEdge : Gl.Repeat);
        t = (id, image.Width, image.Height);
        _textures[key] = t;
        return t;
    }

    /// <summary>The picture for a texture the game does not have: a loud checkerboard, as other editors show.</summary>
    private static RgbaImage Missing()
    {
        var image = new RgbaImage(64, 64);
        for (var y = 0; y < 64; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var on = ((x / 16) + (y / 16)) % 2 == 0;
                image.Set(x, y, on ? (byte)0xE0 : (byte)0x20, on ? (byte)0x30 : (byte)0x10, on ? (byte)0xC0 : (byte)0x20);
            }
        }

        return image;
    }

    private void EnsureFramebuffer(int width, int height)
    {
        if (width == _width && height == _height && _framebuffer != 0) return;
        DeleteFramebuffer();
        _width = width;
        _height = height;
        int fb, colour, depth;
        _gl.GenFramebuffers(1, &fb);
        _gl.BindFramebuffer(Gl.Framebuffer, fb);
        _gl.GenTextures(1, &colour);
        _gl.BindTexture(Gl.Texture2D, colour);
        _gl.TexImage2D(Gl.Texture2D, 0, Gl.Rgba8, width, height, 0, Gl.Rgba, Gl.UnsignedByte, null);
        _gl.TexParameteri(Gl.Texture2D, Gl.TextureMinFilter, Gl.Linear);
        _gl.FramebufferTexture2D(Gl.Framebuffer, Gl.ColorAttachment0, Gl.Texture2D, colour, 0);
        _gl.GenRenderbuffers(1, &depth);
        _gl.BindRenderbuffer(Gl.Renderbuffer, depth);
        _gl.RenderbufferStorage(Gl.Renderbuffer, Gl.DepthComponent24, width, height);
        _gl.FramebufferRenderbuffer(Gl.Framebuffer, Gl.DepthAttachment, Gl.Renderbuffer, depth);
        if (_gl.CheckFramebufferStatus(Gl.Framebuffer) != Gl.FramebufferComplete) throw new InvalidOperationException("The 3D view's picture could not be set up on this GPU.");
        (_framebuffer, _colour, _depth) = (fb, colour, depth);
        _gl.BindFramebuffer(Gl.Framebuffer, 0);
    }

    private void DeleteFramebuffer()
    {
        if (_framebuffer == 0) return;
        int fb = _framebuffer, colour = _colour, depth = _depth;
        _gl.DeleteFramebuffers(1, &fb);
        _gl.DeleteTextures(1, &colour);
        _gl.DeleteRenderbuffers(1, &depth);
        _framebuffer = 0;
    }

    private void ForgetTextures()
    {
        foreach (var (id, _, _) in _textures.Values)
        {
            var t = id;
            _gl.DeleteTextures(1, &t);
        }

        _textures.Clear();
    }

    public void Dispose()
    {
        ForgetTextures();
        DeleteFramebuffer();
        foreach (var b in _batches)
        {
            var vao = b.Vao;
            var vbo = b.Vbo;
            _gl.DeleteVertexArrays(1, &vao);
            _gl.DeleteBuffers(1, &vbo);
        }
    }
}
