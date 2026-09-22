using System.Globalization;
using System.Text;

namespace CVmaker.Core.Osu;

/// <summary>
/// Line-preserving .osu file model. Every section keeps its raw lines so that a file can be
/// round-tripped without touching anything we do not understand. Typed accessors are provided
/// for the sections the cut process needs to rewrite.
/// </summary>
public sealed class OsuFile
{
    public string FormatLine { get; set; } = "osu file format v14";

    /// <summary>Sections in file order. Names without brackets (e.g. "General").</summary>
    public List<OsuSection> Sections { get; } = new();

    public string? SourcePath { get; set; }

    public OsuSection? GetSection(string name) =>
        Sections.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public OsuSection GetOrAddSection(string name)
    {
        var s = GetSection(name);
        if (s != null) return s;
        s = new OsuSection(name);
        Sections.Add(s);
        return s;
    }

    // ---- typed convenience ----
    public string? Get(string section, string key) => GetSection(section)?.GetValue(key);
    public void Set(string section, string key, string value) => GetOrAddSection(section).SetValue(key, value);

    public string AudioFilename => Get("General", "AudioFilename") ?? "";
    public int PreviewTime => ParseInt(Get("General", "PreviewTime"), -1);
    public int Mode => ParseInt(Get("General", "Mode"), 0);
    public string Title => Get("Metadata", "Title") ?? "";
    public string TitleUnicode => Get("Metadata", "TitleUnicode") ?? Title;
    public string Artist => Get("Metadata", "Artist") ?? "";
    public string ArtistUnicode => Get("Metadata", "ArtistUnicode") ?? Artist;
    public string Creator => Get("Metadata", "Creator") ?? "";
    public string Version => Get("Metadata", "Version") ?? "";
    public double CircleSize => ParseDouble(Get("Difficulty", "CircleSize"), 4);
    public double SliderMultiplier => ParseDouble(Get("Difficulty", "SliderMultiplier"), 1.4);

    public List<TimingPoint> ReadTimingPoints()
    {
        var list = new List<TimingPoint>();
        var sec = GetSection("TimingPoints");
        if (sec == null) return list;
        foreach (var line in sec.Lines)
        {
            if (TimingPoint.TryParse(line, out var tp)) list.Add(tp);
        }
        return list;
    }

    public List<HitObject> ReadHitObjects()
    {
        var list = new List<HitObject>();
        var sec = GetSection("HitObjects");
        if (sec == null) return list;
        foreach (var line in sec.Lines)
        {
            if (HitObject.TryParse(line, out var ho)) list.Add(ho);
        }
        return list;
    }

    public void WriteTimingPoints(IEnumerable<TimingPoint> points)
    {
        var sec = GetOrAddSection("TimingPoints");
        sec.Lines.Clear();
        foreach (var p in points) sec.Lines.Add(p.Serialize());
    }

    public void WriteHitObjects(IEnumerable<HitObject> objects)
    {
        var sec = GetOrAddSection("HitObjects");
        sec.Lines.Clear();
        foreach (var o in objects) sec.Lines.Add(o.Serialize());
    }

    // ---- parsing / serialization ----
    public static OsuFile Load(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var f = Parse(text);
        f.SourcePath = path;
        return f;
    }

    public static OsuFile Parse(string text)
    {
        var file = new OsuFile();
        OsuSection? current = null;
        bool sawFormat = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (!sawFormat)
            {
                var t = line.Trim().TrimStart('﻿');
                if (t.Length == 0) continue;
                if (t.StartsWith("osu file format", StringComparison.OrdinalIgnoreCase))
                {
                    file.FormatLine = t;
                    sawFormat = true;
                    continue;
                }
                // tolerate files without the header
                sawFormat = true;
            }
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                current = new OsuSection(trimmed[1..^1]);
                file.Sections.Add(current);
                continue;
            }
            if (current == null)
            {
                if (trimmed.Length == 0) continue;
                current = new OsuSection("");
                file.Sections.Add(current);
            }
            current.Lines.Add(line);
        }
        // strip trailing blank lines of each section (they are re-added on save)
        foreach (var s in file.Sections)
        {
            while (s.Lines.Count > 0 && s.Lines[^1].Trim().Length == 0) s.Lines.RemoveAt(s.Lines.Count - 1);
        }
        return file;
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append(FormatLine).Append("\r\n");
        foreach (var s in Sections)
        {
            sb.Append("\r\n");
            if (s.Name.Length > 0) sb.Append('[').Append(s.Name).Append("]\r\n");
            foreach (var l in s.Lines) sb.Append(l).Append("\r\n");
        }
        return sb.ToString();
    }

    public void Save(string path)
    {
        // osu! writes UTF-8 without BOM
        File.WriteAllText(path, Serialize(), new UTF8Encoding(false));
    }

    internal static int ParseInt(string? s, int def) =>
        int.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v :
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : def;

    internal static double ParseDouble(string? s, double def) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
}

public sealed class OsuSection
{
    public string Name { get; }
    public List<string> Lines { get; } = new();

    public OsuSection(string name) { Name = name; }

    /// <summary>Key/value lookup for "Key: Value" style sections.</summary>
    public string? GetValue(string key)
    {
        foreach (var line in Lines)
        {
            if (TrySplitKeyValue(line, out var k, out _, out var v) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return v;
        }
        return null;
    }

    public void SetValue(string key, string value)
    {
        for (int i = 0; i < Lines.Count; i++)
        {
            if (TrySplitKeyValue(Lines[i], out var k, out var sep, out _) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                Lines[i] = k + sep + value;
                return;
            }
        }
        // new key: mimic the separator style used by the section (Metadata uses "Key:Value")
        var style = Lines.Select(l => TrySplitKeyValue(l, out _, out var s, out _) ? s : null).FirstOrDefault(s => s != null) ?? ": ";
        Lines.Add(key + style + value);
    }

    public void RemoveKey(string key)
    {
        Lines.RemoveAll(l => TrySplitKeyValue(l, out var k, out _, out _) && string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TrySplitKeyValue(string line, out string key, out string separator, out string value)
    {
        key = separator = value = "";
        if (line.StartsWith("//")) return false;
        var idx = line.IndexOf(':');
        if (idx <= 0) return false;
        key = line[..idx].Trim();
        // keep everything between ':' and the first non-space char as the separator
        int j = idx + 1;
        while (j < line.Length && line[j] == ' ') j++;
        separator = line[idx..j];
        value = line[j..];
        return true;
    }
}
