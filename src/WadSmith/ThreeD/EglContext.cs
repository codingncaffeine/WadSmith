using System.Runtime.InteropServices;

namespace WadSmith.App.ThreeD;

/// <summary>
/// An OpenGL 3.3 core context of WadSmith's own, with no window, for the 3D view's render thread (D46):
/// the editor's 3D picture is drawn off the UI thread and handed over as a finished image. Uses
/// Mesa's and NVIDIA's surfaceless EGL platform where offered, else the default display with a tiny
/// pbuffer. Make it current on the thread that will use it, and only there.
/// </summary>
internal sealed unsafe partial class EglContext : IDisposable
{
    private const string Library = "libEGL.so.1";
    private const int None = 0x3038;
    private const int Extensions = 0x3055;
    private const uint PlatformSurfacelessMesa = 0x31DD;
    private const uint OpenGlApi = 0x30A2;
    private const int SurfaceType = 0x3033;
    private const int PbufferBit = 0x0001;
    private const int RenderableType = 0x3040;
    private const int OpenGlBit = 0x0008;
    private const int RedSize = 0x3024;
    private const int GreenSize = 0x3023;
    private const int BlueSize = 0x3022;
    private const int AlphaSize = 0x3021;
    private const int ContextMajorVersion = 0x3098;
    private const int ContextMinorVersion = 0x30FB;
    private const int ContextOpenGlProfileMask = 0x30FD;
    private const int ContextOpenGlCoreProfileBit = 0x1;
    private const int Width = 0x3057;
    private const int Height = 0x3056;

    private readonly IntPtr _display;
    private readonly IntPtr _context;
    private readonly IntPtr _surface;

    private EglContext(IntPtr display, IntPtr context, IntPtr surface, string description)
    {
        _display = display;
        _context = context;
        _surface = surface;
        Description = description;
    }

    /// <summary>"surfaceless" or "pbuffer": how the context was made, for the log.</summary>
    public string Description { get; }

    /// <summary>Makes a context, or says in a sentence why none could be made.</summary>
    public static EglContext Create()
    {
        try
        {
            var clientExtensions = Marshal.PtrToStringAnsi(eglQueryString(IntPtr.Zero, Extensions)) ?? "";
            var display = IntPtr.Zero;
            var surfaceless = false;
            if (clientExtensions.Contains("EGL_MESA_platform_surfaceless", StringComparison.Ordinal))
            {
                display = eglGetPlatformDisplay(PlatformSurfacelessMesa, IntPtr.Zero, null);
                surfaceless = display != IntPtr.Zero;
            }

            if (display == IntPtr.Zero) display = eglGetDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero || eglInitialize(display, out _, out _) == 0) throw new InvalidOperationException($"EGL could not be started (error 0x{eglGetError():X}).");
            if (eglBindAPI(OpenGlApi) == 0) throw new InvalidOperationException("EGL has no desktop OpenGL.");

            var displayExtensions = Marshal.PtrToStringAnsi(eglQueryString(display, Extensions)) ?? "";
            var noSurface = displayExtensions.Contains("EGL_KHR_surfaceless_context", StringComparison.Ordinal);
            int* configAttributes = stackalloc int[] { RenderableType, OpenGlBit, SurfaceType, noSurface ? 0 : PbufferBit, RedSize, 8, GreenSize, 8, BlueSize, 8, AlphaSize, 8, None };
            IntPtr config;
            if (eglChooseConfig(display, configAttributes, &config, 1, out var count) == 0 || count == 0) throw new InvalidOperationException("EGL offers no OpenGL configuration.");

            int* contextAttributes = stackalloc int[] { ContextMajorVersion, 3, ContextMinorVersion, 3, ContextOpenGlProfileMask, ContextOpenGlCoreProfileBit, None };
            var context = eglCreateContext(display, config, IntPtr.Zero, contextAttributes);
            if (context == IntPtr.Zero) throw new InvalidOperationException($"OpenGL 3.3 is not available (EGL error 0x{eglGetError():X}).");

            var surface = IntPtr.Zero;
            if (!noSurface)
            {
                int* pbufferAttributes = stackalloc int[] { Width, 1, Height, 1, None };
                surface = eglCreatePbufferSurface(display, config, pbufferAttributes);
            }

            return new EglContext(display, context, surface, surfaceless ? "surfaceless" : noSurface ? "default display, no surface" : "default display, pbuffer");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException("EGL (libEGL.so.1) is not installed.", ex);
        }
    }

    public void MakeCurrent()
    {
        if (eglMakeCurrent(_display, _surface, _surface, _context) == 0) throw new InvalidOperationException($"The 3D view's OpenGL context could not be used (EGL error 0x{eglGetError():X}).");
    }

    /// <summary>The address of an OpenGL function, or zero.</summary>
    public static IntPtr ProcAddress(string name) => eglGetProcAddress(name);

    public void Dispose()
    {
        eglMakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        eglDestroyContext(_display, _context);
        if (_surface != IntPtr.Zero) eglDestroySurface(_display, _surface);
    }

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr eglGetProcAddress(string name);

    [LibraryImport(Library)]
    private static partial IntPtr eglQueryString(IntPtr display, int name);

    [LibraryImport(Library)]
    private static partial IntPtr eglGetPlatformDisplay(uint platform, IntPtr nativeDisplay, IntPtr* attributes);

    [LibraryImport(Library)]
    private static partial IntPtr eglGetDisplay(IntPtr nativeDisplay);

    [LibraryImport(Library)]
    private static partial uint eglInitialize(IntPtr display, out int major, out int minor);

    [LibraryImport(Library)]
    private static partial uint eglBindAPI(uint api);

    [LibraryImport(Library)]
    private static partial uint eglChooseConfig(IntPtr display, int* attributes, IntPtr* configs, int size, out int count);

    [LibraryImport(Library)]
    private static partial IntPtr eglCreateContext(IntPtr display, IntPtr config, IntPtr share, int* attributes);

    [LibraryImport(Library)]
    private static partial IntPtr eglCreatePbufferSurface(IntPtr display, IntPtr config, int* attributes);

    [LibraryImport(Library)]
    private static partial uint eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);

    [LibraryImport(Library)]
    private static partial uint eglDestroyContext(IntPtr display, IntPtr context);

    [LibraryImport(Library)]
    private static partial uint eglDestroySurface(IntPtr display, IntPtr surface);

    [LibraryImport(Library)]
    private static partial int eglGetError();
}
