using OggVorbisEncoder;

namespace CVmania.Core.Audio;

/// <summary>
/// Ogg Vorbis encoding (managed port of libvorbis). The port loses the first half long block
/// (1024 frames at 44.1/48 kHz) at the start of the stream, so the audio is pre-padded by
/// <paramref name="prePadFrames"/> zero frames by default; the exporter verifies the result with
/// BASS and corrects the padding if a decoder ever disagrees.
/// </summary>
public static class OggEncoder
{
    public const int DefaultPrePadFrames = 1024;

    public static void Encode(PcmAudio pcm, string path, float quality = 0.6f, int prePadFrames = DefaultPrePadFrames)
    {
        int channels = pcm.Channels;
        long srcFrames = pcm.Frames;
        long skip = prePadFrames < 0 ? -prePadFrames : 0;
        long pad = prePadFrames > 0 ? prePadFrames : 0;
        long frames = Math.Max(0, srcFrames - skip) + pad;
        var planar = new float[channels][];
        for (int c = 0; c < channels; c++) planar[c] = new float[frames];
        for (long f = skip; f < srcFrames; f++)
        {
            var b = f * channels;
            var o = f - skip + pad;
            for (int c = 0; c < channels; c++) planar[c][o] = pcm.Samples[b + c];
        }

        using var output = File.Create(path);
        var info = VorbisInfo.InitVariableBitRate(channels, pcm.SampleRate, Math.Clamp(quality, -0.1f, 1f));
        var oggStream = new OggStream(new Random().Next());

        var comments = new Comments();
        comments.AddTag("ENCODER", "CV!mania");
        oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
        oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));
        FlushPages(oggStream, output, true);

        var state = ProcessingState.Create(info);
        const int block = 4096;
        for (int read = 0; read < frames; read += block)
        {
            int length = (int)Math.Min(block, frames - read);
            state.WriteData(planar, length, read);
            while (!oggStream.Finished && state.PacketOut(out var packet))
            {
                oggStream.PacketIn(packet);
                FlushPages(oggStream, output, false);
            }
        }
        state.WriteEndOfStream();
        while (!oggStream.Finished && state.PacketOut(out var packet))
        {
            oggStream.PacketIn(packet);
            FlushPages(oggStream, output, false);
        }
        FlushPages(oggStream, output, true);
    }

    private static void FlushPages(OggStream oggStream, Stream output, bool force)
    {
        while (oggStream.PageOut(out var page, force))
        {
            output.Write(page.Header, 0, page.Header.Length);
            output.Write(page.Body, 0, page.Body.Length);
        }
    }
}
