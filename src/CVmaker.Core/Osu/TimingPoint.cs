using System.Globalization;

namespace CVmaker.Core.Osu;

/// <summary>
/// One [TimingPoints] line: time,beatLength,meter,sampleSet,sampleIndex,volume,uninherited,effects.
/// Old files may have fewer fields; we read leniently and always write all eight.
/// </summary>
public sealed class TimingPoint
{
    public double Time { get; set; }

    /// <summary>Raw beatLength text is preserved so that BPM precision never drifts.</summary>
    public string BeatLengthRaw { get; set; } = "500";

    public double BeatLength => OsuFile.ParseDouble(BeatLengthRaw, 500);
    public int Meter { get; set; } = 4;
    public int SampleSet { get; set; }
    public int SampleIndex { get; set; }
    public int Volume { get; set; } = 100;
    public bool Uninherited { get; set; } = true;
    public int Effects { get; set; }

    public bool Kiai => (Effects & 1) != 0;
    public bool IsRedLine => Uninherited;
    public double Bpm => Uninherited && BeatLength > 0 ? 60000.0 / BeatLength : 0;

    /// <summary>Scroll/slider velocity multiplier for inherited points (-100 => 1.0).</summary>
    public double SvMultiplier => !Uninherited && BeatLength < 0 ? -100.0 / BeatLength : 1.0;

    public TimingPoint Clone() => (TimingPoint)MemberwiseClone();

    public static bool TryParse(string line, out TimingPoint tp)
    {
        tp = new TimingPoint();
        var t = line.Trim();
        if (t.Length == 0 || t.StartsWith("//")) return false;
        var p = t.Split(',');
        if (p.Length < 2) return false;
        if (!double.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var time)) return false;
        tp.Time = time;
        tp.BeatLengthRaw = p[1].Trim();
        if (!double.TryParse(tp.BeatLengthRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var bl)) return false;
        tp.Meter = p.Length > 2 ? OsuFile.ParseInt(p[2], 4) : 4;
        tp.SampleSet = p.Length > 3 ? OsuFile.ParseInt(p[3], 0) : 0;
        tp.SampleIndex = p.Length > 4 ? OsuFile.ParseInt(p[4], 0) : 0;
        tp.Volume = p.Length > 5 ? OsuFile.ParseInt(p[5], 100) : 100;
        // legacy: without the flag, a positive beatLength means uninherited
        tp.Uninherited = p.Length > 6 ? OsuFile.ParseInt(p[6], 1) != 0 : bl > 0;
        tp.Effects = p.Length > 7 ? OsuFile.ParseInt(p[7], 0) : 0;
        if (tp.Meter <= 0) tp.Meter = 4;
        return true;
    }

    public string Serialize() =>
        string.Join(",", FormatTime(Time), BeatLengthRaw, Meter.ToString(CultureInfo.InvariantCulture),
            SampleSet.ToString(CultureInfo.InvariantCulture), SampleIndex.ToString(CultureInfo.InvariantCulture),
            Volume.ToString(CultureInfo.InvariantCulture), Uninherited ? "1" : "0", Effects.ToString(CultureInfo.InvariantCulture));

    public static string FormatTime(double v)
    {
        var r = Math.Round(v);
        if (Math.Abs(v - r) < 1e-6) return ((long)r).ToString(CultureInfo.InvariantCulture);
        return v.ToString("0.############", CultureInfo.InvariantCulture);
    }

    public override string ToString() => Serialize();
}
