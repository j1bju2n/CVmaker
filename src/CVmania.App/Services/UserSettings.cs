using System.IO;
using System.Text.Json;
using CVmania.App.Localization;

namespace CVmania.App.Services;

/// <summary>Persisted UI preferences (%LOCALAPPDATA%\CVmania\settings.json).</summary>
public sealed class UserSettings
{
    public string Language { get; set; } = Loc.DefaultLanguage();
    public int FormatIndex { get; set; } = 0;
    public int OutputMode { get; set; } = 0;
    public string CustomOutputDir { get; set; } = "";
    public string TitleSuffix { get; set; } = " (Cut Ver.)";
    public int CrossfadeMs { get; set; } = 10;
    public int TailSilenceMs { get; set; } = 0;
    public bool CopyBackground { get; set; } = true;
    public bool VerifyOffset { get; set; } = true;
    /// <summary>0 = keep notes, 1 = empty difficulty, 2 = audio only.</summary>
    public int BeatmapContent { get; set; } = 0;
    public string AudioName { get; set; } = "audio";
    public double AmplitudeScale { get; set; } = 0.6;
    public int SnapDivisor { get; set; } = 1;
    public bool FollowPlayhead { get; set; } = true;
    public bool FollowOsu { get; set; } = true;
    public bool ShowNotes { get; set; } = true;
    public bool ShowBookmarks { get; set; } = true;
    /// <summary>Export: scale SVs so the cut scrolls at 1x in its most common BPM (osu!mania).</summary>
    public bool NormalizeScroll { get; set; } = true;
    /// <summary>Export: keep the editor bookmarks that fall inside the kept regions.</summary>
    public bool KeepBookmarks { get; set; } = true;
    public double Volume { get; set; } = 0.8;
    /// <summary>.osu files this app wrote (newest first). Selecting one of them in osu! must not replace the workspace.</summary>
    public List<string> RecentExports { get; set; } = new();
    /// <summary>Folders created for exports (never the source beatmap's own folder): every difficulty in them is a cut version, even after osu! renamed the file.</summary>
    public List<string> RecentExportDirs { get; set; } = new();

    public void AddRecentExport(string osuPath, string? exclusiveDir)
    {
        Push(RecentExports, Norm(osuPath));
        if (exclusiveDir != null) Push(RecentExportDirs, Norm(exclusiveDir));
        Save();
    }

    public bool IsRecentExport(string path)
    {
        var full = Norm(path);
        if (RecentExports.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase))) return true;
        var dir = Path.GetDirectoryName(full) ?? "";
        return RecentExportDirs.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
    }

    private static void Push(List<string> list, string value)
    {
        list.RemoveAll(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, value);
        if (list.Count > 50) list.RemoveRange(50, list.Count - 50);
    }

    private static string Norm(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return p; }
    }

    public static string FilePath => Path.Combine(App.LogDirectory, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath));
                if (s != null) return s;
            }
        }
        catch { /* corrupt settings: start fresh */ }
        return new UserSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(App.LogDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not fatal */ }
    }
}
