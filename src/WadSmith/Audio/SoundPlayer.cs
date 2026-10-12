using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using WadSmith.Editor.App;

namespace WadSmith.App.Audio;

/// <summary>Plays short sounds for the editor (an object's sound when the pointer is over it on the shelf, D33).</summary>
internal interface ISoundPlayer : IDisposable
{
    /// <summary>Plays a WAV file, cutting off the one playing; returns at once.</summary>
    void Play(byte[] wav);
}

/// <summary>No sound: where SDL is missing, and wherever the app runs without its own window (tests, captures).</summary>
internal sealed class SilentPlayer : ISoundPlayer
{
    public void Play(byte[] wav)
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Sounds through SDL2's audio queue, the library GZDoom itself plays through on Linux, loaded when the app starts and
/// opened at the first sound. Every SDL call runs on the player's own worker, never on the UI thread; a sound asked for
/// while another is waiting replaces it, so sweeping the pointer across a shelf plays the last one only.
/// </summary>
internal sealed class SdlSoundPlayer : ISoundPlayer
{
    private const uint InitAudio = 0x10;
    private const ushort S16 = 0x8010;
    private const int Rate = 44100;

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioSpec
    {
        public int Freq;
        public ushort Format;
        public byte Channels;
        public byte Silence;
        public ushort Samples;
        public ushort Padding;
        public uint Size;
        public IntPtr Callback;
        public IntPtr UserData;
    }

    private readonly IntPtr _library;
    private readonly unsafe delegate* unmanaged[Cdecl]<uint, int> _initSubSystem;
    private readonly unsafe delegate* unmanaged[Cdecl]<uint, void> _quitSubSystem;
    private readonly unsafe delegate* unmanaged[Cdecl]<IntPtr, int, AudioSpec*, AudioSpec*, int, uint> _openAudioDevice;
    private readonly unsafe delegate* unmanaged[Cdecl]<uint, void> _closeAudioDevice;
    private readonly unsafe delegate* unmanaged[Cdecl]<uint, void*, uint, int> _queueAudio;
    private readonly unsafe delegate* unmanaged[Cdecl]<uint, void> _clearQueuedAudio;
    private readonly unsafe delegate* unmanaged[Cdecl]<uint, int, void> _pauseAudioDevice;
    private readonly unsafe delegate* unmanaged[Cdecl]<uint, uint> _getQueuedAudioSize;
    private readonly unsafe delegate* unmanaged[Cdecl]<IntPtr> _currentAudioDriver;
    private readonly Channel<byte[]?> _wanted = Channel.CreateBounded<byte[]?>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _worker;
    private uint _device;
    private bool _started;
    private bool _failed;

    private unsafe SdlSoundPlayer(IntPtr library)
    {
        _library = library;
        _initSubSystem = (delegate* unmanaged[Cdecl]<uint, int>)NativeLibrary.GetExport(library, "SDL_InitSubSystem");
        _quitSubSystem = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(library, "SDL_QuitSubSystem");
        _openAudioDevice = (delegate* unmanaged[Cdecl]<IntPtr, int, AudioSpec*, AudioSpec*, int, uint>)NativeLibrary.GetExport(library, "SDL_OpenAudioDevice");
        _closeAudioDevice = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(library, "SDL_CloseAudioDevice");
        _queueAudio = (delegate* unmanaged[Cdecl]<uint, void*, uint, int>)NativeLibrary.GetExport(library, "SDL_QueueAudio");
        _clearQueuedAudio = (delegate* unmanaged[Cdecl]<uint, void>)NativeLibrary.GetExport(library, "SDL_ClearQueuedAudio");
        _pauseAudioDevice = (delegate* unmanaged[Cdecl]<uint, int, void>)NativeLibrary.GetExport(library, "SDL_PauseAudioDevice");
        _getQueuedAudioSize = (delegate* unmanaged[Cdecl]<uint, uint>)NativeLibrary.GetExport(library, "SDL_GetQueuedAudioSize");
        _currentAudioDriver = (delegate* unmanaged[Cdecl]<IntPtr>)NativeLibrary.GetExport(library, "SDL_GetCurrentAudioDriver");
        _worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>The SDL2 player, or null when SDL2 is not installed (the editor is then silent).</summary>
    public static SdlSoundPlayer? TryCreate()
    {
        string[] names = OperatingSystem.IsWindows() ? ["SDL2.dll"] : OperatingSystem.IsMacOS() ? ["libSDL2-2.0.0.dylib", "libSDL2.dylib"] : ["libSDL2-2.0.so.0", "libSDL2.so"];
        foreach (var name in names)
        {
            if (!NativeLibrary.TryLoad(name, out var library)) continue;
            try
            {
                return new SdlSoundPlayer(library);
            }
            catch (EntryPointNotFoundException)
            {
                NativeLibrary.Free(library);
            }
        }

        Log.Info("SDL2 was not found, so the editor plays no sounds.");
        return null;
    }

    public void Play(byte[] wav) => _wanted.Writer.TryWrite(wav);

    /// <summary>The audio driver SDL opened (empty before the first sound), for instruments.</summary>
    internal string Driver { get; private set; } = "";

    /// <summary>Bytes still waiting to be played, for instruments.</summary>
    internal unsafe uint QueuedBytes => _device == 0 ? 0 : _getQueuedAudioSize(_device);

    private async Task Run()
    {
        await foreach (var wav in _wanted.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (wav is null || _failed) continue;
            try
            {
                if (_device == 0 && !Open()) continue;
                Push(Samples(wav));
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                Log.Warn($"A sound could not be played: {ex.Message}");
            }
        }

        Close();
    }

    private unsafe void Push(short[] samples)
    {
        if (samples.Length == 0) return;
        _clearQueuedAudio(_device);
        fixed (short* p = samples) _queueAudio(_device, p, (uint)(samples.Length * 2));
        _pauseAudioDevice(_device, 0);
    }

    private unsafe void Close()
    {
        if (_device != 0) _closeAudioDevice(_device);
        if (_started) _quitSubSystem(InitAudio);
    }

    private unsafe bool Open()
    {
        if (_initSubSystem(InitAudio) != 0)
        {
            _failed = true;
            Log.Warn("SDL could not start its audio, so the editor plays no sounds.");
            return false;
        }

        _started = true;
        var want = new AudioSpec { Freq = Rate, Format = S16, Channels = 1, Samples = 1024 };
        AudioSpec got;
        _device = _openAudioDevice(IntPtr.Zero, 0, &want, &got, 0);
        if (_device != 0)
        {
            Driver = Marshal.PtrToStringUTF8(_currentAudioDriver()) ?? "";
            Log.Info($"Sounds play through SDL's {Driver} audio.");
            return true;
        }

        _failed = true;
        Log.Warn("SDL found no audio device to play on, so the editor plays no sounds.");
        return false;
    }

    /// <summary>A PCM WAV file's samples, mixed to mono, at the device's rate and at half volume (a hint, not a game).</summary>
    internal static short[] Samples(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8)) throw new FormatException("not a WAV file");
        int rate = 0, channels = 0, bits = 0;
        ReadOnlySpan<byte> data = default;
        for (var at = 12; at + 8 <= wav.Length;)
        {
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(at + 4, 4)), (uint)(wav.Length - at - 8));
            var chunk = wav.Slice(at + 8, size);
            if (wav.Slice(at, 4).SequenceEqual("fmt "u8) && size >= 16)
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1) throw new FormatException("not PCM");
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);
            }
            else if (wav.Slice(at, 4).SequenceEqual("data"u8))
            {
                data = chunk;
            }

            at += 8 + size + (size & 1);
        }

        if (rate <= 0 || channels <= 0 || bits is not (8 or 16)) throw new FormatException("an unknown kind of WAV");
        var frame = channels * bits / 8;
        var frames = data.Length / frame;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            float sum = 0;
            for (var c = 0; c < channels; c++)
            {
                var at = i * frame + c * bits / 8;
                sum += bits == 8 ? (data[at] - 128) / 128f : BinaryPrimitives.ReadInt16LittleEndian(data[at..]) / 32768f;
            }

            mono[i] = sum / channels;
        }

        var length = frames == 0 ? 0 : (int)((long)frames * Rate / rate);
        var output = new short[length];
        for (var i = 0; i < length; i++)
        {
            var x = (double)i * rate / Rate;
            var k = (int)x;
            var a = mono[Math.Min(k, frames - 1)];
            var b = mono[Math.Min(k + 1, frames - 1)];
            output[i] = (short)Math.Clamp((a + (b - a) * (x - k)) * 0.5 * 32767, short.MinValue, short.MaxValue);
        }

        return output;
    }

    public void Dispose()
    {
        _wanted.Writer.TryComplete();
        // The library stays loaded if the worker is somehow still in it.
        if (_worker.Wait(TimeSpan.FromSeconds(2))) NativeLibrary.Free(_library);
    }
}
