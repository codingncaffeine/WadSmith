using System.Runtime.InteropServices;

namespace WadSmith.App.ThreeD;

/// <summary>The OpenGL 3.3 calls the 3D view uses, loaded once a context is current on the render thread.</summary>
internal sealed unsafe class Gl
{
    public const int Texture2D = 0x0DE1;
    public const int ArrayBuffer = 0x8892;
    public const int StaticDraw = 0x88E4;
    public const int DynamicDraw = 0x88E8;
    public const int Float = 0x1406;
    public const int UnsignedByte = 0x1401;
    public const int Triangles = 0x0004;
    public const int VertexShader = 0x8B31;
    public const int FragmentShader = 0x8B30;
    public const int CompileStatus = 0x8B81;
    public const int LinkStatus = 0x8B82;
    public const int ColorBufferBit = 0x4000;
    public const int DepthBufferBit = 0x0100;
    public const int DepthTest = 0x0B71;
    public const int CullFaceMode = 0x0B44;
    public const int Blend = 0x0BE2;
    public const int Back = 0x0405;
    public const int Ccw = 0x0901;
    public const int Lequal = 0x0203;
    public const int SrcAlpha = 0x0302;
    public const int OneMinusSrcAlpha = 0x0303;
    public const int Rgba = 0x1908;
    public const int Bgra = 0x80E1;
    public const int Rgba8 = 0x8058;
    public const int DepthComponent24 = 0x81A6;
    public const int TextureMinFilter = 0x2801;
    public const int TextureMagFilter = 0x2800;
    public const int TextureWrapS = 0x2802;
    public const int TextureWrapT = 0x2803;
    public const int Nearest = 0x2600;
    public const int Linear = 0x2601;
    public const int LinearMipmapLinear = 0x2703;
    public const int Repeat = 0x2901;
    public const int ClampToEdge = 0x812F;
    public const int Framebuffer = 0x8D40;
    public const int Renderbuffer = 0x8D41;
    public const int ColorAttachment0 = 0x8CE0;
    public const int DepthAttachment = 0x8D00;
    public const int FramebufferComplete = 0x8CD5;
    public const int Texture0 = 0x84C0;
    public const int PackAlignment = 0x0D05;
    public const int UnpackAlignment = 0x0CF5;
    public const int Renderer = 0x1F01;
    public const int Version = 0x1F02;

    public readonly delegate* unmanaged<int, IntPtr> GetString;
    public readonly delegate* unmanaged<int, int, int, int, void> Viewport;
    public readonly delegate* unmanaged<float, float, float, float, void> ClearColor;
    public readonly delegate* unmanaged<int, void> Clear;
    public readonly delegate* unmanaged<int, void> Enable;
    public readonly delegate* unmanaged<int, void> Disable;
    public readonly delegate* unmanaged<int, void> DepthFunc;
    public readonly delegate* unmanaged<byte, void> DepthMask;
    public readonly delegate* unmanaged<int, void> CullFace;
    public readonly delegate* unmanaged<int, void> FrontFace;
    public readonly delegate* unmanaged<int, int, void> BlendFunc;
    public readonly delegate* unmanaged<int, int*, void> GenBuffers;
    public readonly delegate* unmanaged<int, int, void> BindBuffer;
    public readonly delegate* unmanaged<int, IntPtr, void*, int, void> BufferData;
    public readonly delegate* unmanaged<int, int*, void> DeleteBuffers;
    public readonly delegate* unmanaged<int, int*, void> GenVertexArrays;
    public readonly delegate* unmanaged<int, void> BindVertexArray;
    public readonly delegate* unmanaged<int, int*, void> DeleteVertexArrays;
    public readonly delegate* unmanaged<int, int, int, byte, int, IntPtr, void> VertexAttribPointer;
    public readonly delegate* unmanaged<int, void> EnableVertexAttribArray;
    public readonly delegate* unmanaged<int, int> CreateShader;
    public readonly delegate* unmanaged<int, int, byte**, int*, void> ShaderSource;
    public readonly delegate* unmanaged<int, void> CompileShader;
    public readonly delegate* unmanaged<int, int, int*, void> GetShaderiv;
    public readonly delegate* unmanaged<int, int, int*, byte*, void> GetShaderInfoLog;
    public readonly delegate* unmanaged<int, void> DeleteShader;
    public readonly delegate* unmanaged<int> CreateProgram;
    public readonly delegate* unmanaged<int, int, void> AttachShader;
    public readonly delegate* unmanaged<int, int, byte*, void> BindAttribLocation;
    public readonly delegate* unmanaged<int, void> LinkProgram;
    public readonly delegate* unmanaged<int, int, int*, void> GetProgramiv;
    public readonly delegate* unmanaged<int, int, int*, byte*, void> GetProgramInfoLog;
    public readonly delegate* unmanaged<int, void> UseProgram;
    public readonly delegate* unmanaged<int, byte*, int> GetUniformLocation;
    public readonly delegate* unmanaged<int, int, void> Uniform1i;
    public readonly delegate* unmanaged<int, float, void> Uniform1f;
    public readonly delegate* unmanaged<int, float, float, void> Uniform2f;
    public readonly delegate* unmanaged<int, float, float, float, void> Uniform3f;
    public readonly delegate* unmanaged<int, float, float, float, float, void> Uniform4f;
    public readonly delegate* unmanaged<int, int, float*, void> Uniform4fv;
    public readonly delegate* unmanaged<int, int, byte, float*, void> UniformMatrix4fv;
    public readonly delegate* unmanaged<int, int*, void> GenTextures;
    public readonly delegate* unmanaged<int, int, void> BindTexture;
    public readonly delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void> TexImage2D;
    public readonly delegate* unmanaged<int, int, int, void> TexParameteri;
    public readonly delegate* unmanaged<int, void> GenerateMipmap;
    public readonly delegate* unmanaged<int, void> ActiveTexture;
    public readonly delegate* unmanaged<int, int*, void> DeleteTextures;
    public readonly delegate* unmanaged<int, int*, void> GenFramebuffers;
    public readonly delegate* unmanaged<int, int, void> BindFramebuffer;
    public readonly delegate* unmanaged<int, int, int, int, int, void> FramebufferTexture2D;
    public readonly delegate* unmanaged<int, int, int, int, void> FramebufferRenderbuffer;
    public readonly delegate* unmanaged<int, int> CheckFramebufferStatus;
    public readonly delegate* unmanaged<int, int*, void> DeleteFramebuffers;
    public readonly delegate* unmanaged<int, int*, void> GenRenderbuffers;
    public readonly delegate* unmanaged<int, int, void> BindRenderbuffer;
    public readonly delegate* unmanaged<int, int, int, int, void> RenderbufferStorage;
    public readonly delegate* unmanaged<int, int*, void> DeleteRenderbuffers;
    public readonly delegate* unmanaged<int, int, int, int, int, int, void*, void> ReadPixels;
    public readonly delegate* unmanaged<int, int, void> PixelStorei;
    public readonly delegate* unmanaged<int, int, int, void> DrawArrays;
    public readonly delegate* unmanaged<void> Finish;
    public readonly delegate* unmanaged<int> GetError;

    public Gl(Func<string, IntPtr> load)
    {
        IntPtr F(string name) => load(name) is var p && p != IntPtr.Zero ? p : throw new InvalidOperationException($"OpenGL has no {name}.");
        GetString = (delegate* unmanaged<int, IntPtr>)F("glGetString");
        Viewport = (delegate* unmanaged<int, int, int, int, void>)F("glViewport");
        ClearColor = (delegate* unmanaged<float, float, float, float, void>)F("glClearColor");
        Clear = (delegate* unmanaged<int, void>)F("glClear");
        Enable = (delegate* unmanaged<int, void>)F("glEnable");
        Disable = (delegate* unmanaged<int, void>)F("glDisable");
        DepthFunc = (delegate* unmanaged<int, void>)F("glDepthFunc");
        DepthMask = (delegate* unmanaged<byte, void>)F("glDepthMask");
        CullFace = (delegate* unmanaged<int, void>)F("glCullFace");
        FrontFace = (delegate* unmanaged<int, void>)F("glFrontFace");
        BlendFunc = (delegate* unmanaged<int, int, void>)F("glBlendFunc");
        GenBuffers = (delegate* unmanaged<int, int*, void>)F("glGenBuffers");
        BindBuffer = (delegate* unmanaged<int, int, void>)F("glBindBuffer");
        BufferData = (delegate* unmanaged<int, IntPtr, void*, int, void>)F("glBufferData");
        DeleteBuffers = (delegate* unmanaged<int, int*, void>)F("glDeleteBuffers");
        GenVertexArrays = (delegate* unmanaged<int, int*, void>)F("glGenVertexArrays");
        BindVertexArray = (delegate* unmanaged<int, void>)F("glBindVertexArray");
        DeleteVertexArrays = (delegate* unmanaged<int, int*, void>)F("glDeleteVertexArrays");
        VertexAttribPointer = (delegate* unmanaged<int, int, int, byte, int, IntPtr, void>)F("glVertexAttribPointer");
        EnableVertexAttribArray = (delegate* unmanaged<int, void>)F("glEnableVertexAttribArray");
        CreateShader = (delegate* unmanaged<int, int>)F("glCreateShader");
        ShaderSource = (delegate* unmanaged<int, int, byte**, int*, void>)F("glShaderSource");
        CompileShader = (delegate* unmanaged<int, void>)F("glCompileShader");
        GetShaderiv = (delegate* unmanaged<int, int, int*, void>)F("glGetShaderiv");
        GetShaderInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>)F("glGetShaderInfoLog");
        DeleteShader = (delegate* unmanaged<int, void>)F("glDeleteShader");
        CreateProgram = (delegate* unmanaged<int>)F("glCreateProgram");
        AttachShader = (delegate* unmanaged<int, int, void>)F("glAttachShader");
        BindAttribLocation = (delegate* unmanaged<int, int, byte*, void>)F("glBindAttribLocation");
        LinkProgram = (delegate* unmanaged<int, void>)F("glLinkProgram");
        GetProgramiv = (delegate* unmanaged<int, int, int*, void>)F("glGetProgramiv");
        GetProgramInfoLog = (delegate* unmanaged<int, int, int*, byte*, void>)F("glGetProgramInfoLog");
        UseProgram = (delegate* unmanaged<int, void>)F("glUseProgram");
        GetUniformLocation = (delegate* unmanaged<int, byte*, int>)F("glGetUniformLocation");
        Uniform1i = (delegate* unmanaged<int, int, void>)F("glUniform1i");
        Uniform1f = (delegate* unmanaged<int, float, void>)F("glUniform1f");
        Uniform2f = (delegate* unmanaged<int, float, float, void>)F("glUniform2f");
        Uniform3f = (delegate* unmanaged<int, float, float, float, void>)F("glUniform3f");
        Uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)F("glUniform4f");
        Uniform4fv = (delegate* unmanaged<int, int, float*, void>)F("glUniform4fv");
        UniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)F("glUniformMatrix4fv");
        GenTextures = (delegate* unmanaged<int, int*, void>)F("glGenTextures");
        BindTexture = (delegate* unmanaged<int, int, void>)F("glBindTexture");
        TexImage2D = (delegate* unmanaged<int, int, int, int, int, int, int, int, void*, void>)F("glTexImage2D");
        TexParameteri = (delegate* unmanaged<int, int, int, void>)F("glTexParameteri");
        GenerateMipmap = (delegate* unmanaged<int, void>)F("glGenerateMipmap");
        ActiveTexture = (delegate* unmanaged<int, void>)F("glActiveTexture");
        DeleteTextures = (delegate* unmanaged<int, int*, void>)F("glDeleteTextures");
        GenFramebuffers = (delegate* unmanaged<int, int*, void>)F("glGenFramebuffers");
        BindFramebuffer = (delegate* unmanaged<int, int, void>)F("glBindFramebuffer");
        FramebufferTexture2D = (delegate* unmanaged<int, int, int, int, int, void>)F("glFramebufferTexture2D");
        FramebufferRenderbuffer = (delegate* unmanaged<int, int, int, int, void>)F("glFramebufferRenderbuffer");
        CheckFramebufferStatus = (delegate* unmanaged<int, int>)F("glCheckFramebufferStatus");
        DeleteFramebuffers = (delegate* unmanaged<int, int*, void>)F("glDeleteFramebuffers");
        GenRenderbuffers = (delegate* unmanaged<int, int*, void>)F("glGenRenderbuffers");
        BindRenderbuffer = (delegate* unmanaged<int, int, void>)F("glBindRenderbuffer");
        RenderbufferStorage = (delegate* unmanaged<int, int, int, int, void>)F("glRenderbufferStorage");
        DeleteRenderbuffers = (delegate* unmanaged<int, int*, void>)F("glDeleteRenderbuffers");
        ReadPixels = (delegate* unmanaged<int, int, int, int, int, int, void*, void>)F("glReadPixels");
        PixelStorei = (delegate* unmanaged<int, int, void>)F("glPixelStorei");
        DrawArrays = (delegate* unmanaged<int, int, int, void>)F("glDrawArrays");
        Finish = (delegate* unmanaged<void>)F("glFinish");
        GetError = (delegate* unmanaged<int>)F("glGetError");
    }

    public string Text(int name) => Marshal.PtrToStringAnsi(GetString(name)) ?? "";

    public int Uniform(int program, string name)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) return GetUniformLocation(program, p);
    }

    /// <summary>Compiles and links a vertex and fragment shader with the attributes at fixed places.</summary>
    public int Program(string vertex, string fragment, params string[] attributes)
    {
        var vs = Shader(VertexShader, vertex);
        var fs = Shader(FragmentShader, fragment);
        var program = CreateProgram();
        AttachShader(program, vs);
        AttachShader(program, fs);
        for (var i = 0; i < attributes.Length; i++)
        {
            var name = System.Text.Encoding.ASCII.GetBytes(attributes[i] + "\0");
            fixed (byte* p = name) BindAttribLocation(program, i, p);
        }

        LinkProgram(program);
        int ok;
        GetProgramiv(program, LinkStatus, &ok);
        if (ok == 0)
        {
            var log = new byte[4096];
            int length;
            fixed (byte* p = log) GetProgramInfoLog(program, log.Length, &length, p);
            throw new InvalidOperationException("A 3D view shader did not link: " + System.Text.Encoding.ASCII.GetString(log, 0, length));
        }

        DeleteShader(vs);
        DeleteShader(fs);
        return program;
    }

    private int Shader(int type, string source)
    {
        var shader = CreateShader(type);
        var bytes = System.Text.Encoding.ASCII.GetBytes(source + "\0");
        fixed (byte* p = bytes)
        {
            var ptr = p;
            ShaderSource(shader, 1, &ptr, null);
        }

        CompileShader(shader);
        int ok;
        GetShaderiv(shader, CompileStatus, &ok);
        if (ok != 0) return shader;
        var log = new byte[4096];
        int length;
        fixed (byte* l = log) GetShaderInfoLog(shader, log.Length, &length, l);
        throw new InvalidOperationException("A 3D view shader did not compile: " + System.Text.Encoding.ASCII.GetString(log, 0, length));
    }
}
