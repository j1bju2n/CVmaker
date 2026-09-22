using System.Globalization;
using System.Text.RegularExpressions;
using CVmaker.Core.Osu;
using CVmaker.Core.Timing;

namespace CVmaker.Core.Cut;

public enum NoteIssueKind
{
    /// <summary>A hold note ends on or after the next note in the same column.</summary>
    Overlap,
    /// <summary>Two notes start at the same time in the same column.</summary>
    Duplicate,
    /// <summary>A hold note was shortened by a cut point to less than a quarter beat.</summary>
    ShortHold,
}

/// <summary>A note problem created by the cut (reported, never silently fixed).</summary>
public sealed record NoteIssue(NoteIssueKind Kind, int OutputTimeMs, double OriginalTimeMs, int Column, int LengthMs);

public sealed class CutResult
{
    public required OsuFile Output { get; init; }
    public required TimeMap Map { get; init; }
    public List<string> Warnings { get; } = new();
    /// <summary>Overlapping / duplicated / over-shortened notes in the output, sorted by time.</summary>
    public List<NoteIssue> Issues { get; } = new();
    public int KeptObjects { get; set; }
    public int DroppedObjects { get; set; }
    public int ClampedObjects { get; set; }
    public int InsertedRedLines { get; set; }
    public int OutputLengthMs => Map.OutputLengthMs;
}

/// <summary>
/// Rewrites a beatmap so that it matches the cut audio produced by <see cref="Audio.CutRenderer"/>
/// for the same regions. Timing is preserved exactly: every kept region starts on a red line whose
/// grid continues the original one, unless the original grid already continues across the join.
/// </summary>
public static class CutPlanner
{
    public static CutResult Transform(OsuFile source, IReadOnlyList<CutRegion> regions, CutOptions options, string newAudioFilename)
    {
        var normalized = CutRegion.Normalize(regions);
        if (normalized.Count == 0) throw new ArgumentException("At least one non-empty region is required.", nameof(regions));

        var timing = new TimingModel(source.ReadTimingPoints());
        if (!timing.HasTiming) throw new InvalidOperationException("The beatmap has no uninherited (red) timing point.");

        var map = new TimeMap(normalized);
        var output = OsuFile.Parse(source.Serialize());
        output.SourcePath = null;
        var result = new CutResult { Output = output, Map = map };

        TransformTimingPoints(output, timing, map, options, result);
        if (options.KeepHitObjects) TransformHitObjects(source, output, timing, map, result);
        else
        {
            result.DroppedObjects = source.ReadHitObjects().Count;
            output.WriteHitObjects(Array.Empty<HitObject>());
        }
        TransformEvents(output, map, result);
        TransformEditor(output, map);
        TransformGeneral(output, map, newAudioFilename);
        TransformMetadata(output, options);
        return result;
    }

    // ------------------------------------------------------------------ timing
    private sealed record OutRed(double OutTime, TimingPoint Source);

    private static void TransformTimingPoints(OsuFile output, TimingModel timing, TimeMap map, CutOptions opt, CutResult result)
    {
        var outPoints = new List<TimingPoint>();
        OutRed? cur = null;
        TimingState? curState = null;
        double tol = opt.BeatToleranceMs;

        for (int i = 0; i < map.Regions.Count; i++)
        {
            var r = map.Regions[i];
            double s = r.StartMs, e = r.EndMs, n = map.OutputStarts[i];
            var red = timing.ActiveRedLine(s)!;
            var state = timing.StateAt(s)!.Value;
            var bl = red.BeatLength;

            bool needRed;
            double redOutTime = n;
            if (i == 0 && red.Time > s && red.Time < e)
            {
                // the first red line lies inside the first region: it is copied below at its shifted
                // position, and osu! applies it to everything before it, exactly like the original
                needRed = false;
            }
            else if (cur == null)
            {
                needRed = true;
                var phase = TimingModel.Mod(s - red.Time, bl);
                if (phase > tol && bl - phase > tol)
                {
                    // first region does not start on a beat: keep the phase with a (negative) offset
                    redOutTime = n - phase;
                }
            }
            else if (ReferenceEquals(cur.Source, red))
            {
                var phOrig = TimingModel.Mod(s - red.Time, bl);
                var phOut = TimingModel.Mod(n - cur.OutTime, bl);
                var d = Math.Abs(phOrig - phOut);
                d = Math.Min(d, bl - d);
                needRed = d > tol;
                if (needRed)
                    result.Warnings.Add($"Region {i + 1} ({FormatMs(s)}): join is not beat-aligned (off by {d:0.##} ms); a red line was inserted at {FormatMs(n)}.");
            }
            else needRed = true;

            if (needRed)
            {
                outPoints.Add(new TimingPoint
                {
                    Time = redOutTime,
                    BeatLengthRaw = red.BeatLengthRaw,
                    Meter = red.Meter,
                    SampleSet = state.SampleSet,
                    SampleIndex = state.SampleIndex,
                    Volume = state.Volume,
                    Uninherited = true,
                    Effects = state.Effects,
                });
                result.InsertedRedLines++;
                cur = new OutRed(redOutTime, red);
                if (state.ActiveGreen != null)
                {
                    var g = state.ActiveGreen.Clone();
                    g.Time = n;
                    g.SampleSet = state.SampleSet;
                    g.SampleIndex = state.SampleIndex;
                    g.Volume = state.Volume;
                    g.Effects = state.Effects;
                    outPoints.Add(g);
                }
            }
            else if (i > 0 && (curState == null || !curState.Value.SameAs(state)))
            {
                // grid continues, but SV / samples / kiai differ from what is in effect: carry the state over
                var g = (state.ActiveGreen?.Clone()) ?? new TimingPoint { BeatLengthRaw = "-100", Uninherited = false };
                g.Time = n;
                g.Meter = red.Meter;
                g.SampleSet = state.SampleSet;
                g.SampleIndex = state.SampleIndex;
                g.Volume = state.Volume;
                g.Effects = state.Effects;
                outPoints.Add(g);
            }

            // interior points, shifted
            foreach (var p in timing.All)
            {
                if (p.Time <= s) continue;
                if (p.Time >= e) break;
                var c = p.Clone();
                c.Time = p.Time - s + n;
                if (p.IsRedLine && cur != null && ReferenceEquals(cur.Source, p))
                {
                    // the inserted red line already carries this grid; skip the copy when it would be redundant
                    var d = TimingModel.Mod(c.Time - cur.OutTime, p.BeatLength);
                    d = Math.Min(d, p.BeatLength - d);
                    if (d <= tol) continue;
                }
                outPoints.Add(c);
                if (p.IsRedLine) cur = new OutRed(c.Time, p);
            }

            curState = timing.StateBefore(e);
        }

        output.WriteTimingPoints(outPoints);
    }

    // -------------------------------------------------------------- hit objects
    private static void TransformHitObjects(OsuFile source, OsuFile output, TimingModel timing, TimeMap map, CutResult result)
    {
        var objects = source.ReadHitObjects();
        var kept = new List<HitObject>();
        var clamped = new HashSet<HitObject>(ReferenceEqualityComparer.Instance);
        var sliderMultiplier = source.SliderMultiplier;
        int droppedSliders = 0;

        foreach (var ho in objects)
        {
            int ri = map.RegionIndexOf(ho.Time);
            if (ri < 0) { result.DroppedObjects++; continue; }
            var region = map.Regions[ri];
            var c = ho.Clone();
            switch (ho.Kind)
            {
                case HitObjectKind.Hold:
                case HitObjectKind.Spinner:
                {
                    var end = ho.EndTime;
                    if (end > region.EndMs)
                    {
                        end = region.EndMs;
                        result.ClampedObjects++;
                        clamped.Add(c);
                    }
                    c.Time = (int)Math.Round(map.MapIn(ri, ho.Time));
                    c.EndTime = (int)Math.Round(map.MapIn(ri, end));
                    if (c.EndTime <= c.Time && ho.Kind == HitObjectKind.Hold)
                    {
                        // degenerate hold: keep it as a plain note
                        c.Type = (c.Type & ~128) | 1;
                        c.Extras.Clear();
                        var hs = ho.Extras.Count > 0 ? ho.Extras[0] : "";
                        var idx = hs.IndexOf(':');
                        c.Extras.Add(idx >= 0 ? hs[(idx + 1)..] : "0:0:0:0:");
                    }
                    break;
                }
                case HitObjectKind.Slider:
                {
                    var endTime = SliderEndTime(ho, timing, sliderMultiplier);
                    if (endTime > region.EndMs + 1)
                    {
                        droppedSliders++;
                        result.DroppedObjects++;
                        continue;
                    }
                    c.Time = (int)Math.Round(map.MapIn(ri, ho.Time));
                    break;
                }
                default:
                    c.Time = (int)Math.Round(map.MapIn(ri, ho.Time));
                    break;
            }
            kept.Add(c);
        }

        kept.Sort((a, b) => a.Time.CompareTo(b.Time));
        result.KeptObjects = kept.Count;
        if (result.ClampedObjects > 0)
            result.Warnings.Add($"{result.ClampedObjects} hold/spinner object(s) crossed a cut point and were shortened to the region end.");
        if (droppedSliders > 0)
            result.Warnings.Add($"{droppedSliders} slider(s) crossed a cut point and were removed.");
        output.WriteHitObjects(kept);
        FindNoteIssues(source, kept, clamped, timing, map, result);
    }

    /// <summary>
    /// Looks for notes the cut made unplayable: hold notes that now end on/after the next note of
    /// their column, duplicated notes, and clamped hold notes shorter than a quarter beat.
    /// </summary>
    private static void FindNoteIssues(OsuFile source, List<HitObject> kept, HashSet<HitObject> clamped, TimingModel timing, TimeMap map, CutResult result)
    {
        bool mania = source.Mode == 3;
        int keys = mania ? Math.Max(1, (int)Math.Round(source.CircleSize)) : 1;
        var issues = new List<NoteIssue>();

        if (mania)
        {
            foreach (var group in kept.GroupBy(o => o.Column(keys)))
            {
                var col = group.OrderBy(o => o.Time).ThenBy(o => o.EndTime).ToList();
                for (int i = 0; i + 1 < col.Count; i++)
                {
                    var a = col[i];
                    var b = col[i + 1];
                    if (a.Time == b.Time)
                        issues.Add(new NoteIssue(NoteIssueKind.Duplicate, b.Time, map.Unmap(b.Time) ?? -1, group.Key + 1, 0));
                    else if (a.Kind == HitObjectKind.Hold && a.EndTime >= b.Time)
                        issues.Add(new NoteIssue(NoteIssueKind.Overlap, b.Time, map.Unmap(b.Time) ?? -1, group.Key + 1, a.EndTime - b.Time));
                }
            }
        }

        foreach (var ho in kept)
        {
            if (ho.Kind != HitObjectKind.Hold || !clamped.Contains(ho)) continue;
            var orig = map.Unmap(ho.Time) ?? ho.Time;
            var red = timing.ActiveRedLine(orig);
            double quarter = red != null ? red.BeatLength / 4 : 100;
            int length = ho.EndTime - ho.Time;
            if (length < quarter - 0.5)
                issues.Add(new NoteIssue(NoteIssueKind.ShortHold, ho.Time, orig, mania ? ho.Column(keys) + 1 : 1, length));
        }

        result.Issues.AddRange(issues.OrderBy(i => i.OutputTimeMs).ThenBy(i => i.Column));
    }

    private static double SliderEndTime(HitObject ho, TimingModel timing, double sliderMultiplier)
    {
        if (!ho.TryGetSliderParams(out var slides, out var length)) return ho.Time;
        var red = timing.ActiveRedLine(ho.Time);
        if (red == null) return ho.Time;
        var sv = timing.ActiveGreenLine(ho.Time)?.SvMultiplier ?? 1.0;
        var velocity = sliderMultiplier * 100.0 * sv; // osu!pixels per beat
        if (velocity <= 0) return ho.Time;
        return ho.Time + length / velocity * red.BeatLength * Math.Max(1, slides);
    }

    // ------------------------------------------------------------------- events
    private static readonly Regex BreakRegex = new(@"^\s*(2|Break)\s*,\s*(-?[\d.]+)\s*,\s*(-?[\d.]+)\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex SampleRegex = new(@"^\s*(5|Sample)\s*,\s*(-?[\d.]+)\s*,(.*)$", RegexOptions.IgnoreCase);

    private static void TransformEvents(OsuFile output, TimeMap map, CutResult result)
    {
        var sec = output.GetSection("Events");
        if (sec == null) return;
        var lines = new List<string>();
        int droppedStoryboard = 0, droppedSamples = 0;
        bool videoWarned = false;
        foreach (var line in sec.Lines)
        {
            var t = line.TrimEnd();
            if (t.TrimStart().StartsWith("//") || t.Trim().Length == 0) { lines.Add(line); continue; }

            var m = BreakRegex.Match(t);
            if (m.Success)
            {
                var b0 = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var b1 = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                for (int i = 0; i < map.Regions.Count; i++)
                {
                    var r = map.Regions[i];
                    var s = Math.Max(b0, r.StartMs);
                    var e = Math.Min(b1, r.EndMs);
                    if (e - s < 1) continue;
                    lines.Add($"2,{TimingPoint.FormatTime(Math.Round(map.MapIn(i, s)))},{TimingPoint.FormatTime(Math.Round(map.MapIn(i, e)))}");
                }
                continue;
            }

            m = SampleRegex.Match(t);
            if (m.Success)
            {
                var st = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var mapped = map.Map(st);
                if (mapped == null) { droppedSamples++; continue; }
                lines.Add($"{m.Groups[1].Value},{TimingPoint.FormatTime(Math.Round(mapped.Value))},{m.Groups[3].Value}");
                continue;
            }

            var lead = t.TrimStart();
            if (lead.StartsWith("Sprite", StringComparison.OrdinalIgnoreCase) || lead.StartsWith("Animation", StringComparison.OrdinalIgnoreCase) ||
                lead.StartsWith("4,") || lead.StartsWith("6,") || t.StartsWith(' ') || t.StartsWith('_'))
            {
                droppedStoryboard++;
                continue;
            }

            if (lead.StartsWith("Video", StringComparison.OrdinalIgnoreCase) || lead.StartsWith("1,"))
            {
                if (!videoWarned)
                {
                    result.Warnings.Add("The beatmap has a background video. Videos are not cut; the video line was kept unchanged.");
                    videoWarned = true;
                }
            }
            lines.Add(line);
        }
        if (droppedStoryboard > 0)
            result.Warnings.Add($"{droppedStoryboard} storyboard line(s) in the .osu were removed because storyboard timings are not re-mapped.");
        if (droppedSamples > 0)
            result.Warnings.Add($"{droppedSamples} storyboard sample event(s) fell outside the kept regions and were removed.");
        sec.Lines.Clear();
        sec.Lines.AddRange(lines);
    }

    // ------------------------------------------------------------------- editor
    private static void TransformEditor(OsuFile output, TimeMap map)
    {
        var sec = output.GetSection("Editor");
        var bookmarks = sec?.GetValue("Bookmarks");
        if (sec == null || string.IsNullOrWhiteSpace(bookmarks)) return;
        var mapped = new List<string>();
        foreach (var part in bookmarks.Split(','))
        {
            if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) continue;
            var m = map.Map(b);
            if (m != null) mapped.Add(TimingPoint.FormatTime(Math.Round(m.Value)));
        }
        if (mapped.Count == 0) sec.RemoveKey("Bookmarks");
        else sec.SetValue("Bookmarks", string.Join(",", mapped));
    }

    // ------------------------------------------------------------------ general
    private static void TransformGeneral(OsuFile output, TimeMap map, string newAudioFilename)
    {
        output.Set("General", "AudioFilename", newAudioFilename);
        var preview = output.PreviewTime;
        var mapped = preview >= 0 ? map.Map(preview) : null;
        output.Set("General", "PreviewTime", mapped != null ? ((int)Math.Round(mapped.Value)).ToString(CultureInfo.InvariantCulture) : "-1");
    }

    // ----------------------------------------------------------------- metadata
    private static void TransformMetadata(OsuFile output, CutOptions opt)
    {
        var meta = output.GetOrAddSection("Metadata");
        if (opt.AppendTitleSuffix && !string.IsNullOrEmpty(opt.TitleSuffix))
        {
            foreach (var key in new[] { "Title", "TitleUnicode" })
            {
                var v = meta.GetValue(key);
                if (v == null) continue;
                if (!v.EndsWith(opt.TitleSuffix.Trim(), StringComparison.Ordinal))
                    meta.SetValue(key, v + opt.TitleSuffix);
            }
        }
        if (!string.IsNullOrWhiteSpace(opt.VersionOverride)) meta.SetValue("Version", opt.VersionOverride.Trim());
        if (!string.IsNullOrWhiteSpace(opt.CreatorOverride)) meta.SetValue("Creator", opt.CreatorOverride.Trim());
        meta.SetValue("BeatmapID", "0");
        meta.SetValue("BeatmapSetID", "-1");
    }

    public static string FormatMs(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }
}
