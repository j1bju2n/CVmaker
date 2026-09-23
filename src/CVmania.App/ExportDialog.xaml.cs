using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CVmania.App.Controls;
using CVmania.App.Localization;
using CVmania.App.Services;
using CVmania.Core.Audio;
using CVmania.Core.Cut;
using CVmania.Core.Export;
using CVmania.Core.Osu;
using Microsoft.Win32;

namespace CVmania.App;

public sealed class ExportContext
{
    public required OsuFile Beatmap { get; init; }
    public required string BeatmapPath { get; init; }
    public required PcmAudio Audio { get; init; }
    public string? AudioPath { get; init; }
    public required List<CutRegion> Regions { get; init; }
    public string? SongsFolder { get; init; }
    public required UserSettings Settings { get; init; }
}

public partial class ExportDialog : Window
{
    private readonly ExportContext _ctx;
    private readonly List<CutRegion> _regions; // clipped to the audio: the .osu and the audio are built from the same list
    private string? _lastExportDir;
    private bool _busy;

    public ExportDialog(ExportContext ctx)
    {
        _ctx = ctx;
        _regions = CutRegion.Normalize(ctx.Regions, ctx.Audio.DurationMs);
        InitializeComponent();
        var s = ctx.Settings;
        FormatCombo.SelectedIndex = Math.Clamp(s.FormatIndex, 0, 4);
        CrossfadeBox.Text = s.CrossfadeMs.ToString(CultureInfo.InvariantCulture);
        TailBox.Text = s.TailSilenceMs.ToString(CultureInfo.InvariantCulture);
        TitleSuffixBox.Text = s.TitleSuffix;
        CreatorBox.Text = ctx.Beatmap.Creator; // the cut is usually made by someone else: start from the original name
        AudioNameBox.Text = s.AudioName;
        CopyBgCheck.IsChecked = s.CopyBackground;
        VerifyCheck.IsChecked = s.VerifyOffset;
        KeepBookmarksCheck.IsChecked = s.KeepBookmarks;
        if (s.NormalizeScroll) ScrollNormalizeRadio.IsChecked = true; else ScrollKeepRadio.IsChecked = true;
        switch (s.BeatmapContent)
        {
            case 1: EmptyNotesRadio.IsChecked = true; break;
            case 2: NoOsuRadio.IsChecked = true; break;
            default: KeepNotesRadio.IsChecked = true; break;
        }
        switch (s.OutputMode)
        {
            case 1: OutSameRadio.IsChecked = true; break;
            case 2: OutCustomRadio.IsChecked = true; break;
            default: OutSongsRadio.IsChecked = true; break;
        }
        var map = new TimeMap(_regions);
        SummaryText.Text = Loc.F("Export.Summary", map.Regions.Count, WaveformView.FormatTime(map.OutputLengthMs));
        UpdateOutputPath();
        UpdateScrollInfo();
    }

    private void Content_Changed(object sender, RoutedEventArgs e) => UpdateScrollInfo();

    /// <summary>
    /// Dry run of the planner to show what the scroll-speed normalization would do: the most common BPM of the
    /// original and of the cut (what osu!mania scrolls relative to), and the SV factor that keeps the cut at 1x.
    /// </summary>
    private void UpdateScrollInfo()
    {
        if (ScrollInfoText == null || ScrollNormalizeRadio == null) return;
        bool noNotes = EmptyNotesRadio.IsChecked == true;
        bool enabled = NoOsuRadio.IsChecked != true;
        ScrollNormalizeRadio.IsEnabled = ScrollKeepRadio.IsEnabled = KeepBookmarksCheck.IsEnabled = enabled;
        ScrollInfo? info = null;
        try
        {
            var opt = new CutOptions { KeepHitObjects = !noNotes, NormalizeScrollSpeed = true };
            info = CutPlanner.Transform(_ctx.Beatmap, _regions, opt, "audio").Scroll;
        }
        catch { /* shown when the export itself fails */ }
        if (info == null)
        {
            ScrollNormalizeRadio.Content = Loc.T("Export.Scroll.Normalize");
            ScrollInfoText.Text = "";
            return;
        }
        string bpm = WaveformView.FormatBpm(info.OutputBaseBpm);
        ScrollNormalizeRadio.Content = info.IsIdentity
            ? Loc.F("Export.Scroll.NormalizeSame", bpm)
            : Loc.F("Export.Scroll.Normalize", bpm, info.Factor.ToString("0.###", CultureInfo.InvariantCulture));
        ScrollInfoText.Text = Loc.F("Export.Scroll.Info", WaveformView.FormatBpm(info.SourceBaseBpm), bpm,
                                  info.BaseSv.ToString("0.###", CultureInfo.InvariantCulture))
                              + (noNotes ? Loc.T("Export.Scroll.Info.NoNotes") : "");
    }

    private int OutputMode => OutSameRadio.IsChecked == true ? 1 : OutCustomRadio.IsChecked == true ? 2 : 0;

    private string? ComputeOutputDirectory()
    {
        var mapDir = Path.GetDirectoryName(_ctx.BeatmapPath)!;
        switch (OutputMode)
        {
            case 1: return mapDir;
            case 2: return string.IsNullOrWhiteSpace(_ctx.Settings.CustomOutputDir) ? null : _ctx.Settings.CustomOutputDir;
            default:
            {
                var songs = _ctx.SongsFolder ?? Path.GetDirectoryName(mapDir)!;
                var suffix = string.IsNullOrEmpty(TitleSuffixBox.Text) ? " (Cut Ver.)" : TitleSuffixBox.Text;
                var name = CutExporter.SanitizeFileName($"{_ctx.Beatmap.Artist} - {_ctx.Beatmap.Title}{suffix}".Trim());
                return Path.Combine(songs, name);
            }
        }
    }

    private void UpdateOutputPath()
    {
        if (OutputPathBox == null) return;
        OutputPathBox.Text = ComputeOutputDirectory() ?? Loc.T("Export.ChooseFolder");
    }

    private void OutputMode_Changed(object sender, RoutedEventArgs e) => UpdateOutputPath();

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("Export.ChooseTitle") };
        if (!string.IsNullOrWhiteSpace(_ctx.Settings.CustomOutputDir) && Directory.Exists(_ctx.Settings.CustomOutputDir))
            dlg.InitialDirectory = _ctx.Settings.CustomOutputDir;
        if (dlg.ShowDialog(this) == true)
        {
            _ctx.Settings.CustomOutputDir = dlg.FolderName;
            OutCustomRadio.IsChecked = true;
            UpdateOutputPath();
        }
    }

    private static int ParseIntBox(TextBox box, int def)
    {
        if (int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0) return v;
        box.Text = def.ToString(CultureInfo.InvariantCulture);
        return def;
    }

    private void SaveSettings()
    {
        var s = _ctx.Settings;
        s.FormatIndex = FormatCombo.SelectedIndex;
        s.CrossfadeMs = ParseIntBox(CrossfadeBox, 10);
        s.TailSilenceMs = ParseIntBox(TailBox, 0);
        s.TitleSuffix = TitleSuffixBox.Text;
        s.AudioName = string.IsNullOrWhiteSpace(AudioNameBox.Text) ? "audio" : AudioNameBox.Text.Trim();
        s.CopyBackground = CopyBgCheck.IsChecked == true;
        s.VerifyOffset = VerifyCheck.IsChecked == true;
        s.BeatmapContent = EmptyNotesRadio.IsChecked == true ? 1 : NoOsuRadio.IsChecked == true ? 2 : 0;
        s.OutputMode = OutputMode;
        s.NormalizeScroll = ScrollNormalizeRadio.IsChecked == true;
        s.KeepBookmarks = KeepBookmarksCheck.IsChecked == true;
        s.Save();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SaveSettings();
        var outDir = ComputeOutputDirectory();
        if (string.IsNullOrEmpty(outDir)) { ResultText.Text = Loc.T("Export.ChooseFolder"); return; }

        var (format, kbps, oggQ) = FormatCombo.SelectedIndex switch
        {
            1 => (AudioFormat.Mp3, 320, 0.6f),
            2 => (AudioFormat.Ogg, 192, 0.6f),
            3 => (AudioFormat.Ogg, 192, 0.8f),
            4 => (AudioFormat.Wav, 192, 0.6f),
            _ => (AudioFormat.Mp3, 192, 0.6f),
        };
        var audioBase = CutExporter.SanitizeFileName(_ctx.Settings.AudioName);
        var ext = CutExporter.ExtensionFor(format);
        // never overwrite the source audio when exporting into the beatmap's own folder
        if (_ctx.AudioPath != null && string.Equals(Path.GetFullPath(Path.Combine(outDir, audioBase + ext)), Path.GetFullPath(_ctx.AudioPath), StringComparison.OrdinalIgnoreCase))
        {
            audioBase += "-cut";
            AudioNameBox.Text = audioBase;
        }

        var options = new ExportOptions
        {
            Format = format,
            Mp3BitrateKbps = kbps,
            OggQuality = oggQ,
            AudioBaseName = audioBase,
            OutputDirectory = outDir,
            WriteBeatmap = NoOsuRadio.IsChecked != true,
            CopyBackground = CopyBgCheck.IsChecked == true,
            VerifyOffset = VerifyCheck.IsChecked == true,
            Cut = new CutOptions
            {
                CrossfadeMs = _ctx.Settings.CrossfadeMs,
                TailSilenceMs = _ctx.Settings.TailSilenceMs,
                TitleSuffix = TitleSuffixBox.Text,
                AppendTitleSuffix = !string.IsNullOrEmpty(TitleSuffixBox.Text),
                VersionOverride = string.IsNullOrWhiteSpace(VersionBox.Text) ? null : VersionBox.Text.Trim(),
                CreatorOverride = string.IsNullOrWhiteSpace(CreatorBox.Text) ? null : CreatorBox.Text.Trim(),
                KeepHitObjects = EmptyNotesRadio.IsChecked != true,
                NormalizeScrollSpeed = ScrollNormalizeRadio.IsChecked == true,
                KeepBookmarks = KeepBookmarksCheck.IsChecked == true,
            },
        };

        var beatmap = _ctx.Beatmap;
        var audio = _ctx.Audio;
        var regions = _regions;

        // keeping the notes: warn about notes the cut made unplayable before writing anything
        if (options.WriteBeatmap && options.Cut.KeepHitObjects)
        {
            List<NoteIssue> issues;
            try { issues = CutPlanner.Transform(beatmap, regions, options.Cut, audioBase + ext).Issues; }
            catch (Exception ex) { ResultText.Text = Loc.F("Export.Failed", ex.Message); return; }
            if (issues.Count > 0 && !NoteIssuesDialog.Show(this, issues)) return;
        }

        var progress = new Progress<string>(s => ResultText.Text = s);
        SetBusy(true);
        ResultText.Text = Loc.T("Export.Working");
        ExportReport report;
        try
        {
            report = await Task.Run(() => CutExporter.Export(beatmap, audio, regions, options, progress));
        }
        catch (Exception ex)
        {
            ResultText.Text = Loc.F("Export.Failed", ex.Message);
            SetBusy(false);
            return;
        }
        SetBusy(false);
        _lastExportDir = outDir;
        OpenFolderButton.IsEnabled = true;
        // osu! will show this file next; selecting it there must not replace the workspace of the original
        if (report.BeatmapPath != null)
        {
            var sourceDir = Path.GetFullPath(Path.GetDirectoryName(_ctx.BeatmapPath)!);
            bool ownFolder = !string.Equals(Path.GetFullPath(outDir).TrimEnd('\\'), sourceDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            _ctx.Settings.AddRecentExport(report.BeatmapPath, ownFolder ? outDir : null);
        }

        var lines = new List<string>
        {
            Loc.F("Export.Done", Path.GetFileName(report.AudioPath), WaveformView.FormatTime(report.OutputLengthMs),
                report.BeatmapPath != null ? Loc.F("Export.AndOsu", Path.GetFileName(report.BeatmapPath)) : "", outDir),
        };
        if (report.Offset != null) lines.Add(Loc.F("Export.Offset", report.Offset.Message));
        if (report.Beatmap != null)
            lines.Add(Loc.F("Export.Stats", report.Beatmap.KeptObjects, report.Beatmap.DroppedObjects, report.Beatmap.ClampedObjects, report.Beatmap.InsertedRedLines));
        if (report.Beatmap?.Scroll is { } sc)
        {
            var bpm = WaveformView.FormatBpm(sc.OutputBaseBpm);
            if (sc.Applied)
                lines.Add(Loc.F("Export.ScrollDone", bpm, sc.Factor.ToString("0.###", CultureInfo.InvariantCulture), sc.InsertedGreenLines, sc.ScaledGreenLines));
            else if (!sc.IsIdentity)
                lines.Add(Loc.F("Export.ScrollKept", WaveformView.FormatBpm(sc.SourceBaseBpm), bpm, sc.BaseSv.ToString("0.###", CultureInfo.InvariantCulture)));
        }
        lines.AddRange(report.Warnings.Where(w => !w.StartsWith("Offset check")));
        if (OutputMode == 0) lines.Add(Loc.T("Export.F5"));
        ResultText.Text = string.Join("\n", lines);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ExportButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastExportDir == null || !Directory.Exists(_lastExportDir)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastExportDir}\"") { UseShellExecute = true });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SaveSettings();
        Close();
    }
}
