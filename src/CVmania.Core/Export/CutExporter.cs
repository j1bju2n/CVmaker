using CVmania.Core.Audio;
using CVmania.Core.Cut;
using CVmania.Core.Osu;

namespace CVmania.Core.Export;

public enum AudioFormat { Mp3, Ogg, Wav }

public sealed class ExportOptions
{
    public AudioFormat Format { get; set; } = AudioFormat.Mp3;
    /// <summary>MP3 CBR bitrate in kbps (ranking criteria allow up to 192).</summary>
    public int Mp3BitrateKbps { get; set; } = 192;
    /// <summary>Vorbis quality 0..1 (0.6 is roughly 192 kbps).</summary>
    public float OggQuality { get; set; } = 0.6f;
    /// <summary>Base name of the audio file without extension.</summary>
    public string AudioBaseName { get; set; } = "audio";
    /// <summary>Directory that will receive the audio and the .osu.</summary>
    public required string OutputDirectory { get; set; }
    public bool WriteBeatmap { get; set; } = true;
    /// <summary>Copy the background image (and nothing else) next to the new beatmap.</summary>
    public bool CopyBackground { get; set; } = true;
    /// <summary>Decode the encoded file again and check it lines up with the rendered PCM.</summary>
    public bool VerifyOffset { get; set; } = true;
    public CutOptions Cut { get; set; } = new();
}

public sealed class ExportReport
{
    public required string AudioPath { get; init; }
    public string? BeatmapPath { get; init; }
    public CutResult? Beatmap { get; init; }
    public OffsetReport? Offset { get; init; }
    public List<string> Warnings { get; } = new();
    public double OutputLengthMs { get; init; }
}

/// <summary>Renders, encodes, verifies and writes the beatmap for a cut.</summary>
public static class CutExporter
{
    public static string ExtensionFor(AudioFormat f) => f switch
    {
        AudioFormat.Mp3 => ".mp3",
        AudioFormat.Ogg => ".ogg",
        _ => ".wav",
    };

    public static ExportReport Export(OsuFile beatmap, PcmAudio sourceAudio, IReadOnlyList<CutRegion> regions, ExportOptions options,
        IProgress<string>? progress = null)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var audioName = options.AudioBaseName + ExtensionFor(options.Format);
        var audioPath = Path.Combine(options.OutputDirectory, audioName);

        progress?.Report("Rendering audio...");
        var rendered = CutRenderer.Render(sourceAudio, regions, options.Cut);

        progress?.Report($"Encoding {audioName}...");
        var offset = EncodeVerified(rendered, audioPath, options, progress);

        CutResult? cut = null;
        string? beatmapPath = null;
        var warnings = new List<string>();
        if (options.WriteBeatmap)
        {
            progress?.Report("Writing beatmap...");
            cut = CutPlanner.Transform(beatmap, regions, options.Cut, audioName);
            warnings.AddRange(cut.Warnings);
            beatmapPath = Path.Combine(options.OutputDirectory, BuildOsuFileName(cut.Output));
            cut.Output.Save(beatmapPath);

            if (options.CopyBackground && beatmap.SourcePath != null)
            {
                var bg = FindBackground(beatmap);
                if (bg != null)
                {
                    var srcDir = Path.GetDirectoryName(beatmap.SourcePath)!;
                    var from = Path.Combine(srcDir, bg);
                    var to = Path.Combine(options.OutputDirectory, bg);
                    if (File.Exists(from) && !string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        File.Copy(from, to, true);
                    }
                }
            }
        }

        var report = new ExportReport
        {
            AudioPath = audioPath,
            BeatmapPath = beatmapPath,
            Beatmap = cut,
            Offset = offset,
            OutputLengthMs = rendered.DurationMs,
        };
        report.Warnings.AddRange(warnings);
        if (offset != null && !offset.IsAligned) report.Warnings.Add("Offset check: " + offset.Message);
        return report;
    }

    /// <summary>
    /// Encode, then decode the file again with BASS (what osu! will do) and compare against the
    /// rendered PCM. If the decoder reports a shift, re-encode once with compensating padding.
    /// </summary>
    private static OffsetReport? EncodeVerified(PcmAudio rendered, string audioPath, ExportOptions options, IProgress<string>? progress)
    {
        int correction = 0; // frames added (>0) or dropped (<0) at the start before encoding
        OffsetReport? report = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            switch (options.Format)
            {
                case AudioFormat.Mp3: Mp3Encoder.Encode(PadStart(rendered, correction), audioPath, options.Mp3BitrateKbps); break;
                case AudioFormat.Ogg: OggEncoder.Encode(rendered, audioPath, options.OggQuality, OggEncoder.DefaultPrePadFrames + correction); break;
                default: WavWriter.WritePcm16(audioPath, PadStart(rendered, correction)); break;
            }
            if (!options.VerifyOffset) return null;

            progress?.Report("Verifying offset with BASS...");
            var decoded = BassDecoder.DecodeFile(audioPath);
            report = OffsetVerifier.Verify(rendered, decoded);
            if (report.Correlation < 0.9 || Math.Abs(report.LagSamples) <= 1) return report;
            // decoded audio is early (negative lag => samples missing at the start) or late: compensate
            correction -= report.LagSamples;
            progress?.Report($"Decoder reported {report.LagMs:+0.00;-0.00} ms; re-encoding with correction...");
        }
        return report;
    }

    private static PcmAudio PadStart(PcmAudio pcm, int frames)
    {
        if (frames == 0) return pcm;
        int ch = pcm.Channels;
        if (frames > 0)
        {
            var s = new float[(pcm.Frames + frames) * ch];
            Array.Copy(pcm.Samples, 0, s, (long)frames * ch, pcm.Samples.Length);
            return new PcmAudio(s, pcm.SampleRate, ch);
        }
        long drop = Math.Min(pcm.Frames, -frames);
        var t = new float[(pcm.Frames - drop) * ch];
        Array.Copy(pcm.Samples, drop * ch, t, 0, t.Length);
        return new PcmAudio(t, pcm.SampleRate, ch);
    }

    /// <summary>"Artist - Title (Creator) [Version].osu" with invalid characters removed, as osu! does.</summary>
    public static string BuildOsuFileName(OsuFile f)
    {
        var name = $"{f.Artist} - {f.Title} ({f.Creator}) [{f.Version}].osu";
        return SanitizeFileName(name);
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return s.Length == 0 ? "untitled" : s;
    }

    /// <summary>Background file name from [Events] (0,0,"bg.jpg",...), or null.</summary>
    public static string? FindBackground(OsuFile f)
    {
        var sec = f.GetSection("Events");
        if (sec == null) return null;
        foreach (var line in sec.Lines)
        {
            var t = line.Trim();
            if (t.StartsWith("//") || t.Length == 0) continue;
            var parts = t.Split(',');
            if (parts.Length >= 3 && (parts[0] == "0" || parts[0].Equals("Background", StringComparison.OrdinalIgnoreCase)))
                return parts[2].Trim().Trim('"');
        }
        return null;
    }
}
