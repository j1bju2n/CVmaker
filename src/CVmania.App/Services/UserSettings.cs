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
