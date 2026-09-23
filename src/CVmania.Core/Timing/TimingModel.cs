using CVmania.Core.Osu;

namespace CVmania.Core.Timing;

/// <summary>A stretch of constant BPM between two red lines.</summary>
public sealed class TimingSection
{
    public required TimingPoint Source { get; init; }
    public double Start => Source.Time;
    /// <summary>Start of the next red line, or +inf for the last section.</summary>
    public double End { get; init; }
    public double BeatLength => Source.BeatLength;
    public int Meter => Source.Meter;
    public double Bpm => Source.Bpm;
}

/// <summary>Sample/volume/SV/kiai state in effect at a point in time.</summary>
public readonly record struct TimingState(
    TimingPoint RedLine,
    TimingPoint? ActiveGreen,
    int SampleSet,
    int SampleIndex,
    int Volume,
    int Effects)
{
    public double Sv => ActiveGreen?.SvMultiplier ?? 1.0;
    public bool Kiai => (Effects & 1) != 0;

    /// <summary>True when the values that matter for playback are identical.</summary>
    public bool SameAs(TimingState o) =>
        Math.Abs(Sv - o.Sv) < 1e-9 && SampleSet == o.SampleSet && SampleIndex == o.SampleIndex &&
        Volume == o.Volume && Effects == o.Effects;
}

public enum SnapMode { Nearest, Floor, Ceiling }

/// <summary>
/// Beat grid derived from a beatmap's timing points, following osu! stable semantics:
/// objects before the first red line use the first red line, an inherited point only applies
/// after the red line it belongs to, and a red line resets SV to 1.0.
/// </summary>
public sealed class TimingModel
{
    /// <summary>All points sorted by time; red lines precede green lines at equal times.</summary>
    public IReadOnlyList<TimingPoint> All { get; }
    public IReadOnlyList<TimingPoint> RedLines { get; }
    public IReadOnlyList<TimingSection> Sections { get; }

    public TimingModel(IEnumerable<TimingPoint> points)
    {
        var list = points.Where(p => !(p.IsRedLine && p.BeatLength <= 0)).ToList();
        // stable sort: red before green at equal time
        All = list.OrderBy(p => p.Time).ThenBy(p => p.IsRedLine ? 0 : 1).ToList();
        RedLines = All.Where(p => p.IsRedLine).ToList();
        var secs = new List<TimingSection>();
        for (int i = 0; i < RedLines.Count; i++)
        {
            secs.Add(new TimingSection
            {
                Source = RedLines[i],
                End = i + 1 < RedLines.Count ? RedLines[i + 1].Time : double.PositiveInfinity
            });
        }
        Sections = secs;
    }

    public bool HasTiming => RedLines.Count > 0;

    /// <summary>Red line governing time t (the first red line when t precedes all of them).</summary>
    public TimingPoint? ActiveRedLine(double t)
    {
        if (RedLines.Count == 0) return null;
        TimingPoint? best = null;
        foreach (var r in RedLines)
        {
            if (r.Time <= t) best = r; else break;
        }
        return best ?? RedLines[0];
    }

    public TimingSection? SectionAt(double t)
    {
        var r = ActiveRedLine(t);
        return r == null ? null : Sections.First(s => ReferenceEquals(s.Source, r));
    }

    /// <summary>Last inherited point that belongs to the active red line and is not after t.</summary>
    public TimingPoint? ActiveGreenLine(double t)
    {
        var red = ActiveRedLine(t);
        if (red == null) return null;
        TimingPoint? best = null;
        foreach (var p in All)
        {
            if (p.Time > t) break;
            if (p.IsRedLine) { best = null; continue; } // a red line resets SV
            if (p.Time >= red.Time) best = p;
        }
        return best;
    }

    /// <summary>Effective state at t. Before the first point, the first point's values apply.</summary>
    public TimingState? StateAt(double t)
    {
        var red = ActiveRedLine(t);
        if (red == null) return null;
        TimingPoint? last = null;
        foreach (var p in All)
        {
            if (p.Time > t) break;
            last = p;
        }
        last ??= All[0];
        return new TimingState(red, ActiveGreenLine(t), last.SampleSet, last.SampleIndex, last.Volume, last.Effects);
    }

    /// <summary>State just before t (points at exactly t are excluded).</summary>
    public TimingState? StateBefore(double t) => StateAt(BitDecrement(t));

    private static double BitDecrement(double t) => Math.BitDecrement(t);

    /// <summary>Snap t to the grid of its section with the given divisor (1 = whole beats).</summary>
    public double Snap(double t, int divisor, SnapMode mode = SnapMode.Nearest)
    {
        var red = ActiveRedLine(t);
        if (red == null || divisor <= 0) return t;
        var step = red.BeatLength / divisor;
        if (step <= 0) return t;
        var k = (t - red.Time) / step;
        double kk = mode switch
        {
            SnapMode.Floor => Math.Floor(k + 1e-6),
            SnapMode.Ceiling => Math.Ceiling(k - 1e-6),
            _ => Math.Round(k)
        };
        var snapped = red.Time + kk * step;
        // do not cross into the next section's grid
        var section = SectionAt(t)!;
        if (snapped >= section.End && mode != SnapMode.Floor) return section.End;
        return snapped;
    }

    /// <summary>Snap to whole measures (downbeats).</summary>
    public double SnapMeasure(double t, SnapMode mode = SnapMode.Nearest)
    {
        var red = ActiveRedLine(t);
        if (red == null) return t;
        var step = red.BeatLength * red.Meter;
        var k = (t - red.Time) / step;
        double kk = mode switch
        {
            SnapMode.Floor => Math.Floor(k + 1e-6),
            SnapMode.Ceiling => Math.Ceiling(k - 1e-6),
            _ => Math.Round(k)
        };
        return red.Time + kk * step;
    }

    /// <summary>Phase of t inside its beat, in ms (0 = exactly on a beat).</summary>
    public double BeatPhase(double t)
    {
        var red = ActiveRedLine(t);
        if (red == null) return 0;
        return Mod(t - red.Time, red.BeatLength);
    }

    public static double Mod(double a, double m)
    {
        if (m <= 0) return 0;
        var r = a % m;
        if (r < 0) r += m;
        return r;
    }

    /// <summary>Beat positions inside [from, to) for drawing; downbeat = first beat of a measure.</summary>
    public IEnumerable<(double Time, bool Downbeat, TimingSection Section)> Beats(double from, double to)
    {
        if (!HasTiming) yield break;
        foreach (var s in Sections)
        {
            var segStart = Math.Max(from, ReferenceEquals(s, Sections[0]) ? double.NegativeInfinity : s.Start);
            var segEnd = Math.Min(to, s.End);
            if (segEnd <= segStart) continue;
            var bl = s.BeatLength;
            if (bl <= 0) continue;
            long k0 = (long)Math.Floor((segStart - s.Start) / bl);
            if (ReferenceEquals(s, Sections[0]) && double.IsNegativeInfinity(segStart)) k0 = 0;
            for (long k = k0; ; k++)
            {
                var t = s.Start + k * bl;
                if (t >= segEnd) break;
                if (t < segStart) continue;
                var beatInMeasure = (int)Mod(k, s.Meter);
                yield return (t, beatInMeasure == 0, s);
            }
        }
    }

    public IEnumerable<(double Start, double End)> KiaiRanges(double songEnd)
    {
        double? start = null;
        foreach (var p in All)
        {
            if (p.Kiai && start == null) start = p.Time;
            else if (!p.Kiai && start != null)
            {
                yield return (start.Value, p.Time);
                start = null;
            }
        }
        if (start != null) yield return (start.Value, songEnd);
    }

    /// <summary>
    /// The beatmap's "most common BPM" as osu! stable computes it for song select and for the osu!mania base
    /// scroll speed: the first red line counts from 0, the last one extends to <paramref name="lastObjectTime"/>,
    /// red lines after that time count nothing, and durations are summed per beat length (rounded to 1 µs).
    /// </summary>
    public double MostCommonBpm(double lastObjectTime) => 60000.0 / MostCommonBeatLength(lastObjectTime);

    /// <inheritdoc cref="MostCommonBpm"/>
    public double MostCommonBeatLength(double lastObjectTime)
    {
        if (!HasTiming) return 500;
        var order = new List<double>();
        var durations = new Dictionary<double, double>();
        for (int i = 0; i < RedLines.Count; i++)
        {
            var r = RedLines[i];
            var key = Math.Round(r.BeatLength * 1000) / 1000;
            double duration = 0;
            if (r.Time <= lastObjectTime)
            {
                double start = i == 0 ? 0 : r.Time;
                double next = i == RedLines.Count - 1 ? lastObjectTime : RedLines[i + 1].Time;
                duration = next - start;
            }
            if (!durations.ContainsKey(key)) order.Add(key);
            durations[key] = durations.GetValueOrDefault(key) + duration;
        }
        // ties go to the beat length that appears first, like a stable OrderByDescending
        double best = order[0], bestDur = durations[order[0]];
        foreach (var k in order.Skip(1))
        {
            if (durations[k] > bestDur) { best = k; bestDur = durations[k]; }
        }
        return best > 0 ? best : RedLines[0].BeatLength;
    }

    /// <summary>End time of the last object (hold/spinner ends included), or the last timing point when there are none.</summary>
    public double LastObjectTime(IReadOnlyList<HitObject> objects)
    {
        if (objects.Count > 0) return objects.Max(o => Math.Max(o.Time, o.EndTime));
        return All.Count > 0 ? All[^1].Time : 0;
    }
}
