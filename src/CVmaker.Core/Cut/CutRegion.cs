namespace CVmaker.Core.Cut;

/// <summary>How the audio at a region edge is treated.</summary>
public enum EdgeMode
{
    /// <summary>Start: the region's first N ms ramp up from silence. End: N ms of the audio after the region ramp down to silence.</summary>
    Fade = 0,
    /// <summary>N ms of the audio outside the region are included at full volume (the audio simply gets longer).</summary>
    Extend = 1,
}

/// <summary>
/// A stretch of the original audio that is kept, in whole milliseconds [Start, End).
/// <see cref="FadeInMs"/>/<see cref="FadeInMode"/> shape the start, <see cref="FadeOutMs"/>/<see cref="FadeOutMode"/> the end.
/// Audio that is added outside the region (extend-in, extend-out, fade-out) is inserted into the
/// output timeline; the region's own length and timing never change.
/// </summary>
public readonly record struct CutRegion(int StartMs, int EndMs, int FadeInMs = 0, int FadeOutMs = 0,
    EdgeMode FadeInMode = EdgeMode.Fade, EdgeMode FadeOutMode = EdgeMode.Fade)
{
    public const int MaxFadeMs = 1000;

    public int LengthMs => EndMs - StartMs;
    public bool IsEmpty => LengthMs <= 0;
    public bool Contains(double t) => t >= StartMs && t < EndMs;
    public bool ContainsRange(double a, double b) => a >= StartMs && b <= EndMs;
    public bool Overlaps(double a, double b) => a < EndMs && b > StartMs;

    /// <summary>Audio inserted before the region (extend-in only), ms.</summary>
    public int PreMs => FadeInMode == EdgeMode.Extend ? Math.Clamp(FadeInMs, 0, Math.Max(0, StartMs)) : 0;
    /// <summary>Length of the ramp inside the region at its start (fade-in only), ms.</summary>
    public int InsideFadeMs => FadeInMode == EdgeMode.Fade ? Math.Clamp(FadeInMs, 0, Math.Max(0, LengthMs)) : 0;
    /// <summary>Audio appended after the region (both end modes), ms.</summary>
    public int PostMs => Math.Max(0, FadeOutMs);

    public static int ClampFade(int ms) => Math.Clamp(ms, 0, MaxFadeMs);

    /// <summary>Largest start value: audio before the region for extend, the region length for a fade.</summary>
    public int MaxFadeIn() => FadeInMode == EdgeMode.Extend ? Math.Clamp(StartMs, 0, MaxFadeMs) : Math.Clamp(LengthMs, 0, MaxFadeMs);

    /// <summary>Largest end value given the song length: the audio that exists after the region.</summary>
    public int MaxFadeOut(double songLengthMs) => Math.Clamp((int)Math.Floor(songLengthMs - EndMs), 0, MaxFadeMs);

    /// <summary>
    /// Sort, drop empties and merge overlapping regions. Regions that merely touch are kept
    /// separate so that a split (Enter inside a region) survives; the renderer and planner treat a
    /// touching join without edge audio as continuous.
    /// </summary>
    public static List<CutRegion> Normalize(IEnumerable<CutRegion> regions)
    {
        var sorted = regions.Where(r => !r.IsEmpty).OrderBy(r => r.StartMs).ThenBy(r => r.EndMs).ToList();
        var result = new List<CutRegion>();
        foreach (var r in sorted)
        {
            if (result.Count > 0 && r.StartMs < result[^1].EndMs)
            {
                var last = result[^1];
                bool extends = r.EndMs > last.EndMs;
                result[^1] = last with
                {
                    EndMs = Math.Max(last.EndMs, r.EndMs),
                    FadeOutMs = extends ? r.FadeOutMs : last.FadeOutMs,
                    FadeOutMode = extends ? r.FadeOutMode : last.FadeOutMode,
                };
            }
            else result.Add(r);
        }
        for (int i = 0; i < result.Count; i++)
        {
            var r = result[i];
            result[i] = r with { FadeInMs = Math.Min(ClampFade(r.FadeInMs), r.MaxFadeIn()), FadeOutMs = ClampFade(r.FadeOutMs) };
        }
        return result;
    }

    /// <summary>Remove [a, b) from every region (trimming or splitting as needed).</summary>
    public static List<CutRegion> Subtract(IEnumerable<CutRegion> regions, int a, int b)
    {
        var result = new List<CutRegion>();
        foreach (var r in regions)
        {
            if (!r.Overlaps(a, b)) { result.Add(r); continue; }
            if (a > r.StartMs) result.Add(r with { EndMs = a, FadeOutMs = 0, FadeOutMode = EdgeMode.Fade });
            if (b < r.EndMs) result.Add(r with { StartMs = b, FadeInMs = 0, FadeInMode = EdgeMode.Fade });
        }
        return Normalize(result);
    }

    /// <summary>
    /// Split the region that fully contains [a, b) so that [a, b) becomes its own region.
    /// Returns the index of the new piece, or -1 when no region contains the range.
    /// </summary>
    public static int Split(List<CutRegion> regions, int a, int b)
    {
        int idx = regions.FindIndex(r => r.ContainsRange(a, b));
        if (idx < 0 || b <= a) return -1;
        var r = regions[idx];
        var pieces = new List<CutRegion>();
        if (a > r.StartMs) pieces.Add(r with { EndMs = a, FadeOutMs = 0, FadeOutMode = EdgeMode.Fade });
        pieces.Add(new CutRegion(a, b,
            a == r.StartMs ? r.FadeInMs : 0, b == r.EndMs ? r.FadeOutMs : 0,
            a == r.StartMs ? r.FadeInMode : EdgeMode.Fade, b == r.EndMs ? r.FadeOutMode : EdgeMode.Fade));
        if (b < r.EndMs) pieces.Add(r with { StartMs = b, FadeInMs = 0, FadeInMode = EdgeMode.Fade });
        regions.RemoveAt(idx);
        regions.InsertRange(idx, pieces);
        return idx + (a > r.StartMs ? 1 : 0);
    }
}

public sealed class CutOptions
{
    /// <summary>De-click crossfade used at seams between two pieces of audio that are both audible.</summary>
    public int CrossfadeMs { get; set; } = 10;
    /// <summary>Silence appended after the last region (after its fade-out/extension).</summary>
    public int TailSilenceMs { get; set; } = 0;

    /// <summary>Appended to Title/TitleUnicode of the generated difficulty.</summary>
    public string TitleSuffix { get; set; } = " (Cut Ver.)";
    public bool AppendTitleSuffix { get; set; } = true;
    /// <summary>When set, replaces the difficulty name (Version).</summary>
    public string? VersionOverride { get; set; }
    /// <summary>When set, replaces the mapper name (Creator); the cut is usually made by someone else.</summary>
    public string? CreatorOverride { get; set; }
    /// <summary>False writes an empty difficulty (timing only) for mapping the cut from scratch.</summary>
    public bool KeepHitObjects { get; set; } = true;
    /// <summary>Tolerance (ms) used to decide whether a cut point is on the beat grid.</summary>
    public double BeatToleranceMs { get; set; } = 1.0;
}

/// <summary>
/// Maps original times to output times for a normalized set of regions. Regions are butted
/// together exactly (cut point to cut point). Edge audio (fade-out, extend-out, extend-in) is mixed
/// <em>over</em> the neighbouring region at a join; only the first region's extend-in (lead-in) and the
/// last region's end piece (tail) add audio to the timeline.
/// </summary>
public sealed class TimeMap
{
    public IReadOnlyList<CutRegion> Regions { get; }
    /// <summary>Output start time (ms) of each region.</summary>
    public IReadOnlyList<int> OutputStarts { get; }
    /// <summary>Audio before the first region, in ms.</summary>
    public int LeadInMs { get; }
    /// <summary>Audio after the last region, in ms.</summary>
    public int TailOutMs { get; }
    /// <summary>Total audio length.</summary>
    public int OutputLengthMs { get; }
    /// <summary>Output time where the last region ends (start of the tail).</summary>
    public int RegionsEndMs { get; }

    public TimeMap(IReadOnlyList<CutRegion> normalizedRegions)
    {
        Regions = normalizedRegions;
        var starts = new List<int>();
        LeadInMs = normalizedRegions.Count > 0 ? normalizedRegions[0].PreMs : 0;
        int acc = LeadInMs;
        foreach (var r in normalizedRegions)
        {
            starts.Add(acc);
            acc += r.LengthMs;
        }
        OutputStarts = starts;
        RegionsEndMs = acc;
        TailOutMs = normalizedRegions.Count > 0 ? normalizedRegions[^1].PostMs : 0;
        OutputLengthMs = acc + TailOutMs;
    }

    public int RegionIndexOf(double t, bool inclusiveEnd = false)
    {
        for (int i = 0; i < Regions.Count; i++)
        {
            var r = Regions[i];
            if (t >= r.StartMs && (t < r.EndMs || (inclusiveEnd && t == r.EndMs))) return i;
        }
        return -1;
    }

    /// <summary>Output time for an original time, or null when the time is cut away.</summary>
    public double? Map(double t, bool inclusiveEnd = false)
    {
        var i = RegionIndexOf(t, inclusiveEnd);
        if (i < 0) return null;
        return t - Regions[i].StartMs + OutputStarts[i];
    }

    /// <summary>Output time for an original time within a known region.</summary>
    public double MapIn(int regionIndex, double t) => t - Regions[regionIndex].StartMs + OutputStarts[regionIndex];

    /// <summary>Original time for an output time (lead-in and tail included), for the preview playhead.</summary>
    public double? Unmap(double outT)
    {
        if (Regions.Count == 0) return null;
        if (outT < LeadInMs) return Regions[0].StartMs - (LeadInMs - outT);
        for (int i = 0; i < Regions.Count; i++)
        {
            var s = OutputStarts[i];
            if (outT >= s && outT < s + Regions[i].LengthMs) return outT - s + Regions[i].StartMs;
        }
        if (outT >= RegionsEndMs && outT < OutputLengthMs) return Regions[^1].EndMs + (outT - RegionsEndMs);
        return null;
    }
}
