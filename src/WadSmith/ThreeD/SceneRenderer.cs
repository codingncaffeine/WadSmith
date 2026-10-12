using WadSmith.Core.Graphics;
using WadSmith.Editor;
using WadSmith.Editor.Scene;

namespace WadSmith.App.ThreeD;

/// <summary>
/// Draws a <see cref="MapScene"/> with OpenGL into an offscreen picture and reads it back. Lives on
/// the 3D view's render thread with its context; nothing here may be touched from another thread.
/// </summary>
/// <summary>
/// The game's light as the 3D view draws it, measured against GZDoom's own pictures (the fog-calibration corridors,
/// 2026-10-12): <paramref name="GlobVis"/> is the engine's visibility constant in its lighting equation (how fast an
/// unfogged area darkens with view depth); a fogged area's fog density per map unit is
/// (<paramref name="FogBase"/> + <paramref name="FogDark"/> × (255 − light)) × 10⁻⁵. Measured 2026-10-12 against GZDoom 4.14's
/// pictures of four corridors at light 255, 192, 128 and 64, with and without a fog colour, compared as 8-pixel blocks
/// (capture pose editor-fogcal), with the camera's pixel stretch (<see cref="Camera3D.PixelStretch"/>): visibility 58
/// brings ours within 2.4 per channel of the engine's (the bright corridor 0.9, the dark one 0.1; before the stretch
/// the bright one alone read 11.8); the fog within 5.2 (controls: no fog term 32.7, three times the density 26.6).
/// </summary>
internal readonly record struct Lighting(double GlobVis, double FogBase, double FogDark)
{
    public static readonly Lighting Measured = new(58, 0, 1);
}

internal sealed unsafe class SceneRenderer : IDisposable
{
    private const string SceneVertex = """
        #version 330 core
        in vec3 position;
        in vec2 uv;
        in float light;
        in float lightColour;
        in float fadeColour;
        uniform mat4 viewProjection;
        uniform vec2 textureSize;
        uniform vec3 eye;
        out vec2 vUv;
        out float vLight;
        out float vDistance;
        out float vDepth;
        out vec3 vWorld;
        out vec3 vLightColour;
        out vec4 vFade;
        vec3 unpack(float c)
        {
            float r = floor(c / 65536.0);
            float g = floor((c - r * 65536.0) / 256.0);
            return vec3(r, g, c - r * 65536.0 - g * 256.0) / 255.0;
        }
        void main()
        {
            gl_Position = viewProjection * vec4(position, 1.0);
            vUv = uv / textureSize;
            vLight = light / 255.0;
            vDistance = distance(position, eye);
            vDepth = gl_Position.w;
            vWorld = position;
            vLightColour = unpack(lightColour);
            vFade = vec4(unpack(fadeColour), fadeColour > 0.5 ? 1.0 : 0.0);
        }
        """;

    private const string SpriteVertex = """
        #version 330 core
        in vec3 position;
        in vec2 corner;
        in vec2 uv;
        in float light;
        in float lightColour;
        in float fadeColour;
        uniform mat4 viewProjection;
        uniform vec2 textureSize;
        uniform vec3 eye;
        uniform vec2 across;
        out vec2 vUv;
        out float vLight;
        out float vDistance;
        out float vDepth;
        out vec3 vWorld;
        out vec3 vLightColour;
        out vec4 vFade;
        vec3 unpack(float c)
        {
            float r = floor(c / 65536.0);
            float g = floor((c - r * 65536.0) / 256.0);
            return vec3(r, g, c - r * 65536.0 - g * 256.0) / 255.0;
        }
        void main()
        {
            // Turned to face the camera about its upright axis, as Doom draws sprites.
            vec3 p = position + vec3(across * corner.x, corner.y);
            gl_Position = viewProjection * vec4(p, 1.0);
            vUv = uv / textureSize;
            vLight = light / 255.0;
            vDistance = distance(p, eye);
            vWorld = p;
            vDepth = gl_Position.w;
            vLightColour = unpack(lightColour);
            vFade = vec4(unpack(fadeColour), fadeColour > 0.5 ? 1.0 : 0.0);
        }
        """;

    private const string SceneFragment = """
        #version 330 core
        in vec2 vUv;
        in float vLight;
        in float vDistance;
        in float vDepth;
        in vec3 vWorld;
        in vec3 vLightColour;
        in vec4 vFade;
        uniform sampler2D picture;
        uniform int sky;
        uniform vec3 eye;
        uniform float fade;
        uniform float globVis;
        uniform float fogBase;
        uniform float fogDark;
        uniform int overlay;
        uniform int useAlpha;
        uniform vec4 tint;
        uniform int lightCount;
        uniform vec4 lights[32];
        uniform vec4 lightColours[32];
        out vec4 colour;
        void main()
        {
            if (overlay == 1)
            {
                // The selection's tint: flat colour over the surface (a sprite's see-through pixels stay clear).
                if (useAlpha == 1 && texture(picture, vUv).a < 0.5) discard;
                colour = tint;
                return;
            }

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

            // Inside the map (fade 1): the game's own light. In an area with no fog colour, GZDoom's lighting equation (the
            // colormap index from the light level and the view depth; globVis measured against its pictures); in a fogged
            // area the light level alone, the fog taking over with distance. Outside (orbit), flat brightness, never black,
            // so the editor stays readable.
            float L = vLight * 255.0;
            float shade;
            if (fade > 0.5 && vFade.a < 0.5)
            {
                float vis = min(globVis / max(vDepth, 1.0), 24.0 / 32.0);
                shade = 1.0 - clamp(2.0 - (L + 12.0) / 128.0 - vis, 0.0, 31.0 / 32.0);
            }
            else if (fade > 0.5)
            {
                shade = vLight;
            }
            else
            {
                shade = clamp(0.22 + vLight * 0.9, 0.16, 1.0);
            }

            // The map's lights add their colour, fading to nothing at their reach (subtractive ones take it away); a soft
            // one lights each surface by the way it faces it, as GZDoom does (the surface's facing from its own slope).
            vec3 lit = vec3(shade) * vLightColour;
            vec3 n = normalize(cross(dFdx(vWorld), dFdy(vWorld)));
            if (dot(n, eye - vWorld) < 0.0) n = -n;
            for (int i = 0; i < lightCount; i++)
            {
                vec3 toLight = lights[i].xyz - vWorld;
                float d = length(toLight);
                float fall = max(0.0, 1.0 - d / lights[i].w);
                if (lightColours[i].w > 0.5) fall *= max(0.0, dot(n, toLight / max(d, 0.001)));
                lit += lightColours[i].rgb * fall;
            }

            vec3 shown = c.rgb * clamp(lit, 0.0, 1.6);

            // Fog: the area's fog colour takes over with distance, thicker the darker the area (density measured against the game).
            if (fade > 0.5 && vFade.a > 0.5)
            {
                float density = (fogBase + fogDark * (255.0 - L)) * 0.00001;
                shown = mix(vFade.rgb, shown, exp2(-density * vDistance));
            }

            colour = vec4(shown, 1.0);
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
    private readonly int _uGlobVis;
    private readonly int _uFogBase;
    private readonly int _uFogDark;
    private readonly int _sGlobVis;
    private readonly int _sFogBase;
    private readonly int _sFogDark;

    /// <summary>The game's light as this renderer draws it (see <see cref="Lighting"/>); the live view uses the measured defaults.</summary>
    public Lighting Light { get; set; } = Lighting.Measured;
    private readonly int _sprites;
    private readonly int _sViewProjection;
    private readonly int _sTextureSize;
    private readonly int _sEye;
    private readonly int _sAcross;
    private readonly int _sPicture;
    private readonly int _sFade;
    private readonly int _sSky;
    private readonly Dictionary<int, (int Id, int Width, int Height)> _spriteTextures = [];
    private readonly int _uOverlay;
    private readonly int _uUseAlpha;
    private readonly int _uTint;
    private readonly int _sOverlay;
    private readonly int _sUseAlpha;
    private readonly int _sTint;
    private (int Vao, int Vbo, int Count) _highlight;
    private (int Vao, int Vbo, int Count) _hover;
    private (int Vao, int Vbo, int Count) _ghost;
    private bool _ghostFits;
    private readonly List<(int Vao, int Vbo, int Count, int Type)> _highlightSprites = [];
    private readonly List<(int Vao, int Vbo, int Count, int Type)> _hoverSprites = [];
    private readonly List<(int Vao, int Vbo, int Count, int Type)> _spriteBatches = [];
    private readonly Dictionary<(string Name, SurfaceKind Kind), (int Id, int Width, int Height)> _textures = [];
    private readonly List<(int Vao, int Vbo, int Count, SurfaceKind Kind, string Texture)> _batches = [];
    private const int MaxLights = 32;
    private readonly int _uLightCount;
    private readonly int _uLights;
    private readonly int _uLightColours;
    private readonly int _sLightCount;
    private readonly int _sLights;
    private readonly int _sLightColours;
    private SceneLight[] _lights = [];
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
        _uGlobVis = gl.Uniform(_program, "globVis");
        _uFogBase = gl.Uniform(_program, "fogBase");
        _uFogDark = gl.Uniform(_program, "fogDark");
        _sGlobVis = gl.Uniform(_sprites, "globVis");
        _sFogBase = gl.Uniform(_sprites, "fogBase");
        _sFogDark = gl.Uniform(_sprites, "fogDark");
        _sprites = gl.Program(SpriteVertex, SceneFragment, "position", "corner", "uv", "light");
        _sViewProjection = gl.Uniform(_sprites, "viewProjection");
        _sTextureSize = gl.Uniform(_sprites, "textureSize");
        _sEye = gl.Uniform(_sprites, "eye");
        _sAcross = gl.Uniform(_sprites, "across");
        _sPicture = gl.Uniform(_sprites, "picture");
        _sFade = gl.Uniform(_sprites, "fade");
        _sSky = gl.Uniform(_sprites, "sky");
        _uOverlay = gl.Uniform(_program, "overlay");
        _uUseAlpha = gl.Uniform(_program, "useAlpha");
        _uTint = gl.Uniform(_program, "tint");
        _sOverlay = gl.Uniform(_sprites, "overlay");
        _sUseAlpha = gl.Uniform(_sprites, "useAlpha");
        _sTint = gl.Uniform(_sprites, "tint");
        _uLightCount = gl.Uniform(_program, "lightCount");
        _uLights = gl.Uniform(_program, "lights");
        _uLightColours = gl.Uniform(_program, "lightColours");
        _sLightCount = gl.Uniform(_sprites, "lightCount");
        _sLights = gl.Uniform(_sprites, "lights");
        _sLightColours = gl.Uniform(_sprites, "lightColours");
        RendererName = gl.Text(Gl.Renderer);
    }

    /// <summary>Replaces the tinted selection: wall and floor triangles in the scene's layout, sprite corners per object type.</summary>
    public void Highlight(float[] surfaces, Dictionary<int, float[]> sprites) => Replace(ref _highlight, _highlightSprites, surfaces, sprites);

    /// <summary>Replaces the hovered surface's tint (what a drop or a scroll would change): the same layout as the selection's.</summary>
    public void Hover(float[] surfaces, Dictionary<int, float[]> sprites) => Replace(ref _hover, _hoverSprites, surfaces, sprites);

    private void Replace(ref (int Vao, int Vbo, int Count) buffer, List<(int Vao, int Vbo, int Count, int Type)> spriteBuffers, float[] surfaces, Dictionary<int, float[]> sprites)
    {
        Free(ref buffer);
        foreach (var h in spriteBuffers)
        {
            var x = (h.Vao, h.Vbo, h.Count);
            Free(ref x);
        }

        spriteBuffers.Clear();
        if (surfaces.Length > 0) buffer = Upload(surfaces, SceneBatch.Stride, [3, 2, 1, 1, 1]);
        foreach (var (type, data) in sprites)
        {
            var (vao, vbo, count) = Upload(data, SpriteBatch.Stride, [3, 2, 2, 1, 1, 1]);
            spriteBuffers.Add((vao, vbo, count, type));
        }
    }

    /// <summary>The player's sprite standing where the pointer is on the plan (null for none), green where the player fits, red where not.</summary>
    public void Ghost(float[]? quad, bool fits)
    {
        Free(ref _ghost);
        if (quad is { Length: > 0 }) _ghost = Upload(quad, SpriteBatch.Stride, [3, 2, 2, 1, 1, 1]);
        _ghostFits = fits;
    }

    private (int Vao, int Vbo, int Count) Upload(float[] data, int stride, int[] sizes)
    {
        int vao, vbo;
        _gl.GenVertexArrays(1, &vao);
        _gl.BindVertexArray(vao);
        _gl.GenBuffers(1, &vbo);
        _gl.BindBuffer(Gl.ArrayBuffer, vbo);
        fixed (float* p = data) _gl.BufferData(Gl.ArrayBuffer, data.Length * sizeof(float), p, Gl.DynamicDraw);
        var offset = 0;
        for (var a = 0; a < sizes.Length; a++)
        {
            _gl.EnableVertexAttribArray(a);
            _gl.VertexAttribPointer(a, sizes[a], Gl.Float, 0, stride * sizeof(float), offset * sizeof(float));
            offset += sizes[a];
        }

        _gl.BindVertexArray(0);
        return (vao, vbo, data.Length / stride);
    }

    private void Free(ref (int Vao, int Vbo, int Count) buffer)
    {
        if (buffer.Vao == 0) return;
        var (vao, vbo) = (buffer.Vao, buffer.Vbo);
        _gl.DeleteVertexArrays(1, &vao);
        _gl.DeleteBuffers(1, &vbo);
        buffer = default;
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

        _lights = [.. scene.Lights];
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
            _gl.EnableVertexAttribArray(3);
            _gl.VertexAttribPointer(3, 1, Gl.Float, 0, stride, 6 * sizeof(float));
            _gl.EnableVertexAttribArray(4);
            _gl.VertexAttribPointer(4, 1, Gl.Float, 0, stride, 7 * sizeof(float));
            _batches.Add((vao, vbo, batch.VertexCount, batch.Kind, batch.Texture));
        }

        foreach (var b in _spriteBatches)
        {
            var vao = b.Vao;
            var vbo = b.Vbo;
            _gl.DeleteVertexArrays(1, &vao);
            _gl.DeleteBuffers(1, &vbo);
        }

        _spriteBatches.Clear();
        foreach (var batch in scene.Sprites)
        {
            int vao, vbo;
            _gl.GenVertexArrays(1, &vao);
            _gl.BindVertexArray(vao);
            _gl.GenBuffers(1, &vbo);
            _gl.BindBuffer(Gl.ArrayBuffer, vbo);
            var data = batch.Vertices.ToArray();
            fixed (float* p = data) _gl.BufferData(Gl.ArrayBuffer, data.Length * sizeof(float), p, Gl.StaticDraw);
            const int stride = SpriteBatch.Stride * sizeof(float);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, Gl.Float, 0, stride, IntPtr.Zero);
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 2, Gl.Float, 0, stride, 3 * sizeof(float));
            _gl.EnableVertexAttribArray(2);
            _gl.VertexAttribPointer(2, 2, Gl.Float, 0, stride, 5 * sizeof(float));
            _gl.EnableVertexAttribArray(3);
            _gl.VertexAttribPointer(3, 1, Gl.Float, 0, stride, 7 * sizeof(float));
            _gl.EnableVertexAttribArray(4);
            _gl.VertexAttribPointer(4, 1, Gl.Float, 0, stride, 8 * sizeof(float));
            _gl.EnableVertexAttribArray(5);
            _gl.VertexAttribPointer(5, 1, Gl.Float, 0, stride, 9 * sizeof(float));
            _spriteBatches.Add((vao, vbo, batch.VertexCount, batch.Type));
        }

        _gl.BindVertexArray(0);
    }

    /// <summary>Hands the program the lights nearest the camera (at most <see cref="MaxLights"/>), for the program in use.</summary>
    private void SetLights(Camera3D camera, int count, int positions, int colours)
    {
        var near = _lights.Length <= MaxLights ? _lights : [.. _lights.OrderBy(l => (l.X - camera.X) * (l.X - camera.X) + (l.Y - camera.Y) * (l.Y - camera.Y)).Take(MaxLights)];
        var p = stackalloc float[MaxLights * 4];
        var c = stackalloc float[MaxLights * 4];
        for (var i = 0; i < near.Length; i++)
        {
            (p[i * 4], p[i * 4 + 1], p[i * 4 + 2], p[i * 4 + 3]) = (near[i].X, near[i].Y, near[i].Z, near[i].Reach);
            (c[i * 4], c[i * 4 + 1], c[i * 4 + 2], c[i * 4 + 3]) = (near[i].R, near[i].G, near[i].B, near[i].Soft ? 1 : 0);
        }

        _gl.Uniform1i(count, near.Length);
        if (near.Length == 0) return;
        _gl.Uniform4fv(positions, near.Length, p);
        _gl.Uniform4fv(colours, near.Length, c);
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
        _gl.Uniform1f(_uGlobVis, (float)Light.GlobVis);
        _gl.Uniform1f(_uFogBase, (float)Light.FogBase);
        _gl.Uniform1f(_uFogDark, (float)Light.FogDark);
        _gl.Uniform1i(_uOverlay, 0);
        SetLights(camera, _uLightCount, _uLights, _uLightColours);
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

        // Sprites: both sides drawn (they turn to face the camera), see-through pixels dropped by the shader.
        if (_spriteBatches.Count > 0)
        {
            _gl.Disable(Gl.CullFaceMode);
            _gl.UseProgram(_sprites);
            fixed (float* m = matrix) _gl.UniformMatrix4fv(_sViewProjection, 1, 0, m);
            _gl.Uniform3f(_sEye, (float)camera.X, (float)camera.Y, (float)camera.Z);
            _gl.Uniform1f(_sGlobVis, (float)Light.GlobVis);
            _gl.Uniform1f(_sFogBase, (float)Light.FogBase);
            _gl.Uniform1f(_sFogDark, (float)Light.FogDark);
            var yaw = camera.Yaw * Math.PI / 180;
            _gl.Uniform2f(_sAcross, (float)Math.Sin(yaw), (float)-Math.Cos(yaw));
            _gl.Uniform1i(_sPicture, 0);
            _gl.Uniform1f(_sFade, inside ? 1f : 0f);
            _gl.Uniform1i(_sSky, 0);
            SetLights(camera, _sLightCount, _sLights, _sLightColours);
            _gl.Uniform1i(_sOverlay, 0);
            foreach (var (vao, _, count, type) in _spriteBatches)
            {
                var (id, w, h) = SpriteTexture(type);
                if (id == 0) continue;
                _gl.BindTexture(Gl.Texture2D, id);
                _gl.Uniform2f(_sTextureSize, w, h);
                _gl.BindVertexArray(vao);
                _gl.DrawArrays(Gl.Triangles, 0, count);
            }
        }

        // The selection, tinted in the accent colour over what was drawn (same triangles, same depth); then the hovered
        // surface, lighter, so a drop or a scroll shows what it would change.
        foreach (var (buffer, spriteBuffers, r, g, b, a) in new[] { (_highlight, _highlightSprites, 0.878f, 0.345f, 0.165f, 0.42f), (_hover, _hoverSprites, 1f, 1f, 1f, 0.2f) })
        {
            if (buffer.Count == 0 && spriteBuffers.Count == 0) continue;
            _gl.Enable(Gl.Blend);
            _gl.BlendFunc(Gl.SrcAlpha, Gl.OneMinusSrcAlpha);
            _gl.DepthMask(0);
            if (buffer.Count > 0)
            {
                _gl.Enable(Gl.CullFaceMode);
                _gl.UseProgram(_program);
                _gl.Uniform1i(_uOverlay, 1);
                _gl.Uniform1i(_uUseAlpha, 0);
                _gl.Uniform4f(_uTint, r, g, b, a);
                _gl.BindVertexArray(buffer.Vao);
                _gl.DrawArrays(Gl.Triangles, 0, buffer.Count);
                _gl.Uniform1i(_uOverlay, 0);
            }

            if (spriteBuffers.Count > 0)
            {
                _gl.Disable(Gl.CullFaceMode);
                _gl.UseProgram(_sprites);
                _gl.Uniform1i(_sOverlay, 1);
                _gl.Uniform1i(_sUseAlpha, 1);
                _gl.Uniform4f(_sTint, r, g, b, a + 0.08f);
                foreach (var (vao, _, count, type) in spriteBuffers)
                {
                    var (id, w, h) = SpriteTexture(type);
                    if (id == 0) continue;
                    _gl.BindTexture(Gl.Texture2D, id);
                    _gl.Uniform2f(_sTextureSize, w, h);
                    _gl.BindVertexArray(vao);
                    _gl.DrawArrays(Gl.Triangles, 0, count);
                }

                _gl.Uniform1i(_sOverlay, 0);
            }

            _gl.DepthMask(1);
            _gl.Disable(Gl.Blend);
        }

        // The player's ghost: the player's own picture as a flat green or red shape, hidden by walls in front of it.
        if (_ghost.Count > 0 && SpriteTexture(1) is { Id: not 0 } player)
        {
            _gl.Enable(Gl.Blend);
            _gl.BlendFunc(Gl.SrcAlpha, Gl.OneMinusSrcAlpha);
            _gl.DepthMask(0);
            _gl.Disable(Gl.CullFaceMode);
            _gl.UseProgram(_sprites);
            fixed (float* m = matrix) _gl.UniformMatrix4fv(_sViewProjection, 1, 0, m);
            _gl.Uniform3f(_sEye, (float)camera.X, (float)camera.Y, (float)camera.Z);
            var turn = camera.Yaw * Math.PI / 180;
            _gl.Uniform2f(_sAcross, (float)Math.Sin(turn), (float)-Math.Cos(turn));
            _gl.Uniform1i(_sPicture, 0);
            _gl.Uniform1i(_sOverlay, 1);
            _gl.Uniform1i(_sUseAlpha, 1);
            if (_ghostFits) _gl.Uniform4f(_sTint, 0.37f, 0.83f, 0.37f, 0.62f);
            else _gl.Uniform4f(_sTint, 0.9f, 0.28f, 0.28f, 0.7f);
            _gl.BindTexture(Gl.Texture2D, player.Id);
            _gl.Uniform2f(_sTextureSize, player.Width, player.Height);
            _gl.BindVertexArray(_ghost.Vao);
            _gl.DrawArrays(Gl.Triangles, 0, _ghost.Count);
            _gl.Uniform1i(_sOverlay, 0);
            _gl.DepthMask(1);
            _gl.Disable(Gl.Blend);
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

    private (int Id, int Width, int Height) SpriteTexture(int type)
    {
        if (_spriteTextures.TryGetValue(type, out var t)) return t;
        if (_resources?.SpriteFor(type) is not { } image) return _spriteTextures[type] = (0, 1, 1);
        int id;
        _gl.GenTextures(1, &id);
        _gl.BindTexture(Gl.Texture2D, id);
        _gl.PixelStorei(Gl.UnpackAlignment, 1);
        fixed (byte* p = image.Pixels) _gl.TexImage2D(Gl.Texture2D, 0, Gl.Rgba8, image.Width, image.Height, 0, Gl.Rgba, Gl.UnsignedByte, p);
        _gl.GenerateMipmap(Gl.Texture2D);
        _gl.TexParameteri(Gl.Texture2D, Gl.TextureMagFilter, Gl.Nearest);
        _gl.TexParameteri(Gl.Texture2D, Gl.TextureMinFilter, Gl.LinearMipmapLinear);
        _gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapS, Gl.ClampToEdge);
        _gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapT, Gl.ClampToEdge);
        return _spriteTextures[type] = (id, image.Width, image.Height);
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
        foreach (var (id, _, _) in _spriteTextures.Values)
        {
            if (id == 0) continue;
            var t = id;
            _gl.DeleteTextures(1, &t);
        }

        _spriteTextures.Clear();
    }

    public void Dispose()
    {
        Free(ref _ghost);
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
