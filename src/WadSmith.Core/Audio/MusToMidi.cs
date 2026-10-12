using System.Buffers.Binary;

namespace WadSmith.Core.Audio;

/// <summary>
/// Converts Doom's MUS music to a standard MIDI file (format 0, one track). MUS runs at 140 ticks a
/// second; the MIDI file uses 70 ticks per quarter note at 120 beats a minute, which is the same
/// rate, so delays copy across unchanged. MUS channel 15 is percussion (MIDI channel 10); the
/// others take the remaining MIDI channels in the order they are first used.
/// </summary>
public static class MusToMidi
{
    private static readonly byte[] ControllerMap = [0, 0, 1, 7, 10, 11, 91, 93, 64, 67];
    private static readonly byte[] SystemEventMap = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 120, 123, 126, 127, 121];

    public static byte[] Convert(ReadOnlySpan<byte> mus, string name = "music")
    {
        if (mus.Length < 16 || !mus.StartsWith("MUS\x1A"u8)) throw new CorruptDataException($"{name} is not MUS music.");
        int scoreLength = BinaryPrimitives.ReadUInt16LittleEndian(mus[4..]);
        int scoreStart = BinaryPrimitives.ReadUInt16LittleEndian(mus[6..]);
        if (scoreStart >= mus.Length) throw new CorruptDataException($"{name}'s score starts past the end of the lump.");
        var score = mus[scoreStart..Math.Min(mus.Length, scoreStart + scoreLength)].ToArray();

        var track = new List<byte>(score.Length * 2);
        var channelMap = new int[16];
        Array.Fill(channelMap, -1);
        var velocity = new byte[16];
        Array.Fill(velocity, (byte)127);
        var nextMidiChannel = 0;
        var pendingDelay = 0;

        // Tempo: 500000 microseconds per quarter note.
        WriteDelay(track, 0);
        track.AddRange([0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20]);

        int MidiChannel(int musChannel)
        {
            if (musChannel == 15) return 9;
            if (channelMap[musChannel] >= 0) return channelMap[musChannel];
            if (nextMidiChannel == 9) nextMidiChannel++;
            channelMap[musChannel] = Math.Min(nextMidiChannel++, 15);
            return channelMap[musChannel];
        }

        void Event(byte status, params byte[] data)
        {
            WriteDelay(track, pendingDelay);
            pendingDelay = 0;
            track.Add(status);
            track.AddRange(data);
        }

        var pos = 0;
        var ended = false;
        while (pos < score.Length && !ended)
        {
            var descriptor = score[pos++];
            var last = (descriptor & 0x80) != 0;
            var type = (descriptor >> 4) & 7;
            var musChannel = descriptor & 0x0F;
            var ch = MidiChannel(musChannel);

            byte Read() => pos < score.Length ? score[pos++] : throw new CorruptDataException($"{name} ends in the middle of an event.");

            switch (type)
            {
                case 0: // release note
                    Event((byte)(0x80 | ch), (byte)(Read() & 0x7F), 0);
                    break;
                case 1: // play note
                {
                    var note = Read();
                    if ((note & 0x80) != 0) velocity[musChannel] = (byte)(Read() & 0x7F);
                    Event((byte)(0x90 | ch), (byte)(note & 0x7F), velocity[musChannel]);
                    break;
                }

                case 2: // pitch bend: one byte, 128 is centre
                {
                    var bend = Read() * 64;
                    Event((byte)(0xE0 | ch), (byte)(bend & 0x7F), (byte)((bend >> 7) & 0x7F));
                    break;
                }

                case 3: // system event
                {
                    var sys = Read() & 0x7F;
                    if (sys < SystemEventMap.Length && SystemEventMap[sys] != 0) Event((byte)(0xB0 | ch), SystemEventMap[sys], 0);
                    break;
                }

                case 4: // controller
                {
                    var controller = Read() & 0x7F;
                    var value = (byte)(Read() & 0x7F);
                    if (controller == 0) Event((byte)(0xC0 | ch), value);
                    else if (controller < ControllerMap.Length) Event((byte)(0xB0 | ch), ControllerMap[controller], value);
                    break;
                }

                case 5: // end of measure: nothing to write
                    break;
                case 6: // score end
                    ended = true;
                    break;
                default:
                    throw new CorruptDataException($"{name} holds an unknown MUS event type {type}.");
            }

            if (last && !ended)
            {
                var delay = 0;
                byte b;
                do
                {
                    b = Read();
                    delay = (delay << 7) | (b & 0x7F);
                }
                while ((b & 0x80) != 0);
                pendingDelay += delay;
            }
        }

        WriteDelay(track, pendingDelay);
        track.AddRange([0xFF, 0x2F, 0x00]);

        var midi = new byte[14 + 8 + track.Count];
        var s = midi.AsSpan();
        "MThd"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32BigEndian(s[4..], 6);
        BinaryPrimitives.WriteInt16BigEndian(s[8..], 0);
        BinaryPrimitives.WriteInt16BigEndian(s[10..], 1);
        BinaryPrimitives.WriteInt16BigEndian(s[12..], 70);
        "MTrk"u8.CopyTo(s[14..]);
        BinaryPrimitives.WriteInt32BigEndian(s[18..], track.Count);
        track.CopyTo(midi, 22);
        return midi;
    }

    private static void WriteDelay(List<byte> track, int delay)
    {
        Span<byte> buffer = stackalloc byte[5];
        var n = 0;
        buffer[n++] = (byte)(delay & 0x7F);
        while ((delay >>= 7) > 0) buffer[n++] = (byte)(0x80 | (delay & 0x7F));
        for (var i = n - 1; i >= 0; i--) track.Add(buffer[i]);
    }
}
