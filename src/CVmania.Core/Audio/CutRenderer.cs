using CVmania.Core.Cut;

namespace CVmania.Core.Audio;

/// <summary>
/// Produces the cut audio for a set of regions. Regions are butted together exactly at the
/// millisecond positions of <see cref="TimeMap"/>, so hit objects and audio can never drift apart.
///
/// Edge audio never moves a cut point:
/// <list type="bullet">
/// <item>Fade-in: the region's own first N ms ramp up from silence.</item>
/// <item>Fade-out: N ms of the audio after the region ramp down to silence, mixed over the start of
/// the next region (or appended as a tail after the last region).</item>
/// <item>Extend-out / extend-in: N ms of outside audio at full volume, mixed over the neighbouring
/// region (or added as lead-in / tail at the outer ends).</item>
/// </list>
/// Joins without any edge audio get a short equal-power crossfade centred on the cut point; a side
/// that would otherwise stop or start abruptly gets a de-click ramp of half that length. Mixed
/// zones pass through a soft limiter so two full-volume signals cannot clip.
/// </summary>
public static class CutRenderer
{
    public static PcmAudio Render(PcmAudio src, IReadOnlyList<CutRegion> regions, CutOptions options)
    {
        var normalized = CutRegion.Normalize(regions);
        if (normalized.Count == 0) throw new ArgumentException("At least one non-empty region is required.", nameof(regions));
        var map = new TimeMap(normalized);

        int ch = src.Channels;
        int sr = src.SampleRate;
        int n = normalized.Count;

        var srcStart = new long[n];
        var lengths = new long[n];
        var outStart = new long[n];
        long leadIn = src.MsToFrame(map.LeadInMs);
        long acc = leadIn;
        for (int i = 0; i < n; i++)
        {
            srcStart[i] = src.MsToFrame(normalized[i].StartMs);
            var srcEnd = src.MsToFrame(normalized[i].EndMs);
            lengths[i] = Math.Max(0, srcEnd - srcStart[i]);
            outStart[i] = acc;
            acc += lengths[i];
        }
        long regionsEnd = acc;
        long tailOut = src.MsToFrame(map.TailOutMs);
        long tailSilence = src.MsToFrame(options.TailSilenceMs);
        long total = regionsEnd + tailOut + tailSilence;

        var output = new float[total * ch];
        var mixed = new bool[total]; // frames where two sources were added

        // region bodies (zeros where the source has no audio)
        for (int i = 0; i < n; i++) CopyFrames(src, srcStart[i], output, outStart[i], lengths[i]);

        // inside fade-ins
        for (int i = 0; i < n; i++)
        {
            long f = Math.Min(src.MsToFrame(normalized[i].InsideFadeMs), lengths[i]);
            for (long k = 0; k < f; k++)
            {
                float g = (float)((k + 1) / (double)f);
                long o = (outStart[i] + k) * ch;
                for (int c = 0; c < ch; c++) output[o + c] *= g;
            }
        }

        // lead-in before the first region (extend-in at full volume)
        if (leadIn > 0)
        {
            long from = srcStart[0] - leadIn;
            for (long k = 0; k < leadIn; k++)
            {
                long o = k * ch;
                for (int c = 0; c < ch; c++) output[o + c] = src.At(from + k, c);
            }
        }

        // tail after the last region
        if (tailOut > 0)
        {
            var last = normalized[n - 1];
            long from = srcStart[n - 1] + lengths[n - 1];
            bool fades = last.FadeOutMode == EdgeMode.Fade;
            for (long k = 0; k < tailOut; k++)
            {
                float g = fades ? (float)(1.0 - (k + 1) / (double)tailOut) : 1f;
                long o = (regionsEnd + k) * ch;
                for (int c = 0; c < ch; c++) output[o + c] = src.At(from + k, c) * g;
            }
        }

        long crossfade = src.MsToFrame(options.CrossfadeMs);
        long declick = Math.Max(1, crossfade / 2);

        for (int i = 1; i < n; i++)
        {
            var a = normalized[i - 1];
            var b = normalized[i];
            long join = outStart[i];
            long aEnd = srcStart[i - 1] + lengths[i - 1];
            long bStart = srcStart[i];
            bool touching = a.EndMs == b.StartMs;
            if (touching) continue; // the audio is continuous; edge settings at this join would only duplicate it

            long post = src.MsToFrame(a.PostMs);       // a's audio after its end, mixed over b's start
            long pre = src.MsToFrame(b.PreMs);         // b's audio before its start, mixed over a's end
            bool bFadesIn = b.InsideFadeMs > 0;

            if (post <= 0 && pre <= 0 && !bFadesIn)
            {
                // plain cut: symmetric equal-power crossfade centred on the join
                long len = Math.Min(crossfade, 2 * Math.Min(lengths[i - 1], lengths[i]));
                if (len < 2) continue;
                long half = len / 2;
                for (long k = -half; k < len - half; k++)
                {
                    double w = (k + half + 0.5) / len;
                    float go = (float)Math.Cos(w * Math.PI / 2);
                    float gi = (float)Math.Sin(w * Math.PI / 2);
                    long o = (join + k) * ch;
                    for (int c = 0; c < ch; c++) output[o + c] = src.At(aEnd + k, c) * go + src.At(bStart + k, c) * gi;
                }
                continue;
            }

            // de-clicks (multiplicative) for the side that stops or starts abruptly
            if (post <= 0)
            {
                long d = Math.Min(declick, lengths[i - 1]);
                for (long k = 0; k < d; k++)
                {
                    float g = (float)(1.0 - (k + 0.5) / d);
                    long o = (join - d + k) * ch;
                    for (int c = 0; c < ch; c++) output[o + c] *= g;
                }
            }
            if (pre <= 0 && !bFadesIn)
            {
                long d = Math.Min(declick, lengths[i]);
                for (long k = 0; k < d; k++)
                {
                    float g = (float)((k + 0.5) / d);
                    long o = (join + k) * ch;
                    for (int c = 0; c < ch; c++) output[o + c] *= g;
                }
            }

            // overlays (additive)
            if (post > 0)
            {
                long t = Math.Min(post, lengths[i]);
                bool fades = a.FadeOutMode == EdgeMode.Fade;
                for (long k = 0; k < t; k++)
                {
                    float g = fades ? (float)(1.0 - (k + 0.5) / t) : 1f;
                    long o = (join + k) * ch;
                    for (int c = 0; c < ch; c++) output[o + c] += src.At(aEnd + k, c) * g;
                    mixed[join + k] = true;
                }
            }
            if (pre > 0)
            {
                long h = Math.Min(pre, lengths[i - 1]);
                for (long k = 0; k < h; k++)
                {
                    long o = (join - h + k) * ch;
                    for (int c = 0; c < ch; c++) output[o + c] += src.At(bStart - h + k, c);
                    mixed[join - h + k] = true;
                }
            }
        }

        // keep mixed zones from clipping without touching untouched audio
        for (long f = 0; f < total; f++)
        {
            if (!mixed[f]) continue;
            long o = f * ch;
            for (int c = 0; c < ch; c++) output[o + c] = SoftLimit(output[o + c]);
        }

        return new PcmAudio(output, sr, ch);
    }

    /// <summary>Transparent below 0.9, smoothly compressed above, never exceeding 1.0.</summary>
    public static float SoftLimit(float x)
    {
        const float t = 0.9f;
        float a = Math.Abs(x);
        if (a <= t) return x;
        float y = t + (1f - t) * MathF.Tanh((a - t) / (1f - t));
        return x < 0 ? -y : y;
    }

    private static void CopyFrames(PcmAudio src, long srcFrame, float[] dst, long dstFrame, long count)
    {
        int ch = src.Channels;
        long srcFrames = src.Frames;
        long from = Math.Max(0, srcFrame);
        long to = Math.Min(srcFrames, srcFrame + count);
        if (to <= from) return;
        long skip = from - srcFrame;
        Array.Copy(src.Samples, from * ch, dst, (dstFrame + skip) * ch, (to - from) * ch);
    }
}
