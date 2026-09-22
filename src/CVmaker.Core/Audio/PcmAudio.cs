namespace CVmaker.Core.Audio;

/// <summary>Interleaved 32-bit float PCM.</summary>
public sealed class PcmAudio
{
    public float[] Samples { get; }
    public int SampleRate { get; }
    public int Channels { get; }

    public PcmAudio(float[] samples, int sampleRate, int channels)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public long Frames => Samples.Length / Channels;
    public double DurationMs => Frames * 1000.0 / SampleRate;

    public long MsToFrame(double ms) => (long)Math.Round(ms * SampleRate / 1000.0);
    public double FrameToMs(long frame) => frame * 1000.0 / SampleRate;

    /// <summary>Sample at (frame, channel); zero outside the buffer.</summary>
    public float At(long frame, int channel)
    {
        if (frame < 0 || frame >= Frames) return 0f;
        return Samples[frame * Channels + channel];
    }

    /// <summary>Mono mixdown (average of channels).</summary>
    public float[] ToMono()
    {
        var frames = Frames;
        var mono = new float[frames];
        if (Channels == 1) { Array.Copy(Samples, mono, frames); return mono; }
        for (long i = 0; i < frames; i++)
        {
            float acc = 0;
            var b = i * Channels;
            for (int c = 0; c < Channels; c++) acc += Samples[b + c];
            mono[i] = acc / Channels;
        }
        return mono;
    }

    /// <summary>
    /// Min/max peaks per bucket for waveform drawing. <paramref name="framesPerBucket"/> frames are
    /// folded into one (min, max) pair of the mono mix.
    /// </summary>
    public (float[] Min, float[] Max) Peaks(int framesPerBucket)
    {
        if (framesPerBucket <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerBucket));
        var frames = Frames;
        var buckets = (int)((frames + framesPerBucket - 1) / framesPerBucket);
        var min = new float[buckets];
        var max = new float[buckets];
        for (int b = 0; b < buckets; b++)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            long start = (long)b * framesPerBucket;
            long end = Math.Min(frames, start + framesPerBucket);
            for (long f = start; f < end; f++)
            {
                float v = 0;
                var idx = f * Channels;
                for (int c = 0; c < Channels; c++) v += Samples[idx + c];
                v /= Channels;
                if (v < lo) lo = v;
                if (v > hi) hi = v;
            }
            if (end <= start) { lo = hi = 0; }
            min[b] = lo; max[b] = hi;
        }
        return (min, max);
    }
}
