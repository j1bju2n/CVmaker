using System.Runtime.InteropServices;
using NAudio.Lame;
using NAudio.Wave;

namespace CVmaker.Core.Audio;

/// <summary>
/// MP3 encoding through LAME (NAudio.Lame). The Xing/LAME info frame with encoder delay and
/// padding is always written, so BASS (and therefore osu!) trims the encoder delay and the output
/// is sample-accurate relative to the PCM we rendered.
/// </summary>
public static class Mp3Encoder
{
    public static void Encode(PcmAudio pcm, string path, int bitrateKbps = 192)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(pcm.SampleRate, pcm.Channels);
        var config = new LameConfig
        {
            BitRate = bitrateKbps,
            WriteVBRTag = true,
            Mode = pcm.Channels == 1 ? MPEGMode.Mono : MPEGMode.JointStereo,
        };
        using var stream = File.Create(path);
        using var writer = new LameMP3FileWriter(stream, format, config);
        var bytes = MemoryMarshal.AsBytes(pcm.Samples.AsSpan());
        const int chunk = 1 << 16;
        for (int off = 0; off < bytes.Length; off += chunk)
        {
            var len = Math.Min(chunk, bytes.Length - off);
            writer.Write(bytes.Slice(off, len).ToArray(), 0, len);
        }
        writer.Flush();
    }
}
