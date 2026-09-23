using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace CVmania.Core.Audio;

public static class WavWriter
{
    /// <summary>Encodes as 32-bit IEEE float WAV (format tag 3) into a byte array.</summary>
    public static byte[] ToFloatWav(PcmAudio pcm)
    {
        var data = MemoryMarshal.AsBytes(pcm.Samples.AsSpan());
        var buf = new byte[44 + data.Length];
        var span = buf.AsSpan();
        int blockAlign = pcm.Channels * 4;
        "RIFF"u8.CopyTo(span[0..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + data.Length);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 3); // IEEE float
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)pcm.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], pcm.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], pcm.SampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 32);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], data.Length);
        data.CopyTo(span[44..]);
        return buf;
    }

    /// <summary>Encodes as 16-bit PCM WAV into a byte array.</summary>
    public static byte[] ToPcm16Wav(PcmAudio pcm)
    {
        int dataLen = pcm.Samples.Length * 2;
        var buf = new byte[44 + dataLen];
        var span = buf.AsSpan();
        int blockAlign = pcm.Channels * 2;
        "RIFF"u8.CopyTo(span[0..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataLen);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)pcm.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], pcm.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], pcm.SampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLen);
        var s = pcm.Samples;
        for (int i = 0; i < s.Length; i++)
        {
            var v = Math.Clamp(s[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], (short)Math.Round(v * 32767f));
        }
        return buf;
    }

    public static void WriteFloat(string path, PcmAudio pcm) => File.WriteAllBytes(path, ToFloatWav(pcm));
    public static void WritePcm16(string path, PcmAudio pcm) => File.WriteAllBytes(path, ToPcm16Wav(pcm));
}
