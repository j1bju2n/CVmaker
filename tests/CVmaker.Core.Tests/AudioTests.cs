using CVmaker.Core.Audio;
using CVmaker.Core.Cut;
using Xunit;

namespace CVmaker.Core.Tests;

public class AudioTests
{
    /// <summary>Stereo test signal with sharp onsets every 250 ms so that offsets are unambiguous.</summary>
    private static PcmAudio TestSignal(int sampleRate = 44100, double seconds = 3.0)
    {
        int ch = 2;
        long frames = (long)(sampleRate * seconds);
        var s = new float[frames * ch];
        var rng = new Random(1234);
        for (long f = 0; f < frames; f++)
        {
            double t = (double)f / sampleRate;
            double burst = (t % 0.25) < 0.05 ? 1.0 : 0.0;
            double v = 0.4 * Math.Sin(2 * Math.PI * 440 * t) * burst + 0.005 * (rng.NextDouble() * 2 - 1);
            s[f * ch] = (float)v;
            s[f * ch + 1] = (float)(0.7 * v);
        }
        return new PcmAudio(s, sampleRate, ch);
    }

    [Fact]
    public void Render_LengthsFollowRegions_AndCrossfadeKeepsTiming()
    {
        var src = TestSignal(44100, 2.0);
        var regions = new[] { new CutRegion(100, 600), new CutRegion(1000, 1500) };
        var opt = new CutOptions { CrossfadeMs = 10 };
        var outp = CutRenderer.Render(src, regions, opt);
        Assert.Equal(src.MsToFrame(1000), outp.Frames);
        // well away from the join the samples are straight copies
        Assert.Equal(src.At(src.MsToFrame(300), 0), outp.At(outp.MsToFrame(200), 0));
        Assert.Equal(src.At(src.MsToFrame(1200), 1), outp.At(outp.MsToFrame(700), 1));
        // the very centre of the join is the average of both sides (equal-power at w=0.5)
        long j = outp.MsToFrame(500);
        float a = src.At(src.MsToFrame(600), 0), b = src.At(src.MsToFrame(1000), 0);
        float expected = (float)(a * Math.Cos(Math.PI / 4) + b * Math.Sin(Math.PI / 4));
        Assert.InRange(outp.At(j, 0), Math.Min(expected, a * 0.6f + b * 0.6f) - 0.05f, Math.Max(expected, a * 0.8f + b * 0.8f) + 0.05f);
    }

    [Fact]
    public void Render_FadeModes_InsideFadeIn_AndFadeOutTail()
    {
        var src = TestSignal(44100, 2.0);
        var outp = CutRenderer.Render(src, new[] { new CutRegion(200, 1000, 100, 300) }, new CutOptions { TailSilenceMs = 500 });
        // 800 region (first 100 ms ramp up inside) + 300 fade-out tail + 500 silence
        Assert.Equal(src.MsToFrame(1600), outp.Frames);
        Assert.Equal(0f, outp.At(outp.Frames - 1, 0));
        // past the inside ramp the region is an exact copy
        Assert.Equal(src.At(src.MsToFrame(500), 0), outp.At(outp.MsToFrame(300), 0));
        // the first sample is (almost) silent
        Assert.True(Math.Abs(outp.At(0, 0)) < 0.01f);
        // the tail is the following audio, attenuated
        long tf = outp.MsToFrame(850);
        float expected = src.At(src.MsToFrame(1050), 0);
        Assert.True(Math.Abs(outp.At(tf, 0)) <= Math.Abs(expected) + 1e-6);
        Assert.True(Math.Abs(outp.At(outp.MsToFrame(1099), 0)) < 0.02f);
    }

    [Fact]
    public void Render_ExtendModes_UseOutsideAudioAtFullVolume()
    {
        var src = TestSignal(44100, 2.0);
        var outp = CutRenderer.Render(src, new[] { new CutRegion(200, 1000, 100, 300, EdgeMode.Extend, EdgeMode.Extend) }, new CutOptions { TailSilenceMs = 500 });
        // 100 extend-in + 800 region + 300 extend-out + 500 silence
        Assert.Equal(src.MsToFrame(1700), outp.Frames);
        Assert.Equal(src.At(src.MsToFrame(150), 0), outp.At(outp.MsToFrame(50), 0));    // raw lead-in
        Assert.Equal(src.At(src.MsToFrame(500), 0), outp.At(outp.MsToFrame(400), 0));   // region shifted by the lead-in
        Assert.Equal(src.At(src.MsToFrame(1250), 1), outp.At(outp.MsToFrame(1150), 1)); // raw tail
    }

    [Fact]
    public void Render_JoinWithEdgeAudio_MixesOverTheNeighbour_WithoutMovingTheCut()
    {
        var src = TestSignal(44100, 3.0);
        var regions = new[] { new CutRegion(100, 600, 0, 200), new CutRegion(1000, 1500, 150, 0) };
        var outp = CutRenderer.Render(src, regions, new CutOptions());
        // regions stay butted: 500 + 500
        Assert.Equal(src.MsToFrame(1000), outp.Frames);
        Assert.Equal(src.At(src.MsToFrame(200), 0), outp.At(outp.MsToFrame(100), 0));
        Assert.Equal(src.At(src.MsToFrame(1400), 1), outp.At(outp.MsToFrame(900), 1));
        // right at the join region 2 is still silent (inside fade-in) and region 1's tail is at ~full volume
        float atJoin = outp.At(outp.MsToFrame(500), 0);
        float tail = src.At(src.MsToFrame(600), 0);
        Assert.True(Math.Abs(atJoin - tail) < 0.02f, $"join {atJoin} vs tail {tail}");
        // inside the overlap the output is region 2 plus the attenuated tail of region 1
        long f = outp.MsToFrame(650);
        float b = src.At(src.MsToFrame(1150), 0);
        float aTail = src.At(src.MsToFrame(750), 0);
        Assert.True(Math.Abs(outp.At(f, 0) - b) <= Math.Abs(aTail) + 0.02f);
        // after the overlap region 2 is an exact copy again
        Assert.Equal(src.At(src.MsToFrame(1300), 0), outp.At(outp.MsToFrame(800), 0));

        // extend-in of region 2 is mixed over the end of region 1 at full volume
        var ext = CutRenderer.Render(src, new[] { new CutRegion(100, 600), new CutRegion(1000, 1500, 100, 0, EdgeMode.Extend) }, new CutOptions());
        Assert.Equal(src.MsToFrame(1000), ext.Frames);
        long g = ext.MsToFrame(450);
        float expected = CutRenderer.SoftLimit(src.At(src.MsToFrame(550), 0) + src.At(src.MsToFrame(950), 0));
        Assert.Equal(expected, ext.At(g, 0), 3);

        // touching regions without edge audio are an exact copy with no crossfade
        var touching = CutRenderer.Render(src, new[] { new CutRegion(100, 600), new CutRegion(600, 800) }, new CutOptions());
        Assert.Equal(src.MsToFrame(700), touching.Frames);
        Assert.Equal(src.At(src.MsToFrame(600), 0), touching.At(touching.MsToFrame(500), 0));
    }

    [Fact]
    public void SoftLimit_IsTransparentBelowKneeAndBounded()
    {
        Assert.Equal(0.5f, CutRenderer.SoftLimit(0.5f));
        Assert.Equal(-0.85f, CutRenderer.SoftLimit(-0.85f));
        Assert.True(CutRenderer.SoftLimit(1.8f) <= 1f);
        Assert.True(CutRenderer.SoftLimit(1.8f) > 0.95f);
        Assert.True(CutRenderer.SoftLimit(-3f) >= -1f);
    }

    [Fact]
    public void Wav_RoundTripsThroughBass()
    {
        var src = TestSignal(44100, 1.0);
        var decoded = BassDecoder.DecodeMemory(WavWriter.ToFloatWav(src));
        Assert.Equal(src.SampleRate, decoded.SampleRate);
        Assert.Equal(src.Channels, decoded.Channels);
        Assert.Equal(src.Frames, decoded.Frames);
        Assert.Equal(src.At(1000, 1), decoded.At(1000, 1), 5);
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData("ogg")]
    [InlineData("wav")]
    public void EncodedAudio_DecodesWithZeroOffsetInBass(string format)
    {
        var src = TestSignal(44100, 3.0);
        var dir = Path.Combine(Path.GetTempPath(), "cvmaker-tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"roundtrip.{format}");
        switch (format)
        {
            case "mp3": Mp3Encoder.Encode(src, path, 192); break;
            case "ogg": OggEncoder.Encode(src, path, 0.6f); break;
            default: WavWriter.WritePcm16(path, src); break;
        }
        var decoded = BassDecoder.DecodeFile(path);
        var report = OffsetVerifier.Verify(src, decoded);
        Assert.True(report.Correlation > 0.95, report.Message);
        Assert.InRange(report.LagSamples, -1, 1);
        Assert.True(report.IsAligned, report.Message);
        // the encoded file must not be shorter than what we rendered (a lost tail would truncate the map)
        Assert.True(decoded.Frames >= src.Frames - 1, $"decoded {decoded.Frames} frames < source {src.Frames}");
    }

    [Fact]
    public void Exporter_WritesAudioAndBeatmap_WithVerifiedOffset()
    {
        var src = TestSignal(44100, 4.0);
        var beatmap = CVmaker.Core.Osu.OsuFile.Parse(OsuFileTests.Sample);
        var dir = Path.Combine(Path.GetTempPath(), "cvmaker-tests", "export");
        var options = new Export.ExportOptions { OutputDirectory = dir, Format = Export.AudioFormat.Ogg, AudioBaseName = "cut" };
        var report = Export.CutExporter.Export(beatmap, src, new[] { new CutRegion(1000, 2000), new CutRegion(3000, 4000) }, options);
        Assert.True(File.Exists(report.AudioPath));
        Assert.NotNull(report.BeatmapPath);
        Assert.True(File.Exists(report.BeatmapPath!));
        Assert.NotNull(report.Offset);
        Assert.True(report.Offset!.IsAligned, report.Offset.Message);
        var written = CVmaker.Core.Osu.OsuFile.Load(report.BeatmapPath!);
        Assert.Equal("cut.ogg", written.AudioFilename);
        Assert.Equal("Song (Cut Ver.)", written.Title);
        Assert.Equal(2000, report.OutputLengthMs, 0);
    }
}
