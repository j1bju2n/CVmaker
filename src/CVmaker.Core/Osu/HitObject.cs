using System.Globalization;

namespace CVmaker.Core.Osu;

public enum HitObjectKind { Circle, Slider, Spinner, Hold }

/// <summary>
/// One [HitObjects] line. Only the fields the cut needs (start/end time) are typed; the rest of the
/// line is carried verbatim so hitsounds, slider paths etc. survive untouched.
/// </summary>
public sealed class HitObject
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Time { get; set; }
    public int Type { get; set; }
    public int HitSound { get; set; }

    /// <summary>Fields after hitSound, unparsed (index 5..).</summary>
    public List<string> Extras { get; } = new();

    public HitObjectKind Kind =>
        (Type & 128) != 0 ? HitObjectKind.Hold :
        (Type & 8) != 0 ? HitObjectKind.Spinner :
        (Type & 2) != 0 ? HitObjectKind.Slider : HitObjectKind.Circle;

    public bool IsNewCombo => (Type & 4) != 0;

    /// <summary>Column for mania (0-based).</summary>
    public int Column(int keyCount) =>
        Math.Clamp((int)Math.Floor(X * keyCount / 512.0), 0, Math.Max(0, keyCount - 1));

    /// <summary>
    /// End time for hold/spinner (start time for circles). Sliders need timing information,
    /// see <see cref="TryGetSliderParams"/>.
    /// </summary>
    public int EndTime
    {
        get
        {
            switch (Kind)
            {
                case HitObjectKind.Hold:
                    if (Extras.Count > 0)
                    {
                        var s = Extras[0];
                        var idx = s.IndexOf(':');
                        var num = idx >= 0 ? s[..idx] : s;
                        if (int.TryParse(num.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var e)) return e;
                        if (double.TryParse(num.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return (int)Math.Round(d);
                    }
                    return Time;
                case HitObjectKind.Spinner:
                    if (Extras.Count > 0 && double.TryParse(Extras[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var se)) return (int)Math.Round(se);
                    return Time;
                default:
                    return Time;
            }
        }
        set
        {
            switch (Kind)
            {
                case HitObjectKind.Hold:
                {
                    var rest = "";
                    if (Extras.Count > 0)
                    {
                        var idx = Extras[0].IndexOf(':');
                        rest = idx >= 0 ? Extras[0][idx..] : "";
                    }
                    else Extras.Add("");
                    Extras[0] = value.ToString(CultureInfo.InvariantCulture) + rest;
                    break;
                }
                case HitObjectKind.Spinner:
                    if (Extras.Count == 0) Extras.Add("");
                    Extras[0] = value.ToString(CultureInfo.InvariantCulture);
                    break;
                default:
                    Time = value;
                    break;
            }
        }
    }

    /// <summary>Slider repeat count (slides) and pixel length, when this is a slider.</summary>
    public bool TryGetSliderParams(out int slides, out double pixelLength)
    {
        slides = 1; pixelLength = 0;
        if (Kind != HitObjectKind.Slider || Extras.Count < 3) return false;
        slides = OsuFile.ParseInt(Extras[1], 1);
        pixelLength = OsuFile.ParseDouble(Extras[2], 0);
        return true;
    }

    public HitObject Clone()
    {
        var c = new HitObject { X = X, Y = Y, Time = Time, Type = Type, HitSound = HitSound };
        c.Extras.AddRange(Extras);
        return c;
    }

    public static bool TryParse(string line, out HitObject ho)
    {
        ho = new HitObject();
        var t = line.Trim();
        if (t.Length == 0 || t.StartsWith("//")) return false;
        var p = t.Split(',');
        if (p.Length < 4) return false;
        ho.X = OsuFile.ParseInt(p[0], 0);
        ho.Y = OsuFile.ParseInt(p[1], 0);
        if (!double.TryParse(p[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var time)) return false;
        ho.Time = (int)Math.Round(time);
        ho.Type = OsuFile.ParseInt(p[3], 1);
        ho.HitSound = p.Length > 4 ? OsuFile.ParseInt(p[4], 0) : 0;
        for (int i = 5; i < p.Length; i++) ho.Extras.Add(p[i]);
        return true;
    }

    public string Serialize()
    {
        var head = string.Join(",", X.ToString(CultureInfo.InvariantCulture), Y.ToString(CultureInfo.InvariantCulture),
            Time.ToString(CultureInfo.InvariantCulture), Type.ToString(CultureInfo.InvariantCulture), HitSound.ToString(CultureInfo.InvariantCulture));
        return Extras.Count == 0 ? head : head + "," + string.Join(",", Extras);
    }

    public override string ToString() => Serialize();
}
