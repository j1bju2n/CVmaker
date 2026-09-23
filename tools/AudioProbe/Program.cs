using CVmania.Core.Audio;

// AudioProbe <file>          : decode with BASS and print stream info
// AudioProbe roundtrip [dir] : encode synthetic signals to mp3/ogg/wav, decode with BASS, report lag/length
static PcmAudio Signal(int sr, long frames, bool noise)
{
    var s = new float[frames * 2];
    var rng = new Random(1234);
    for (long f = 0; f < frames; f++)
    {
        double t = (double)f / sr;
        double burst = (t % 0.25) < 0.05 ? 1.0 : 0.0;
        double v = 0.4 * Math.Sin(2 * Math.PI * 440 * t) * burst + (noise ? 0.05 * (rng.NextDouble() * 2 - 1) : 0);
        s[f * 2] = (float)v; s[f * 2 + 1] = (float)(0.7 * v);
    }
    return new PcmAudio(s, sr, 2);
}

static double CorrAtLag(PcmAudio a0, PcmAudio b0, int lag, int start, int len)
{
    var a = a0.ToMono(); var b = b0.ToMono();
    double dot = 0, ea = 0, eb = 0;
    for (int i = 0; i < len; i++)
    {
        int j = start + i + lag; if (j < 0 || j >= b.Length) continue;
        dot += a[start + i] * b[j]; ea += a[start + i] * a[start + i]; eb += b[j] * b[j];
    }
    return ea > 0 && eb > 0 ? dot / Math.Sqrt(ea * eb) : 0;
}

static void Report(string label, PcmAudio src, PcmAudio dec)
{
    Console.WriteLine($"{label}: src={src.Frames} dec={dec.Frames} diff={dec.Frames - src.Frames}");
    var r = OffsetVerifier.Verify(src, dec, 60);
    Console.WriteLine($"   verify: lag={r.LagSamples} ({r.LagMs:0.00} ms) corr={r.Correlation:0.000} rms={r.ResidualRms:0.0000}");
    int len = 44100;
    Console.WriteLine($"   corr@0 first1s={CorrAtLag(src, dec, 0, 3000, len):0.000} mid={CorrAtLag(src, dec, 0, (int)src.Frames / 2, len):0.000} corr@-1024={CorrAtLag(src, dec, -1024, 3000, len):0.000} corr@+1024={CorrAtLag(src, dec, 1024, 3000, len):0.000}");
}

// AudioProbe export <file.osu> <outDir> <startMs-endMs> [more regions...] : real export through CutExporter
if (args.Length >= 4 && args[0] == "export")
{
    var osu = CVmania.Core.Osu.OsuFile.Load(args[1]);
    var audioPath = Path.Combine(Path.GetDirectoryName(args[1])!, osu.AudioFilename);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var pcmIn = BassDecoder.DecodeFile(audioPath);
    Console.WriteLine($"decoded {osu.AudioFilename}: {pcmIn.SampleRate} Hz {pcmIn.Channels} ch {pcmIn.DurationMs:0.000} ms in {sw.ElapsedMilliseconds} ms");
    var regs = args.Skip(3).Select(a => { var q = a.Split('-'); return new CVmania.Core.Cut.CutRegion(int.Parse(q[0]), int.Parse(q[1])); }).ToList();
    var opt = new CVmania.Core.Export.ExportOptions { OutputDirectory = args[2], Format = CVmania.Core.Export.AudioFormat.Mp3 };
    sw.Restart();
    var rep = CVmania.Core.Export.CutExporter.Export(osu, pcmIn, regs, opt, new Progress<string>(m => Console.WriteLine("  " + m)));
    Console.WriteLine($"export done in {sw.ElapsedMilliseconds} ms: {rep.AudioPath}");
    Console.WriteLine($"  beatmap: {rep.BeatmapPath}");
    Console.WriteLine($"  length {rep.OutputLengthMs:0.000} ms; offset: {rep.Offset?.Message}");
    if (rep.Beatmap != null) Console.WriteLine($"  kept={rep.Beatmap.KeptObjects} dropped={rep.Beatmap.DroppedObjects} clamped={rep.Beatmap.ClampedObjects} redlines+={rep.Beatmap.InsertedRedLines}");
    foreach (var w in rep.Warnings) Console.WriteLine("  warn: " + w);
    return;
}

if (args.Length > 0 && args[0] != "roundtrip")
{
    var pcm = BassDecoder.DecodeFile(args[0]);
    Console.WriteLine($"{args[0]}: {pcm.SampleRate} Hz, {pcm.Channels} ch, {pcm.Frames} frames, {pcm.DurationMs:0.000} ms (BASS {BassRuntime.Version})");
    return;
}

var dir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "cvmania-probe");
Directory.CreateDirectory(dir);
foreach (var frames in new long[] { 132300, 132800, 133300, 100000, 44100 * 10 })
{
    var src = Signal(44100, frames, false);
    Console.WriteLine($"== frames={frames}");
    var ogg = Path.Combine(dir, "probe.ogg"); OggEncoder.Encode(src, ogg, 0.6f); Report("ogg", src, BassDecoder.DecodeFile(ogg));
}
{
    var src = Signal(44100, 132300, true);
    Console.WriteLine("== noise signal");
    var mp3 = Path.Combine(dir, "probe.mp3"); Mp3Encoder.Encode(src, mp3, 192); Report("mp3", src, BassDecoder.DecodeFile(mp3));
    var ogg = Path.Combine(dir, "probe.ogg"); OggEncoder.Encode(src, ogg, 0.6f); Report("ogg", src, BassDecoder.DecodeFile(ogg));
}
