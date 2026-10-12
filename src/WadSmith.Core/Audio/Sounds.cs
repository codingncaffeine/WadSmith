using System.Buffers.Binary;

namespace WadSmith.Core.Audio;

/// <summary>What kind of audio a lump holds, judged by its content.</summary>
public enum AudioKind
{
    None,
    /// <summary>Doom's digital sound format (DMX): 8-bit unsigned mono with a small header.</summary>
    DmxSound,
    /// <summary>PC speaker tones (the DP sounds): a list of tone numbers at 140 per second.</summary>
    PcSpeaker,
    Wav,
    Ogg,
    Flac,
    Mp3,
    /// <summary>Doom's MUS music format.</summary>
    Mus,
    Midi,
    /// <summary>Tracker modules: MOD, XM, IT, S3M.</summary>
    Module,
}

public static class AudioFormats
{
    public static AudioKind Detect(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 4 && d.StartsWith("MUS\x1A"u8)) return AudioKind.Mus;
        if (d.StartsWith("MThd"u8)) return AudioKind.Midi;
        if (d.Length >= 12 && d.StartsWith("RIFF"u8) && d.Slice(8, 4).SequenceEqual("WAVE"u8)) return AudioKind.Wav;
        if (d.StartsWith("OggS"u8)) return AudioKind.Ogg;
        if (d.StartsWith("fLaC"u8)) return AudioKind.Flac;
        if (d.StartsWith("ID3"u8) || (d.Length > 2 && d[0] == 0xFF && (d[1] & 0xE0) == 0xE0 && (d[1] & 0x06) != 0)) return AudioKind.Mp3;
        if (d.StartsWith("Extended Module: "u8) || d.StartsWith("IMPM"u8)) return AudioKind.Module;
        if (d.Length > 48 && d.Slice(44, 4).SequenceEqual("SCRM"u8)) return AudioKind.Module;
        if (d.Length > 1084 && (d.Slice(1080, 4).SequenceEqual("M.K."u8) || d.Slice(1080, 4).SequenceEqual("M!K!"u8) || d.Slice(1080, 4).SequenceEqual("FLT4"u8) || d.Slice(1080, 4).SequenceEqual("8CHN"u8))) return AudioKind.Module;
        if (d.Length >= 8)
        {
            var format = BinaryPrimitives.ReadUInt16LittleEndian(d);
            if (format == 3)
            {
                var rate = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
                var count = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]);
                if (rate >= 4000 && rate <= 48000 && count > 0 && count <= d.Length - 8) return AudioKind.DmxSound;
            }
            else if (format == 0)
            {
                var count = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
                if (count > 0 && count == d.Length - 4) return AudioKind.PcSpeaker;
            }
        }

        return AudioKind.None;
    }

    /// <summary>The usual file extension for the kind, with the dot.</summary>
    public static string Extension(AudioKind kind, ReadOnlySpan<byte> d) => kind switch
    {
        AudioKind.DmxSound or AudioKind.Wav => ".wav",
        AudioKind.Ogg => ".ogg",
        AudioKind.Flac => ".flac",
        AudioKind.Mp3 => ".mp3",
        AudioKind.Mus or AudioKind.Midi => ".mid",
        AudioKind.Module when d.StartsWith("Extended Module: "u8) => ".xm",
        AudioKind.Module when d.StartsWith("IMPM"u8) => ".it",
        AudioKind.Module when d.Length > 48 && d.Slice(44, 4).SequenceEqual("SCRM"u8) => ".s3m",
        AudioKind.Module => ".mod",
        _ => ".lmp",
    };

    /// <summary>
    /// A DMX sound as a WAV file. The format pads the samples with 16 bytes at each end; those are
    /// left out, as source ports leave them out when they play the sound.
    /// </summary>
    public static byte[] DmxToWav(ReadOnlySpan<byte> d)
    {
        if (Detect(d) != AudioKind.DmxSound) throw new CorruptDataException("Not a Doom sound.");
        var rate = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[4..]);
        var samples = d.Slice(8, count);
        if (samples.Length > 32) samples = samples[16..^16];
        return Wav(samples, rate, 1, 8);
    }

    /// <summary>A WAV file around raw PCM samples.</summary>
    public static byte[] Wav(ReadOnlySpan<byte> pcm, int rate, int channels, int bits)
    {
        var data = new byte[44 + pcm.Length + (pcm.Length & 1)];
        var s = data.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], data.Length - 8);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], rate * channels * bits / 8);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], (short)(channels * bits / 8));
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], (short)bits);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], pcm.Length);
        pcm.CopyTo(s[44..]);
        return data;
    }
}
