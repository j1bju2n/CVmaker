using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CVmania.App.Controls;
using CVmania.App.Localization;
using CVmania.App.Services;
using CVmania.Core.Audio;
using CVmania.Core.Cut;
using CVmania.Core.Osu;
using CVmania.Core.Timing;
using Microsoft.Win32;
using EdgeMode = CVmania.Core.Cut.EdgeMode;

namespace CVmania.App;

public sealed class RegionRow
{
    public int Index { get; init; }
    public CutRegion Region { get; init; }
    public string StartText => WaveformView.FormatTime(Region.StartMs);
    public string EndText => WaveformView.FormatTime(Region.EndMs);
    public string LengthText => WaveformView.FormatTime(Region.LengthMs);
    public string FadeInText => Region.FadeInMs > 0 ? WaveformView.EdgeLabel(Region.FadeInMs, Region.FadeInMode, "in") : "-";
    public string FadeOutText => Region.FadeOutMs > 0 ? WaveformView.EdgeLabel(Region.FadeOutMs, Region.FadeOutMode, "out") : "-";
    public string OutStartText { get; init; } = "";
    public string GridText { get; init; } = "";
}

public sealed class BpmChip
{
    public string Label { get; init; } = "";
    public double StartMs { get; init; }
    public string Tooltip { get; init; } = "";
}

public partial class MainWindow : Window
{
    private static readonly int[] SnapDivisors = { 0, 1, 2, 3, 4, 6, 8, 12, 16, -1 };

    private readonly UserSettings _settings = App.Settings;
    private readonly OsuWatcher _watcher = new();
    private readonly BassPlayer _player;
    private readonly DispatcherTimer _uiTimer;
    private readonly ObservableCollection<RegionRow> _regions = new();
    private readonly List<CutRegion> _regionList = new();

    private OsuFile? _beatmap;
    private TimingModel? _timing;
    private PcmAudio? _audio;
    private string? _audioPath;
    private string? _beatmapPath;
    private string? _songsFolder;
    private int _loadToken;

    private double? _selStart, _selEnd;

    private bool _previewMode;
    private TimeMap? _previewMap;
    private double _previewDurationMs;

    private int _fadeRegion = -1;
    private bool _fadeIsStart;
    private bool _fadeUpdating;
    private bool _busy;
    private bool _syncingChecks;

    private string? _screenshotDir;
    private List<CutRegion>? _startupRegions;
    private string? _startupOutDir;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            _player = new BassPlayer();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Audio engine (BASS) could not be initialised:\n" + ex.Message, App.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
        _player.PlaybackEnded += () => Dispatcher.BeginInvoke(new Action(UpdatePlayhead));

        Overview.IsOverview = true;
        Overview.ViewportMoveRequested += start => Detail.SetView(start, Detail.ViewLengthMs);
        Overview.AmplitudeScaleChanged += s => { Detail.AmplitudeScale = s; SaveAmplitude(s); };
        Detail.AmplitudeScaleChanged += s => { Overview.AmplitudeScale = s; SaveAmplitude(s); };
        Detail.SeekRequested += OnSeekRequested;
        Detail.SelectionChanged += OnSelectionChanged;
        Detail.SelectionCleared += () => { _selStart = _selEnd = null; UpdateSelectionUi(); };
        Detail.RegionClicked += OnRegionClicked;
        Detail.FadeHandleClicked += (idx, isStart, pos) => OpenFadePopup(idx, isStart, pos);
        Detail.ViewChanged += (s, l) =>
        {
            Overview.ViewportStartMs = s;
            Overview.ViewportLengthMs = l;
            Overview.InvalidateVisual();
        };

        RegionsList.ItemsSource = _regions;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer.Tick += (_, _) => UpdatePlayhead();
        _uiTimer.Start();

        _watcher.Polled += snap => Dispatcher.BeginInvoke(new Action(() => UpdateOsuStatus(snap)));
        _watcher.BeatmapChanged += path => Dispatcher.BeginInvoke(new Action(() => OnOsuBeatmapChanged(path)));
        _watcher.Start();

        // settings
        Detail.AmplitudeScale = Overview.AmplitudeScale = _settings.AmplitudeScale;
        VolumeSlider.Value = Math.Clamp(_settings.Volume, 0, 1);
        FollowPlayheadCheck.IsChecked = FollowPlayheadMenu.IsChecked = _settings.FollowPlayhead;
        FollowOsuCheck.IsChecked = FollowOsuMenu.IsChecked = _settings.FollowOsu;
        ShowNotesMenu.IsChecked = _settings.ShowNotes;
        Detail.ShowNotes = _settings.ShowNotes;
        ShowBookmarksMenu.IsChecked = _settings.ShowBookmarks;
        Detail.ShowBookmarks = Overview.ShowBookmarks = _settings.ShowBookmarks;
        BuildLanguageMenu();
        BuildSnapStrip();
        Loc.LanguageChanged += OnLanguageChanged;
        UpdateRegionButtons();

        var wa = SystemParameters.WorkArea;
        if (Height > wa.Height - 20) Height = Math.Max(MinHeight, wa.Height - 20);
        if (Width > wa.Width - 20) Width = Math.Max(MinWidth, wa.Width - 20);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Loaded += (_, _) => HandleCommandLine();
    }

    // ============================================================== startup / dev flags
    private void HandleCommandLine()
    {
        // CVmania.exe <file.osu> [--out <folder>] [--regions a-b,c-d] [--screenshot <dir>]
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        string? Opt(string name)
        {
            var i = args.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
        }
        var outDir = Opt("--out");
        if (outDir != null)
        {
            _startupOutDir = Path.GetFullPath(outDir);
            _settings.CustomOutputDir = _startupOutDir;
            _settings.OutputMode = 2;
        }
        var regs = Opt("--regions");
        if (regs != null)
        {
            _startupRegions = regs.Split(',').Select(p =>
            {
                var q = p.Split('-');
                return new CutRegion(int.Parse(q[0], CultureInfo.InvariantCulture), int.Parse(q[1], CultureInfo.InvariantCulture));
            }).ToList();
        }
        _screenshotDir = Opt("--screenshot");
        var lang = Opt("--lang");
        if (lang != null)
        {
            Loc.Apply(lang);
            foreach (MenuItem mi in LanguageMenu.Items) mi.IsChecked = (string)mi.Tag == Loc.Language;
        }
        var osu = args.FirstOrDefault(a => a.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
        if (osu != null && File.Exists(osu))
        {
            FollowOsuCheck.IsChecked = false;
            LoadBeatmap(Path.GetFullPath(osu));
        }
    }

    private async Task RunScreenshotModeAsync()
    {
        if (_screenshotDir == null) return;
        Directory.CreateDirectory(_screenshotDir);
        await Task.Delay(1500);
        SaveVisual(this, Path.Combine(_screenshotDir, "main.png"));
        var dlg = new ExportDialog(BuildExportContext()!) { Owner = this };
        dlg.Show();
        await Task.Delay(800);
        SaveVisual(dlg, Path.Combine(_screenshotDir, "export.png"));
        dlg.Close();
        var sc = new ShortcutsWindow(this);
        sc.Show();
        await Task.Delay(500);
        SaveVisual(sc, Path.Combine(_screenshotDir, "shortcuts.png"));
        sc.Close();
        if (_regionList.Count > 0)
        {
            var msg = BuildCutPointMessage(_regionList[^1].StartMs + 137, _regionList[^1].EndMs + 250, true, true) ?? "(on grid)";
            var md = MessageDialog.CreateForScreenshot(this, Loc.T("Confirm.Title"), Loc.T("Confirm.Question"), msg);
            md.Show();
            await Task.Delay(500);
            SaveVisual(md, Path.Combine(_screenshotDir, "confirm.png"));
            md.Close();
        }
        var sampleIssues = new List<NoteIssue>
        {
            new(NoteIssueKind.Overlap, 12345, 45678, 3, 120),
            new(NoteIssueKind.ShortHold, 20000, 60000, 1, 40),
            new(NoteIssueKind.Duplicate, 30500, 70500, 2, 0),
        };
        var nd = new NoteIssuesDialog(this, sampleIssues);
        nd.ExpandDetails();
        nd.Show();
        await Task.Delay(500);
        SaveVisual(nd, Path.Combine(_screenshotDir, "issues.png"));
        nd.Close();
        await Task.Delay(200);
        Application.Current.Shutdown();
    }

    /// <summary>Dev helper: renders a window's content (with its background) to a PNG without touching the desktop.</summary>
    private static void SaveVisual(Window window, string path)
    {
        var fe = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(fe);
        double w = fe.ActualWidth + fe.Margin.Left + fe.Margin.Right;
        double h = fe.ActualHeight + fe.Margin.Top + fe.Margin.Bottom;
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(window.Background ?? Brushes.Black, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(fe) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(fe.Margin.Left, fe.Margin.Top, fe.ActualWidth, fe.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ============================================================== language / menus
    private void BuildLanguageMenu()
    {
        LanguageMenu.Items.Clear();
        foreach (var (code, name) in Loc.Languages)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = code == Loc.Language, Tag = code };
            item.Click += (s, _) =>
            {
                var c = (string)((MenuItem)s).Tag;
                Loc.Apply(c);
                _settings.Language = c;
                _settings.Save();
                foreach (MenuItem mi in LanguageMenu.Items) mi.IsChecked = (string)mi.Tag == c;
            };
            LanguageMenu.Items.Add(item);
        }
    }

    private void OnLanguageChanged()
    {
        BuildSnapStrip();
        RefreshRegions();
        UpdateOsuStatus(_watcher.Latest);
        UpdateSelectionUi();
        UpdatePlayhead();
        if (_beatmap == null) MapTitleText.Text = Loc.T("Map.None");
        else UpdateMapTexts();
        PreviewCutButton.Content = Loc.T(_previewMode ? "Bottom.BackToOriginal" : "Bottom.Preview");
        if (_previewMode) PreviewModeText.Text = Loc.T("Transport.Preview");
    }

    private void BuildSnapStrip()
    {
        SnapPanel.Children.Clear();
        var style = (Style)FindResource("SnapButton");
        foreach (var d in SnapDivisors)
        {
            var color = d > 0 ? WaveformView.DivisorColor(d) : Color.FromRgb(0xaa, 0xaa, 0xaa);
            var rb = new RadioButton
            {
                Style = style,
                GroupName = "snap",
                Tag = d,
                Content = d == 0 ? Loc.T("Snap.Measure") : d < 0 ? Loc.T("Snap.Off") : $"1/{d}",
                BorderBrush = new SolidColorBrush(color),
                Background = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B)),
                IsChecked = d == _settings.SnapDivisor,
            };
            rb.Checked += (s, _) =>
            {
                var div = (int)((RadioButton)s).Tag;
                _settings.SnapDivisor = div;
                _settings.Save();
                Detail.SnapDivisor = div;
                Detail.InvalidateVisual();
            };
            SnapPanel.Children.Add(rb);
        }
        Detail.SnapDivisor = _settings.SnapDivisor;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => new ShortcutsWindow(this).ShowDialog();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var ver = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        ImageSource? icon = null;
        try { icon = new BitmapImage(new Uri("pack://application:,,,/icon256.png")); } catch { /* no icon */ }
        MessageDialog.Info(this, Loc.T("Menu.About"), Loc.T("App.Title"), Loc.F("About.Text", ver), icon);
    }

    private void ShowNotesMenu_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowNotes = ShowNotesMenu.IsChecked;
        _settings.Save();
        Detail.ShowNotes = _settings.ShowNotes;
        Detail.InvalidateVisual();
    }

    private void ShowBookmarksMenu_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowBookmarks = ShowBookmarksMenu.IsChecked;
        _settings.Save();
        Detail.ShowBookmarks = Overview.ShowBookmarks = _settings.ShowBookmarks;
        Detail.InvalidateVisual();
        Overview.InvalidateVisual();
    }

    private void FollowPlayheadMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingChecks) return;
        FollowPlayheadCheck.IsChecked = FollowPlayheadMenu.IsChecked;
    }

    private void FollowPlayheadCheck_Changed(object sender, RoutedEventArgs e)
    {
        _syncingChecks = true;
        FollowPlayheadMenu.IsChecked = FollowPlayheadCheck.IsChecked == true;
        _syncingChecks = false;
        _settings.FollowPlayhead = FollowPlayheadCheck.IsChecked == true;
        _settings.Save();
    }

    private void FollowOsuMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingChecks) return;
        FollowOsuCheck.IsChecked = FollowOsuMenu.IsChecked;
    }

    private void FollowOsuCheck_Changed(object sender, RoutedEventArgs e)
    {
        _syncingChecks = true;
        FollowOsuMenu.IsChecked = FollowOsuCheck.IsChecked == true;
        _syncingChecks = false;
        _settings.FollowOsu = FollowOsuCheck.IsChecked == true;
        _settings.Save();
        if (FollowOsuCheck.IsChecked == true) OnOsuBeatmapChanged(_watcher.Latest.BeatmapPath);
    }

    private void SaveAmplitude(double s)
    {
        _settings.AmplitudeScale = s;
        _settings.Save();
    }

    // ============================================================== osu! link
    private void UpdateOsuStatus(OsuSnapshot snap)
    {
        _songsFolder = snap.SongsFolder ?? _songsFolder;
        if (!snap.ProcessFound)
        {
            OsuDot.Fill = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
            OsuStatusText.Text = Loc.T("Status.NotRunning");
        }
        else if (!snap.CanRead)
        {
            OsuDot.Fill = new SolidColorBrush(Color.FromRgb(0xe0, 0xb0, 0x20));
            OsuStatusText.Text = Loc.T("Status.Reading");
        }
        else
        {
            OsuDot.Fill = new SolidColorBrush(Color.FromRgb(0x3c, 0xd0, 0x70));
            OsuStatusText.Text = Loc.F("Status.Connected", snap.OsuVersion, snap.Status);
        }
    }

    private void OnOsuBeatmapChanged(string? path)
    {
        if (FollowOsuCheck.IsChecked != true || path == null) return;
        if (string.Equals(path, _beatmapPath, StringComparison.OrdinalIgnoreCase)) return;
        LoadBeatmap(path);
    }

    // ============================================================== loading
    private async void LoadBeatmap(string path)
    {
        var token = ++_loadToken;
        OsuFile file;
        try { file = OsuFile.Load(path); }
        catch (Exception ex)
        {
            StatusText.Text = Loc.F("Msg.ReadFail", Path.GetFileName(path), ex.Message);
            return;
        }

        var dir = Path.GetDirectoryName(path)!;
        var audioPath = ResolveAudio(dir, file.AudioFilename);

        _beatmap = file;
        _beatmapPath = path;
        _timing = new TimingModel(file.ReadTimingPoints());
        Detail.Timing = _timing;
        Overview.Timing = _timing;

        var objects = file.ReadHitObjects();
        int keys = file.Mode == 3 ? Math.Max(1, (int)Math.Round(file.CircleSize)) : 1;
        Detail.KeyCount = keys;
        Detail.Notes = file.Mode == 3
            ? objects.Select(o => (o.Time, o.Column(keys))).ToList()
            : objects.Select(o => (o.Time, 0)).ToList();
        Detail.Breaks = ReadBreaks(file);
        Overview.Breaks = Detail.Breaks;
        Detail.Bookmarks = Overview.Bookmarks = file.ReadBookmarks();
        UpdateMapTexts();
        BuildBpmChips();

        if (audioPath == null)
        {
            StatusText.Text = Loc.F("Msg.AudioMissing", file.AudioFilename);
            _audio = null; _audioPath = null;
            Detail.SetAudio(null); Overview.SetAudio(null);
            _player.Free();
            ClearRegions();
            RefreshRegions();
            return;
        }

        bool sameAudio = string.Equals(audioPath, _audioPath, StringComparison.OrdinalIgnoreCase) && _audio != null;
        if (sameAudio)
        {
            RefreshRegions();
            Detail.InvalidateVisual();
            Overview.InvalidateVisual();
            return;
        }

        ExitPreview(keepPosition: false);
        _player.Free();
        ClearRegions();
        _selStart = _selEnd = null;
        UpdateSelectionUi();
        LoadingText.Visibility = Visibility.Visible;
        StatusText.Text = Loc.T("Msg.Decoding");
        PcmAudio pcm;
        try
        {
            pcm = await Task.Run(() => BassDecoder.DecodeFile(audioPath));
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            LoadingText.Visibility = Visibility.Collapsed;
            StatusText.Text = Loc.F("Msg.DecodeFail", ex.Message);
            return;
        }
        if (token != _loadToken) return; // a newer beatmap was requested meanwhile

        _audio = pcm;
        _audioPath = audioPath;
        Detail.SetAudio(pcm);
        Overview.SetAudio(pcm);
        try
        {
            _player.LoadFile(audioPath);
            _player.Volume = (float)VolumeSlider.Value;
        }
        catch (Exception ex)
        {
            StatusText.Text = Loc.F("Msg.PlayFail", ex.Message);
        }
        LoadingText.Visibility = Visibility.Collapsed;
        PlayButton.IsEnabled = StopButton.IsEnabled = _player.HasStream;
        StatusText.Text = Loc.F("Msg.Loaded", Path.GetFileName(audioPath), pcm.SampleRate, pcm.Channels, WaveformView.FormatTime(pcm.DurationMs));
        if (_startupRegions != null)
        {
            _regionList.AddRange(_startupRegions);
            _startupRegions = null;
        }
        RefreshRegions();
        if (_regions.Count > 0 && _screenshotDir != null) RegionsList.SelectedIndex = 0;
        UpdatePlayhead();
        if (_screenshotDir != null) _ = RunScreenshotModeAsync();
    }

    private void UpdateMapTexts()
    {
        if (_beatmap == null || _timing == null) return;
        var file = _beatmap;
        var objects = file.ReadHitObjects();
        int keys = file.Mode == 3 ? Math.Max(1, (int)Math.Round(file.CircleSize)) : 1;
        MapTitleText.Text = $"{file.Artist} - {file.Title} [{file.Version}]  (by {file.Creator})";
        var mcb = _timing.HasTiming ? WaveformView.FormatBpm(_timing.MostCommonBpm(_timing.LastObjectTime(objects))) : "?";
        MapInfoText.Text = Loc.F("Map.Info", file.AudioFilename, file.Mode == 3 ? keys + "K" : "mode " + file.Mode, objects.Count,
            _timing.RedLines.Count, mcb, Path.GetFileName(_beatmapPath ?? ""));
    }

    private static string? ResolveAudio(string dir, string audioFilename)
    {
        if (string.IsNullOrWhiteSpace(audioFilename)) return null;
        var p = Path.Combine(dir, audioFilename);
        if (File.Exists(p)) return p;
        try
        {
            return Directory.GetFiles(dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), audioFilename, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    private static List<(double, double)> ReadBreaks(OsuFile file)
    {
        var list = new List<(double, double)>();
        var sec = file.GetSection("Events");
        if (sec == null) return list;
        foreach (var line in sec.Lines)
        {
            var p = line.Trim().Split(',');
            if (p.Length >= 3 && (p[0] == "2" || p[0].Equals("Break", StringComparison.OrdinalIgnoreCase)) &&
                double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
                double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b))
                list.Add((a, b));
        }
        return list;
    }

    private void BuildBpmChips()
    {
        var chips = new List<BpmChip>();
        if (_timing != null)
        {
            var dur = _audio?.DurationMs ?? 0;
            foreach (var s in _timing.Sections)
            {
                var end = double.IsPositiveInfinity(s.End) ? (dur > 0 ? dur : s.Start) : s.End;
                chips.Add(new BpmChip
                {
                    Label = $"{WaveformView.FormatBpm(s.Bpm)} BPM {s.Meter}/4",
                    StartMs = s.Start,
                    Tooltip = $"{WaveformView.FormatTime(s.Start)} → {WaveformView.FormatTime(end)}  ({(end - s.Start) / 1000.0:0.0} s)",
                });
            }
        }
        BpmSectionsList.ItemsSource = chips;
    }

    private void BpmChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: double ms })
        {
            Detail.SetView(ms - Detail.ViewLengthMs * 0.1, Detail.ViewLengthMs);
            OnSeekRequested(ms);
        }
    }

    // ============================================================== selection
    private bool HasSelection => _selStart != null && _selEnd != null && _selEnd > _selStart;

    private int SelectionInsideRegionIndex()
    {
        if (!HasSelection) return -1;
        return _regionList.FindIndex(r => r.ContainsRange(_selStart!.Value, _selEnd!.Value));
    }

    private void OnSelectionChanged(double a, double b)
    {
        _selStart = a; _selEnd = b;
        UpdateSelectionUi();
    }

    private void UpdateSelectionUi()
    {
        Detail.SelectionStartMs = _selStart;
        Detail.SelectionEndMs = _selEnd;
        int inside = SelectionInsideRegionIndex();
        Detail.SelectionInsideRegion = inside >= 0;
        Detail.InvalidateVisual();
        if (HasSelection)
        {
            var text = Loc.F("Selection.Text", WaveformView.FormatTime(_selStart!.Value), WaveformView.FormatTime(_selEnd!.Value),
                WaveformView.FormatTime(_selEnd.Value - _selStart.Value)) + GridHint(_selStart.Value, _selEnd.Value);
            text += "  — " + (inside >= 0 ? Loc.F("Selection.Inside", inside + 1) : Loc.T("Selection.Outside"));
            SelectionText.Text = text;
            if (LoopSelectionCheck.IsChecked == true && !_previewMode) _player.SetLoop(_selStart.Value, _selEnd.Value);
        }
        else
        {
            SelectionText.Text = _selStart != null ? Loc.F("Selection.StartOnly", WaveformView.FormatTime(_selStart.Value)) : "";
            _player.ClearLoop();
        }
        UpdateRegionButtons();
    }

    private string GridHint(double a, double b)
    {
        if (_timing == null || !_timing.HasTiming) return "";
        return $"  [{OnBeatText(a)} / {OnBeatText(b)}]";
    }

    private string OnBeatText(double t)
    {
        var red = _timing!.ActiveRedLine(t);
        if (red == null) return "?";
        var ph = _timing.BeatPhase(t);
        var off = Math.Min(ph, red.BeatLength - ph);
        if (off <= 1.0)
        {
            var measurePh = TimingModel.Mod(t - red.Time, red.BeatLength * red.Meter);
            var mOff = Math.Min(measurePh, red.BeatLength * red.Meter - measurePh);
            return Loc.T(mOff <= 1.0 ? "Grid.Downbeat" : "Grid.OnBeat");
        }
        return Loc.F("Grid.Off", off.ToString("0.#", CultureInfo.InvariantCulture));
    }

    private void SetSelStart_Click(object sender, RoutedEventArgs e) => SetSelectionEdge(true);
    private void SetSelEnd_Click(object sender, RoutedEventArgs e) => SetSelectionEdge(false);

    private void SetSelectionEdge(bool start)
    {
        if (_audio == null) return;
        var t = Detail.SnapTime(CurrentOriginalPositionMs());
        if (start)
        {
            _selStart = t;
            if (_selEnd != null && _selEnd <= t) _selEnd = null;
        }
        else
        {
            _selEnd = t;
            if (_selStart == null || _selStart >= t) _selStart = Math.Max(0, Detail.SnapTime(t - BeatLengthAt(t)));
        }
        UpdateSelectionUi();
    }

    private double BeatLengthAt(double t) => _timing?.ActiveRedLine(t)?.BeatLength ?? 500;

    // ============================================================== regions
    private void UpdateRegionButtons()
    {
        bool hasAudio = _audio != null;
        bool rowSelected = RegionsList.SelectedItem != null;
        AddRegionButton.IsEnabled = hasAudio && HasSelection;
        RemoveRegionButton.IsEnabled = (HasSelection && _regionList.Any(r => r.Overlaps(_selStart!.Value, _selEnd!.Value))) || rowSelected;
        ClearRegionsButton.IsEnabled = _regionList.Count > 0;
        FadeInButton.IsEnabled = FadeOutButton.IsEnabled = hasAudio && rowSelected;
        ExportButton.IsEnabled = hasAudio && _regionList.Count > 0 && !_busy;
        PreviewCutButton.IsEnabled = hasAudio && _regionList.Count > 0 && !_busy;
    }

    private void AddRegionButton_Click(object sender, RoutedEventArgs e) => AddOrSplitFromSelection();

    /// <summary>Enter: keep the selection as a region, or split the region it lies in.</summary>
    private void AddOrSplitFromSelection()
    {
        if (!HasSelection || _audio == null) return;
        int a = (int)Math.Round(_selStart!.Value), b = (int)Math.Round(_selEnd!.Value);
        int selectIndex;
        int inside = SelectionInsideRegionIndex();
        if (inside >= 0)
        {
            var host = _regionList[inside];
            if (!ConfirmCutPoints(a, b, a != host.StartMs, b != host.EndMs)) return;
            selectIndex = CutRegion.Split(_regionList, a, b);
        }
        else
        {
            bool checkA = !_regionList.Any(r => r.StartMs == a || r.EndMs == a || (r.StartMs < a && a < r.EndMs));
            bool checkB = !_regionList.Any(r => r.StartMs == b || r.EndMs == b || (r.StartMs < b && b < r.EndMs));
            if (!ConfirmCutPoints(a, b, checkA, checkB)) return;
            _regionList.Add(new CutRegion(a, b));
            selectIndex = -2; // resolve after normalization: the region containing a
        }
        _selStart = _selEnd = null;
        UpdateSelectionUi();
        RefreshRegions();
        if (selectIndex == -2) selectIndex = _regionList.FindIndex(r => r.Contains(a));
        if (selectIndex >= 0 && selectIndex < _regions.Count) RegionsList.SelectedIndex = selectIndex;
    }

    private void RemoveRegionButton_Click(object sender, RoutedEventArgs e) => DeleteSelectionOrRegion();

    /// <summary>Delete: remove the selected part from the regions, or the selected region row.</summary>
    private void DeleteSelectionOrRegion()
    {
        if (HasSelection)
        {
            int a = (int)Math.Round(_selStart!.Value), b = (int)Math.Round(_selEnd!.Value);
            if (_regionList.Any(r => r.Overlaps(a, b)))
            {
                bool checkA = _regionList.Any(r => r.StartMs < a && a < r.EndMs);
                bool checkB = _regionList.Any(r => r.StartMs < b && b < r.EndMs);
                if (!ConfirmCutPoints(a, b, checkA, checkB)) return;
                var cut = CutRegion.Subtract(_regionList, a, b);
                _regionList.Clear();
                _regionList.AddRange(cut);
                _selStart = _selEnd = null;
                UpdateSelectionUi();
                RefreshRegions();
                return;
            }
        }
        if (RegionsList.SelectedItem is RegionRow row)
        {
            _regionList.Remove(row.Region);
            RefreshRegions();
        }
    }

    private void ClearRegionsButton_Click(object sender, RoutedEventArgs e)
    {
        ClearRegions();
        RefreshRegions();
    }

    private void ClearRegions()
    {
        _regionList.Clear();
        _regions.Clear();
        Detail.SelectedRegionIndex = -1;
    }

    private void RefreshRegions()
    {
        int keep = RegionsList.SelectedIndex;
        var normalized = CutRegion.Normalize(_regionList);
        _regionList.Clear();
        _regionList.AddRange(normalized);
        var map = new TimeMap(normalized);
        _regions.Clear();
        for (int i = 0; i < normalized.Count; i++)
        {
            var r = normalized[i];
            _regions.Add(new RegionRow
            {
                Index = i + 1,
                Region = r,
                OutStartText = WaveformView.FormatTime(map.OutputStarts[i]),
                GridText = _timing != null && _timing.HasTiming ? $"{OnBeatText(r.StartMs)} → {OnBeatText(r.EndMs)}" : "",
            });
        }
        Detail.Regions = normalized;
        Overview.Regions = normalized;
        if (keep >= 0 && keep < _regions.Count) RegionsList.SelectedIndex = keep;
        Detail.SelectedRegionIndex = RegionsList.SelectedIndex;
        Detail.InvalidateVisual();
        Overview.InvalidateVisual();
        RegionsSummaryText.Text = normalized.Count == 0
            ? Loc.T("Regions.None")
            : Loc.F("Regions.Summary", normalized.Count, WaveformView.FormatTime(map.OutputLengthMs));
        if (_previewMode) ExitPreview(keepPosition: true);
        UpdateRegionButtons();
    }

    // ------------------------------------------------------------ mistake guard
    /// <summary>Warns when a new cut point is neither on a measure line nor on a BPM change. Returns false to cancel.</summary>
    private bool ConfirmCutPoints(int a, int b, bool checkA, bool checkB)
    {
        if (_screenshotDir != null) return true;
        var msg = BuildCutPointMessage(a, b, checkA, checkB);
        if (msg == null) return true;
        return MessageDialog.Confirm(this, Loc.T("Confirm.Title"), Loc.T("Confirm.Question"), msg);
    }

    private string? BuildCutPointMessage(int a, int b, bool checkA, bool checkB)
    {
        if (_timing == null || !_timing.HasTiming) return null;
        var lines = new List<string>();
        foreach (var (t, isStart, check) in new[] { (a, true, checkA), (b, false, checkB) })
        {
            if (!check) continue;
            var (dist, at) = DistanceToSafeLine(t);
            if (dist <= 1.0) continue;
            lines.Add(Loc.F("Confirm.Line", Loc.T(isStart ? "Confirm.Start" : "Confirm.End"),
                dist.ToString("0.#", CultureInfo.InvariantCulture), CellsText(t, dist), WaveformView.FormatTime(at)));
        }
        return lines.Count == 0 ? null : string.Join("\n\n", lines);
    }

    /// <summary>Distance from t to the nearest 1/1 or 1/2 beat position (of its own BPM section) or red line.</summary>
    private (double Distance, double At) DistanceToSafeLine(double t)
    {
        var red = _timing!.ActiveRedLine(t)!;
        double half = red.BeatLength / 2;
        double best = double.MaxValue, at = t;
        void Consider(double c)
        {
            var d = Math.Abs(t - c);
            if (d < best) { best = d; at = c; }
        }
        if (half > 0)
        {
            double k = Math.Round((t - red.Time) / half);
            for (int i = -1; i <= 1; i++) Consider(red.Time + (k + i) * half);
        }
        foreach (var r in _timing.RedLines) Consider(r.Time);
        return (best, at);
    }

    private string CellsText(double t, double dist)
    {
        var red = _timing!.ActiveRedLine(t)!;
        int d = Detail.SnapDivisor;
        if (d >= 1)
            return Loc.F("Confirm.Cells", d, (dist / (red.BeatLength / d)).ToString("0.##", CultureInfo.InvariantCulture));
        if (d == 0)
            return Loc.F("Confirm.Measures", (dist / (red.BeatLength * red.Meter)).ToString("0.##", CultureInfo.InvariantCulture));
        return Loc.F("Confirm.Beats", (dist / red.BeatLength).ToString("0.##", CultureInfo.InvariantCulture));
    }

    private void RegionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        Detail.SelectedRegionIndex = RegionsList.SelectedIndex;
        Detail.InvalidateVisual();
        UpdateRegionButtons();
        if (RegionsList.SelectedItem is RegionRow row && RegionsList.IsKeyboardFocusWithin)
            Detail.EnsureVisible(row.Region.StartMs);
    }

    private void OnRegionClicked(int index)
    {
        RegionsList.SelectedIndex = index;
    }

    // ============================================================== fades
    private void FadeInButton_Click(object sender, RoutedEventArgs e)
    {
        if (RegionsList.SelectedIndex >= 0) OpenFadePopup(RegionsList.SelectedIndex, true, null, FadeInButton);
    }

    private void FadeOutButton_Click(object sender, RoutedEventArgs e)
    {
        if (RegionsList.SelectedIndex >= 0) OpenFadePopup(RegionsList.SelectedIndex, false, null, FadeOutButton);
    }

    private void OpenFadePopup(int regionIndex, bool isStart, Point? at, UIElement? target = null)
    {
        if (_audio == null || regionIndex < 0 || regionIndex >= _regionList.Count) return;
        RegionsList.SelectedIndex = regionIndex;
        var r = _regionList[regionIndex];
        _fadeRegion = regionIndex;
        _fadeIsStart = isStart;
        _fadeUpdating = true;
        FadeExtendCheck.IsChecked = (isStart ? r.FadeInMode : r.FadeOutMode) == EdgeMode.Extend;
        RefreshFadePopupFields(r);
        _fadeUpdating = false;
        if (at != null)
        {
            FadePopup.PlacementTarget = Detail;
            FadePopup.Placement = PlacementMode.Relative;
            FadePopup.HorizontalOffset = Math.Max(0, at.Value.X - 140);
            FadePopup.VerticalOffset = at.Value.Y + 18;
        }
        else
        {
            FadePopup.PlacementTarget = target ?? Detail;
            FadePopup.Placement = PlacementMode.Bottom;
            FadePopup.HorizontalOffset = 0;
            FadePopup.VerticalOffset = 2;
        }
        FadePopup.IsOpen = true;
        FadeBox.Focus();
        FadeBox.SelectAll();
    }

    private void RefreshFadePopupFields(CutRegion r)
    {
        bool extend = (_fadeIsStart ? r.FadeInMode : r.FadeOutMode) == EdgeMode.Extend;
        int max = _fadeIsStart ? r.MaxFadeIn() : r.MaxFadeOut(_audio?.DurationMs ?? 0);
        FadeTitle.Text = $"{Loc.F("Fade.Region", _fadeRegion + 1)} — {Loc.T(_fadeIsStart ? "Fade.In" : "Fade.Out")}";
        FadeHint.Text = Loc.T(_fadeIsStart ? (extend ? "Fade.Hint.In.Extend" : "Fade.Hint.In") : (extend ? "Fade.Hint.Out.Extend" : "Fade.Hint.Out"));
        FadeSlider.Maximum = max;
        int value = Math.Min(max, _fadeIsStart ? r.FadeInMs : r.FadeOutMs);
        FadeSlider.Value = value;
        FadeBox.Text = value.ToString(CultureInfo.InvariantCulture);
        FadeMaxText.Text = Loc.F("Fade.Max", max);
        FadeSlider.IsEnabled = FadeBox.IsEnabled = max > 0;
    }

    private void FadeExtend_Changed(object sender, RoutedEventArgs e)
    {
        if (_fadeUpdating || _fadeRegion < 0 || _fadeRegion >= _regionList.Count) return;
        var mode = FadeExtendCheck.IsChecked == true ? EdgeMode.Extend : EdgeMode.Fade;
        var r = _regionList[_fadeRegion];
        r = _fadeIsStart ? r with { FadeInMode = mode } : r with { FadeOutMode = mode };
        int max = _fadeIsStart ? r.MaxFadeIn() : r.MaxFadeOut(_audio?.DurationMs ?? 0);
        r = _fadeIsStart ? r with { FadeInMs = Math.Min(r.FadeInMs, max) } : r with { FadeOutMs = Math.Min(r.FadeOutMs, max) };
        _regionList[_fadeRegion] = r;
        _fadeUpdating = true;
        RefreshFadePopupFields(r);
        _fadeUpdating = false;
        int keep = _fadeRegion;
        RefreshRegions();
        RegionsList.SelectedIndex = keep;
    }

    private void ApplyFade(int value)
    {
        if (_fadeRegion < 0 || _fadeRegion >= _regionList.Count) return;
        var r = _regionList[_fadeRegion];
        value = Math.Clamp(value, 0, (int)FadeSlider.Maximum);
        _regionList[_fadeRegion] = _fadeIsStart ? r with { FadeInMs = value } : r with { FadeOutMs = value };
        int keep = _fadeRegion;
        RefreshRegions();
        RegionsList.SelectedIndex = keep;
    }

    private void FadeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_fadeUpdating) return;
        _fadeUpdating = true;
        FadeBox.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
        _fadeUpdating = false;
        ApplyFade((int)Math.Round(e.NewValue));
    }

    private void FadeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitFadeBox(); FadePopup.IsOpen = false; e.Handled = true; }
        else if (e.Key == Key.Escape) { FadePopup.IsOpen = false; e.Handled = true; }
    }

    private void FadeBox_LostFocus(object sender, RoutedEventArgs e) => CommitFadeBox();

    private void CommitFadeBox()
    {
        if (_fadeUpdating) return;
        if (!int.TryParse(FadeBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return;
        v = Math.Clamp(v, 0, (int)FadeSlider.Maximum);
        _fadeUpdating = true;
        FadeSlider.Value = v;
        FadeBox.Text = v.ToString(CultureInfo.InvariantCulture);
        _fadeUpdating = false;
        ApplyFade(v);
    }

    // ============================================================== playback
    private double CurrentOriginalPositionMs()
    {
        var pos = _player.PositionMs;
        if (_previewMode && _previewMap != null) return _previewMap.Unmap(pos) ?? pos;
        return pos;
    }

    private void OnSeekRequested(double originalMs)
    {
        if (!_player.HasStream) return;
        if (_previewMode && _previewMap != null)
        {
            var mapped = _previewMap.Map(originalMs);
            if (mapped != null) { _player.Seek(mapped.Value); return; }
            ExitPreview(keepPosition: false);
        }
        _player.Seek(originalMs);
        UpdatePlayhead();
    }

    private void TogglePlay()
    {
        if (!_player.HasStream) return;
        if (_player.IsPlaying) _player.Pause();
        else
        {
            if (!_previewMode && LoopSelectionCheck.IsChecked == true && HasSelection)
            {
                var pos = _player.PositionMs;
                if (pos < _selStart!.Value || pos >= _selEnd!.Value) _player.Seek(_selStart.Value);
            }
            _player.Play();
        }
        UpdatePlayhead();
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        UpdatePlayhead();
    }

    private void UpdatePlayhead()
    {
        bool has = _player.HasStream;
        PlayButton.Content = Loc.T(_player.IsPlaying ? "Transport.Pause" : "Transport.Play");
        if (!has)
        {
            PositionText.Text = "00:00.000 / 00:00.000";
            DetailPlayhead.Visibility = OverviewPlayhead.Visibility = Visibility.Collapsed;
            return;
        }
        var pos = _player.PositionMs;
        var orig = CurrentOriginalPositionMs();
        PositionText.Text = _previewMode
            ? $"{WaveformView.FormatTime(pos)} / {WaveformView.FormatTime(_previewDurationMs)}  (orig {WaveformView.FormatTime(orig)})"
            : $"{WaveformView.FormatTime(pos)} / {WaveformView.FormatTime(_player.DurationMs)}";

        if (_player.IsPlaying && FollowPlayheadCheck.IsChecked == true && !Overview.IsDragging) Detail.EnsureVisible(orig);

        double x = Detail.MsToX(orig);
        if (x >= 0 && x <= Detail.ActualWidth)
        {
            DetailPlayhead.X1 = DetailPlayhead.X2 = x;
            DetailPlayhead.Y2 = Detail.ActualHeight;
            DetailPlayhead.Visibility = Visibility.Visible;
        }
        else DetailPlayhead.Visibility = Visibility.Collapsed;

        double ox = Overview.MsToX(orig);
        OverviewPlayhead.X1 = OverviewPlayhead.X2 = ox;
        OverviewPlayhead.Y2 = Overview.ActualHeight;
        OverviewPlayhead.Visibility = ox >= 0 && ox <= Overview.ActualWidth ? Visibility.Visible : Visibility.Collapsed;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player != null && _player.HasStream) _player.Volume = (float)e.NewValue;
        if (_settings != null) { _settings.Volume = e.NewValue; }
    }

    private void LoopSelectionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (LoopSelectionCheck.IsChecked == true && HasSelection && !_previewMode) _player.SetLoop(_selStart!.Value, _selEnd!.Value);
        else _player.ClearLoop();
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e) => Detail.ZoomToFit();

    // ============================================================== preview
    private async void PreviewCutButton_Click(object sender, RoutedEventArgs e) => await TogglePreviewAsync();

    private CutOptions PreviewOptions() => new() { CrossfadeMs = _settings.CrossfadeMs, TailSilenceMs = _settings.TailSilenceMs };

    private async Task TogglePreviewAsync()
    {
        if (_previewMode) { ExitPreview(keepPosition: true); return; }
        if (_audio == null || _regionList.Count == 0 || _busy) return;
        var opt = PreviewOptions();
        var regions = _regionList.ToList();
        var audio = _audio;
        SetBusy(true, Loc.T("Msg.Rendering"));
        var originalPos = _player.PositionMs;
        try
        {
            var pcm = await Task.Run(() => CutRenderer.Render(audio, regions, opt));
            _previewMap = new TimeMap(CutRegion.Normalize(regions));
            _previewDurationMs = pcm.DurationMs;
            _player.ClearLoop();
            _player.LoadPcm(pcm);
            _player.Volume = (float)VolumeSlider.Value;
            _previewMode = true;
            PreviewModeText.Text = Loc.T("Transport.Preview");
            PreviewCutButton.Content = Loc.T("Bottom.BackToOriginal");
            // continue from the current position when it lies inside a kept region, else from the start
            _player.Seek(_previewMap.Map(originalPos) ?? 0);
            _player.Play();
            SetBusy(false, Loc.F("Msg.PreviewReady", WaveformView.FormatTime(pcm.DurationMs)));
        }
        catch (Exception ex)
        {
            SetBusy(false, Loc.F("Msg.PreviewFail", ex.Message));
        }
    }

    private void ExitPreview(bool keepPosition)
    {
        if (!_previewMode) return;
        var orig = keepPosition ? CurrentOriginalPositionMs() : 0;
        bool wasPlaying = _player.IsPlaying;
        _previewMode = false;
        _previewMap = null;
        PreviewModeText.Text = "";
        PreviewCutButton.Content = Loc.T("Bottom.Preview");
        if (_audioPath != null)
        {
            try
            {
                _player.LoadFile(_audioPath);
                _player.Volume = (float)VolumeSlider.Value;
                _player.Seek(orig);
                if (wasPlaying) _player.Play();
            }
            catch (Exception ex) { StatusText.Text = Loc.F("Msg.ReloadFail", ex.Message); }
        }
        UpdatePlayhead();
    }

    // ============================================================== export
    private ExportContext? BuildExportContext()
    {
        if (_beatmap == null || _beatmapPath == null || _audio == null) return null;
        return new ExportContext
        {
            Beatmap = _beatmap,
            BeatmapPath = _beatmapPath,
            Audio = _audio,
            AudioPath = _audioPath,
            Regions = _regionList.ToList(),
            SongsFolder = _songsFolder,
            Settings = _settings,
        };
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var ctx = BuildExportContext();
        if (ctx == null || _regionList.Count == 0 || _busy) return;
        if (_player.IsPlaying) _player.Pause();
        var dlg = new ExportDialog(ctx) { Owner = this };
        dlg.ShowDialog();
        _settings.Save();
    }

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        if (status != null) StatusText.Text = status;
        OpenOsuButton.IsEnabled = ReloadButton.IsEnabled = !busy;
        UpdateRegionButtons();
    }

    // ============================================================== misc UI
    private void OpenOsuButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "osu! beatmap (*.osu)|*.osu", Title = Loc.T("Menu.Open").Replace("_", "") };
        if (_songsFolder != null && Directory.Exists(_songsFolder)) dlg.InitialDirectory = _songsFolder;
        if (dlg.ShowDialog(this) == true)
        {
            FollowOsuCheck.IsChecked = false;
            LoadBeatmap(dlg.FileName);
        }
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_beatmapPath != null) LoadBeatmap(_beatmapPath);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var osu = files.FirstOrDefault(f => f.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
        if (osu == null) return;
        FollowOsuCheck.IsChecked = false;
        LoadBeatmap(osu);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem or Slider) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.O) { OpenOsuButton_Click(sender, e); e.Handled = true; return; }
        if (ctrl && e.Key == Key.E) { ExportButton_Click(sender, e); e.Handled = true; return; }
        if (e.Key == Key.F5) { ReloadButton_Click(sender, e); e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Escape: if (FadePopup.IsOpen) FadePopup.IsOpen = false; else { _player.Stop(); UpdatePlayhead(); } break;
            case Key.Enter: AddOrSplitFromSelection(); break;
            case Key.Delete: DeleteSelectionOrRegion(); break;
            case Key.I: SetSelectionEdge(true); break;
            case Key.O: SetSelectionEdge(false); break;
            case Key.P: _ = TogglePreviewAsync(); break;
            case Key.Z: Detail.ZoomToFit(); break;
            case Key.L: LoopSelectionCheck.IsChecked = LoopSelectionCheck.IsChecked != true; break;
            case Key.Home: OnSeekRequested(0); break;
            case Key.End: OnSeekRequested(Math.Max(0, (_audio?.DurationMs ?? 0) - 1)); break;
            case Key.Left:
            case Key.Right:
            {
                if (_audio == null) break;
                var pos = CurrentOriginalPositionMs();
                double step = ctrl ? 10 : shift ? BeatLengthAt(pos) * (_timing?.ActiveRedLine(pos)?.Meter ?? 4) : BeatLengthAt(pos);
                var target = e.Key == Key.Left ? pos - step : pos + step;
                if (!ctrl && _timing != null && _timing.HasTiming)
                    target = shift ? _timing.SnapMeasure(target) : _timing.Snap(target, 1);
                OnSeekRequested(Math.Max(0, target));
                Detail.EnsureVisible(target);
                break;
            }
            default: return;
        }
        e.Handled = true;
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _settings.Save();
        _uiTimer.Stop();
        _watcher.Dispose();
        _player.Dispose();
    }
}
