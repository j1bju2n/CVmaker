using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CVmania.Core.Osu;

namespace CVmania.Core.Cut;

public sealed class SessionRegion
{
    public int Start { get; set; }
    public int End { get; set; }
    public int FadeIn { get; set; }
    public int FadeOut { get; set; }
    public int FadeInMode { get; set; }
    public int FadeOutMode { get; set; }
}

/// <summary>
/// The editable state of a cut for one song: the regions with their edge settings, plus the view.
/// The app writes it automatically after every change (one file per song, keyed by folder + audio
/// file, so all difficulties of a set share it) and restores it when the song is loaded again, so
/// testing the exported cut in osu! can never lose the work. The same format is used for explicit
/// "Save work..." / "Load work..." files (<c>*.cvmania.json</c>).
/// </summary>
public sealed class WorkSession
{
    public const string Extension = ".cvmania.json";

    public int Format { get; set; } = 1;
    public string Generator { get; set; } = "CV!mania";
    public string BeatmapPath { get; set; } = "";
    public string AudioFilename { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public List<SessionRegion> Regions { get; set; } = new();
    public double ViewStartMs { get; set; }
    public double ViewLengthMs { get; set; }

    /// <summary>One song = its folder + audio file (case-insensitive), so switching difficulties keeps the same work.</summary>
    public static string SongKey(string beatmapPath, string audioFilename)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(beatmapPath)) ?? "";
        var text = (folder.TrimEnd('\\', '/') + "|" + audioFilename).ToLowerInvariant();
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..20].ToLowerInvariant();
    }

    public static string AutoPathFor(string sessionsDirectory, string beatmapPath, string audioFilename) =>
        Path.Combine(sessionsDirectory, SongKey(beatmapPath, audioFilename) + ".json");

    public static WorkSession From(OsuFile beatmap, string beatmapPath, IEnumerable<CutRegion> regions, double viewStartMs, double viewLengthMs) => new()
    {
        BeatmapPath = beatmapPath,
        AudioFilename = beatmap.AudioFilename,
        Title = $"{beatmap.Artist} - {beatmap.Title} [{beatmap.Version}]",
        SavedAt = DateTime.Now,
        Regions = regions.Select(r => new SessionRegion
        {
            Start = r.StartMs, End = r.EndMs, FadeIn = r.FadeInMs, FadeOut = r.FadeOutMs,
            FadeInMode = (int)r.FadeInMode, FadeOutMode = (int)r.FadeOutMode,
        }).ToList(),
        ViewStartMs = viewStartMs,
        ViewLengthMs = viewLengthMs,
    };

    public IEnumerable<CutRegion> ToRegions() => Regions.Select(r =>
        new CutRegion(r.Start, r.End, r.FadeIn, r.FadeOut, (EdgeMode)r.FadeInMode, (EdgeMode)r.FadeOutMode));

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Writes atomically (temp file + move) so a crash mid-write cannot destroy the previous save.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, true);
    }

    public static WorkSession? Load(string path) => JsonSerializer.Deserialize<WorkSession>(File.ReadAllText(path));

    public static WorkSession? LoadAuto(string sessionsDirectory, string beatmapPath, string audioFilename)
    {
        try
        {
            var p = AutoPathFor(sessionsDirectory, beatmapPath, audioFilename);
            return File.Exists(p) ? Load(p) : null;
        }
        catch { return null; }
    }
}
