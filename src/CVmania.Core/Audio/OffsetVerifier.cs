namespace CVmania.Core.Audio;

public sealed record OffsetReport(int LagSamples, double LagMs, double Correlation, double ResidualRms, string Message)
{
    public bool IsAligned => Math.Abs(LagMs) <= 1.0 && Correlation > 0.9;
}

/// <summary>
/// Measures the time shift between the PCM we intended to write and what a decoder (BASS, i.e.
/// osu!) actually gets back from the encoded file. A non-zero lag would mean the generated .osu
/// is offset from the audio; the exporter refuses to call an export "verified" unless it is ~0.
/// </summary>
public static class OffsetVerifier
{
    public static OffsetReport Verify(PcmAudio expected, PcmAudio actual, int maxLagMs = 60)
    {
        if (expected.SampleRate != actual.SampleRate)
            return new OffsetReport(0, 0, 0, 0, $"Sample rate differs ({expected.SampleRate} vs {actual.SampleRate}); cannot verify.");

        var a = expected.ToMono();
        var b = actual.ToMono();
        int sr = expected.SampleRate;
        int maxLag = (int)Math.Round(maxLagMs * sr / 1000.0);

        // pick a 1-second window starting at the first clearly audible frame
        int window = Math.Min(sr, a.Length / 2);
        int w0 = 0;
        for (int i = 0; i < a.Length; i++) { if (Math.Abs(a[i]) > 0.05f) { w0 = i; break; } }
        w0 = Math.Max(maxLag, Math.Min(w0, Math.Max(0, a.Length - window - maxLag - 1)));
        if (window < sr / 10 || a.Length < 2 * maxLag + window)
            return new OffsetReport(0, 0, 0, 0, "Audio too short to verify.");

        double ea = 0;
        for (int i = 0; i < window; i++) ea += (double)a[w0 + i] * a[w0 + i];
        if (ea < 1e-9) return new OffsetReport(0, 0, 0, 0, "Silent audio; nothing to verify.");

        int bestLag = 0;
        double bestCorr = double.NegativeInfinity;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            int start = w0 + lag;
            if (start < 0 || start + window > b.Length) continue;
            double dot = 0, eb = 0;
            for (int i = 0; i < window; i++)
            {
                double x = a[w0 + i];
                double y = b[start + i];
                dot += x * y;
                eb += y * y;
            }
            double corr = eb <= 1e-12 ? 0 : dot / Math.Sqrt(ea * eb);
            if (corr > bestCorr) { bestCorr = corr; bestLag = lag; }
        }

        double resid = 0;
        int n = 0;
        for (int i = 0; i < window; i++)
        {
            int j = w0 + bestLag + i;
            if (j < 0 || j >= b.Length) continue;
            double d = a[w0 + i] - b[j];
            resid += d * d; n++;
        }
        double rms = n > 0 ? Math.Sqrt(resid / n) : 0;
        double lagMs = bestLag * 1000.0 / sr;
        var msg = bestCorr < 0.9
            ? $"Low correlation ({bestCorr:0.000}); the decoded audio does not resemble the rendered audio."
            : Math.Abs(bestLag) <= 1
                ? "Decoded audio lines up with the rendered audio (0 ms offset)."
                : $"Decoded audio is shifted by {lagMs:+0.00;-0.00} ms ({bestLag} samples).";
        return new OffsetReport(bestLag, lagMs, bestCorr, rms, msg);
    }
}
