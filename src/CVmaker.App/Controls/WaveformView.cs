using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CVmaker.Core.Audio;
using CVmaker.Core.Cut;
using CVmaker.Core.Timing;
using EdgeMode = CVmaker.Core.Cut.EdgeMode;

namespace CVmaker.App.Controls;

/// <summary>
/// Waveform + beat grid + BPM sections + kept regions (with edge zones and handles).
/// Two modes: the detail view (zoomable) and the overview (whole song, shows the detail viewport).
/// The playhead is drawn by a separate overlay so playback does not force a full redraw.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private const int BaseBucket = 64; // frames per cached peak bucket
    private const double DragThresholdPx = 6;

    private float[]? _peakMin, _peakMax;
    private int _sampleRate = 44100;
    private double _durationMs;

    private double _viewStartMs;
    private double _viewLengthMs = 30000;
    private double _amplitudeScale = 0.6;

    private Point? _dragStart;
    private bool _dragging;
    private bool _panning;
    private double _panStartView;
    private readonly List<(Rect Rect, int Region, bool IsStart)> _fadeHandles = new();
    private (int Region, bool IsStart)? _pendingHandle;
    private bool _overviewDragging;
    private double _grabOffsetMs;

    /// <summary>Overview only: true while the user drags the viewport (follow-playhead pauses meanwhile).</summary>
    public bool IsDragging => _overviewDragging;

    public WaveformView()
    {
        ClipToBounds = true;
        Focusable = true;
        SnapsToDevicePixels = true;
    }

    // ------------------------------------------------------------------ data
    public bool IsOverview { get; set; }
    public TimingModel? Timing { get; set; }
    public IReadOnlyList<CutRegion> Regions { get; set; } = Array.Empty<CutRegion>();
    public int SelectedRegionIndex { get; set; } = -1;
    public IReadOnlyList<(double Start, double End)> Breaks { get; set; } = Array.Empty<(double, double)>();
    public int KeyCount { get; set; } = 4;
    public IReadOnlyList<(int Time, int Column)> Notes { get; set; } = Array.Empty<(int, int)>();
    public bool ShowNotes { get; set; } = true;
    public double? SelectionStartMs { get; set; }
    public double? SelectionEndMs { get; set; }
    /// <summary>True when the selection lies inside one region (drawn orange: Enter splits, Delete removes).</summary>
    public bool SelectionInsideRegion { get; set; }
    /// <summary>-1 = no snapping, 0 = whole measures, otherwise the beat divisor (1, 2, 3, 4, 6, 8, 12, 16).</summary>
    public int SnapDivisor { get; set; } = 1;
    /// <summary>Overview only: the viewport of the detail view.</summary>
    public double ViewportStartMs { get; set; }
    public double ViewportLengthMs { get; set; }

    public double AmplitudeScale
    {
        get => _amplitudeScale;
        set { _amplitudeScale = Math.Clamp(value, 0.05, 8); InvalidateVisual(); }
    }

    public double DurationMs => _durationMs;
    public double ViewStartMs => IsOverview ? 0 : _viewStartMs;
    public double ViewLengthMs => IsOverview ? Math.Max(1, _durationMs) : _viewLengthMs;

    public event Action<double>? SeekRequested;
    public event Action<double, double>? SelectionChanged;
    public event Action? SelectionCleared;
    public event Action<double, double>? ViewChanged;
    /// <summary>Overview only: the viewport should move so that it starts at the given time (fires continuously while dragging).</summary>
    public event Action<double>? ViewportMoveRequested;
    public event Action<int>? RegionClicked;
    public event Action<int, bool, Point>? FadeHandleClicked;
    public event Action<double>? AmplitudeScaleChanged;

    public void SetAudio(PcmAudio? audio)
    {
        if (audio == null)
        {
            _peakMin = _peakMax = null;
            _durationMs = 0;
        }
        else
        {
            (_peakMin, _peakMax) = audio.Peaks(BaseBucket);
            _sampleRate = audio.SampleRate;
            _durationMs = audio.DurationMs;
        }
        _viewStartMs = 0;
        _viewLengthMs = Math.Max(1000, Math.Min(30000, _durationMs > 0 ? _durationMs : 30000));
        InvalidateVisual();
        ViewChanged?.Invoke(ViewStartMs, ViewLengthMs);
    }

    // ------------------------------------------------------------- coordinates
    public double MsToX(double ms) => (ms - ViewStartMs) / ViewLengthMs * ActualWidth;
    public double XToMs(double x) => ViewStartMs + x / ActualWidth * ViewLengthMs;

    public void SetView(double startMs, double lengthMs)
    {
        if (IsOverview) return;
        lengthMs = Math.Clamp(lengthMs, 200, Math.Max(200, _durationMs > 0 ? _durationMs : lengthMs));
        startMs = Math.Clamp(startMs, 0, Math.Max(0, _durationMs - lengthMs));
        _viewStartMs = startMs;
        _viewLengthMs = lengthMs;
        InvalidateVisual();
        ViewChanged?.Invoke(_viewStartMs, _viewLengthMs);
    }

    public void ZoomToFit() => SetView(0, Math.Max(1000, _durationMs));

    /// <summary>Scroll so that ms is visible (used for "follow playhead").</summary>
    public void EnsureVisible(double ms)
    {
        if (IsOverview) return;
        if (ms < _viewStartMs || ms > _viewStartMs + _viewLengthMs)
            SetView(ms - _viewLengthMs * 0.1, _viewLengthMs);
    }

    public double SnapTime(double ms)
    {
        if (SnapDivisor < 0 || Timing == null || !Timing.HasTiming) return Math.Round(ms);
        var snapped = SnapDivisor == 0 ? Timing.SnapMeasure(ms) : Timing.Snap(ms, SnapDivisor);
        return Math.Round(snapped);
    }

    /// <summary>osu! editor colours for beat snap divisors.</summary>
    public static Color DivisorColor(int divisor) => divisor switch
    {
        1 => Color.FromRgb(0xff, 0xff, 0xff),
        2 => Color.FromRgb(0xf0, 0x32, 0x32),
        3 => Color.FromRgb(0xb3, 0x6b, 0xff),
        4 => Color.FromRgb(0x4f, 0xa8, 0xff),
        6 => Color.FromRgb(0xd9, 0xa4, 0x00),
        8 => Color.FromRgb(0xff, 0xe9, 0x5c),
        12 => Color.FromRgb(0xb3, 0x86, 0x00),
        16 => Color.FromRgb(0x8f, 0x6c, 0xe6),
        _ => Color.FromRgb(0xaa, 0xaa, 0xaa),
    };

    // ---------------------------------------------------------------- drawing
    private static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1b, 0x1d, 0x23)));
    private static readonly Brush WaveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6c, 0xa0, 0xdc)));
    private static readonly Brush KiaiBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x28, 0xff, 0xa5, 0x00)));
    private static readonly Brush BreakBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)));
    private static readonly Brush RegionBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x62, 0x3c, 0xd0, 0x70)));
    private static readonly Brush RegionSelectedBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x84, 0x4c, 0xe0, 0x80)));
    private static readonly Pen RegionPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x3c, 0xd0, 0x70)), 1.5));
    private static readonly Pen RegionSelectedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x9f, 0xff, 0xb0)), 2.5));
    private static readonly Brush FadeBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x58, 0x3c, 0xd0, 0x70)));
    private static readonly Brush ExtendBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0xe0, 0xa0)));
    private static readonly Pen ExtendPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xa0, 0x9f, 0xff, 0xb0)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Brush SelectionBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x50, 0x40, 0x90, 0xff)));
    private static readonly Pen SelectionPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0xa8, 0xff)), 1));
    private static readonly Brush InsideBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0xff, 0x9f, 0x1c)));
    private static readonly Pen InsidePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xff, 0xb3, 0x4d)), 1.5));
    private static readonly Pen MeasurePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xc0, 0xff, 0xff, 0xff)), 1));
    private static readonly Pen BeatPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xff, 0xff, 0xff)), 1));
    private static readonly Pen RedLinePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xff, 0x40, 0x40)), 2));
    private static readonly Brush RedLineLabelBg = Frozen(new SolidColorBrush(Color.FromArgb(0xc0, 0xff, 0x40, 0x40)));
    private static readonly Pen ViewportPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xff, 0xd7, 0x00)), 1.5));
    private static readonly Brush ViewportBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x20, 0xff, 0xd7, 0x00)));
    private static readonly Brush RulerText = Frozen(new SolidColorBrush(Color.FromRgb(0xc8, 0xc8, 0xc8)));
    private static readonly Brush NoteBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xb0, 0xf0, 0xf0, 0xf0)));
    private static readonly Brush HandleBg = Frozen(new SolidColorBrush(Color.FromArgb(0xe0, 0x14, 0x3c, 0x22)));
    private static readonly Pen HandlePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x9f, 0xff, 0xb0)), 1));
    private static readonly Typeface Font = new("Segoe UI");
    private static readonly Dictionary<int, Pen> DivisorPens = new();

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static Pen PenFor(int divisor)
    {
        if (!DivisorPens.TryGetValue(divisor, out var pen))
        {
            var c = DivisorColor(divisor);
            byte alpha = divisor <= 2 ? (byte)0x80 : (byte)0x70;
            pen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)), 1));
            DivisorPens[divisor] = pen;
        }
        return pen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(BgBrush, null, new Rect(0, 0, w, h));
        _fadeHandles.Clear();

        double rulerH = IsOverview ? 0 : 18;
        double notesH = IsOverview || !ShowNotes || Notes.Count == 0 ? 0 : Math.Min(60, h * 0.25);
        double waveTop = rulerH;
        double waveH = h - rulerH - notesH;
        double mid = waveTop + waveH / 2;

        var vs = ViewStartMs;
        var vl = ViewLengthMs;
        var ve = vs + vl;

        // kiai / breaks
        if (Timing != null)
        {
            foreach (var (ks, ke) in Timing.KiaiRanges(_durationMs))
            {
                if (ke < vs || ks > ve) continue;
                dc.DrawRectangle(KiaiBrush, null, RectFor(ks, ke, waveTop, waveH));
            }
        }
        foreach (var (bs, be) in Breaks)
        {
            if (be < vs || bs > ve) continue;
            dc.DrawRectangle(BreakBrush, null, RectFor(bs, be, waveTop, waveH));
        }

        // regions with their edge zones
        for (int i = 0; i < Regions.Count; i++)
        {
            var r = Regions[i];
            if (r.EndMs + r.PostMs < vs || r.StartMs - r.PreMs > ve) continue;
            bool selected = i == SelectedRegionIndex;
            var rect = RectFor(r.StartMs, r.EndMs, 0, h);
            dc.DrawRectangle(selected ? RegionSelectedBrush : RegionBrush, null, rect);
            var pen = selected ? RegionSelectedPen : RegionPen;
            dc.DrawLine(pen, new Point(rect.Left, 0), new Point(rect.Left, h));
            dc.DrawLine(pen, new Point(rect.Right, 0), new Point(rect.Right, h));

            if (r.FadeInMs > 0)
            {
                if (r.FadeInMode == EdgeMode.Extend)
                {
                    var z = RectFor(r.StartMs - r.PreMs, r.StartMs, waveTop, waveH);
                    dc.DrawRectangle(ExtendBrush, null, z);
                    dc.DrawLine(ExtendPen, new Point(z.Left, waveTop), new Point(z.Left, waveTop + waveH));
                }
                else
                {
                    double x0 = MsToX(r.StartMs), x1 = MsToX(r.StartMs + r.InsideFadeMs);
                    dc.DrawGeometry(FadeBrush, null, Triangle(new Point(x0, waveTop + waveH), new Point(x1, waveTop), new Point(x1, waveTop + waveH)));
                }
            }
            if (r.FadeOutMs > 0)
            {
                if (r.FadeOutMode == EdgeMode.Extend)
                {
                    var z = RectFor(r.EndMs, r.EndMs + r.PostMs, waveTop, waveH);
                    dc.DrawRectangle(ExtendBrush, null, z);
                    dc.DrawLine(ExtendPen, new Point(z.Right, waveTop), new Point(z.Right, waveTop + waveH));
                }
                else
                {
                    double x0 = MsToX(r.EndMs), x1 = MsToX(r.EndMs + r.PostMs);
                    dc.DrawGeometry(FadeBrush, null, Triangle(new Point(x0, waveTop), new Point(x1, waveTop + waveH), new Point(x0, waveTop + waveH)));
                }
            }
        }

        // beat grid (detail only)
        if (!IsOverview && Timing != null && Timing.HasTiming)
        {
            double pxPerMs = w / vl;
            var firstSection = Timing.SectionAt(vs);
            double pxPerBeat = firstSection != null ? firstSection.BeatLength * pxPerMs : 0;
            bool drawBeats = pxPerBeat >= 4;
            int div = SnapDivisor > 1 && pxPerBeat / SnapDivisor >= 3 ? SnapDivisor : 1;
            foreach (var (t, downbeat, section) in Timing.Beats(vs, ve))
            {
                double x = Snap(MsToX(t));
                if (downbeat || drawBeats)
                    dc.DrawLine(downbeat ? MeasurePen : BeatPen, new Point(x, waveTop), new Point(x, waveTop + waveH));
                if (div > 1 && drawBeats)
                {
                    var step = section.BeatLength / div;
                    for (int k = 1; k < div; k++)
                    {
                        int g = Gcd(k, div);
                        var pen = PenFor(div / g);
                        double xs = Snap(MsToX(t + k * step));
                        dc.DrawLine(pen, new Point(xs, waveTop), new Point(xs, waveTop + waveH));
                    }
                }
            }
        }

        // waveform
        if (_peakMin != null && _peakMax != null && _durationMs > 0)
        {
            double framesPerPx = vl / 1000.0 * _sampleRate / w;
            double amp = waveH * 0.48 * _amplitudeScale;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                int cols = (int)w;
                var tops = new double[cols + 1];
                var bottoms = new double[cols + 1];
                int n = 0;
                for (int px = 0; px < cols; px++)
                {
                    double f0 = (vs / 1000.0 * _sampleRate) + px * framesPerPx;
                    double f1 = f0 + framesPerPx;
                    int b0 = (int)Math.Floor(f0 / BaseBucket);
                    int b1 = Math.Max(b0 + 1, (int)Math.Ceiling(f1 / BaseBucket));
                    float lo = 0, hi = 0;
                    bool any = false;
                    for (int b = Math.Max(0, b0); b < Math.Min(_peakMin.Length, b1); b++)
                    {
                        if (!any) { lo = _peakMin[b]; hi = _peakMax[b]; any = true; }
                        else { if (_peakMin[b] < lo) lo = _peakMin[b]; if (_peakMax[b] > hi) hi = _peakMax[b]; }
                    }
                    if (!any) { lo = hi = 0; }
                    tops[n] = Math.Max(waveTop, mid - hi * amp);
                    bottoms[n] = Math.Min(waveTop + waveH, mid - lo * amp);
                    n++;
                }
                bool started = false;
                for (int i = 0; i < n; i++)
                {
                    var p = new Point(i, tops[i]);
                    if (!started) { g.BeginFigure(p, true, true); started = true; }
                    else g.LineTo(p, false, false);
                }
                for (int i = n - 1; i >= 0; i--) g.LineTo(new Point(i, Math.Max(bottoms[i], tops[i] + 1)), false, false);
            }
            geo.Freeze();
            dc.DrawGeometry(WaveBrush, null, geo);
        }

        // notes strip (mania columns)
        if (notesH > 0)
        {
            double top = h - notesH;
            double laneH = notesH / Math.Max(1, KeyCount);
            foreach (var (t, col) in Notes)
            {
                if (t < vs || t > ve) continue;
                double x = MsToX(t);
                double y = top + col * laneH;
                dc.DrawRectangle(NoteBrush, null, new Rect(x - 1, y + 1, 2, Math.Max(1, laneH - 2)));
            }
        }

        // red lines with BPM labels
        if (Timing != null)
        {
            foreach (var s in Timing.Sections)
            {
                if (s.Start < vs - 1 || s.Start > ve) continue;
                double x = Snap(MsToX(s.Start));
                dc.DrawLine(RedLinePen, new Point(x, rulerH), new Point(x, h));
                if (!IsOverview)
                {
                    var label = $"{FormatBpm(s.Bpm)} BPM {s.Meter}/4";
                    var ft = Text(label, 11, Brushes.White);
                    dc.DrawRectangle(RedLineLabelBg, null, new Rect(x + 2, rulerH + 2, ft.Width + 6, ft.Height + 2));
                    dc.DrawText(ft, new Point(x + 5, rulerH + 3));
                }
            }
        }

        // selection
        if (SelectionStartMs != null && SelectionEndMs != null)
        {
            var a = Math.Min(SelectionStartMs.Value, SelectionEndMs.Value);
            var b = Math.Max(SelectionStartMs.Value, SelectionEndMs.Value);
            if (b >= vs && a <= ve)
            {
                var rect = RectFor(a, b, 0, h);
                dc.DrawRectangle(SelectionInsideRegion ? InsideBrush : SelectionBrush, SelectionInsideRegion ? InsidePen : SelectionPen, rect);
            }
        }

        // edge handles of the selected region
        if (!IsOverview && SelectedRegionIndex >= 0 && SelectedRegionIndex < Regions.Count)
        {
            var r = Regions[SelectedRegionIndex];
            double y = rulerH + 24;
            DrawHandle(dc, $"◀ {EdgeLabel(r.FadeInMs, r.FadeInMode, "in")}", MsToX(r.StartMs), y, true, SelectedRegionIndex);
            DrawHandle(dc, $"{EdgeLabel(r.FadeOutMs, r.FadeOutMode, "out")} ▶", MsToX(r.EndMs), y, false, SelectedRegionIndex);
        }

        // ruler
        if (!IsOverview) DrawRuler(dc, w, rulerH);

        // overview viewport
        if (IsOverview && ViewportLengthMs > 0)
        {
            var rect = RectFor(ViewportStartMs, ViewportStartMs + ViewportLengthMs, 0, h);
            dc.DrawRectangle(ViewportBrush, ViewportPen, rect);
        }
    }

    public static string EdgeLabel(int ms, EdgeMode mode, string edge) =>
        ms <= 0 ? $"{edge} 0 ms" : mode == EdgeMode.Extend ? $"ext {ms} ms" : $"fade {ms} ms";

    private void DrawHandle(DrawingContext dc, string label, double x, double y, bool isStart, int region)
    {
        var ft = Text(label, 11, Brushes.White);
        double pad = 5;
        double bw = ft.Width + pad * 2, bh = ft.Height + 4;
        double bx = isStart ? x - bw - 2 : x + 2;
        bx = Math.Clamp(bx, 0, Math.Max(0, ActualWidth - bw));
        var rect = new Rect(bx, y, bw, bh);
        dc.DrawRoundedRectangle(HandleBg, HandlePen, rect, 3, 3);
        dc.DrawText(ft, new Point(bx + pad, y + 2));
        _fadeHandles.Add((rect, region, isStart));
    }

    private static StreamGeometry Triangle(Point a, Point b, Point c)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a, true, true);
            ctx.LineTo(b, false, false);
            ctx.LineTo(c, false, false);
        }
        g.Freeze();
        return g;
    }

    private static FormattedText Text(string s, double size, Brush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush, 1.0);

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    private void DrawRuler(DrawingContext dc, double w, double rulerH)
    {
        double vl = ViewLengthMs;
        double[] steps = { 50, 100, 250, 500, 1000, 2000, 5000, 10000, 15000, 30000, 60000, 120000 };
        double step = steps.Last();
        foreach (var s in steps)
        {
            if (s / vl * w >= 70) { step = s; break; }
        }
        double first = Math.Floor(ViewStartMs / step) * step;
        for (double t = first; t <= ViewStartMs + vl; t += step)
        {
            if (t < 0) continue;
            double x = Snap(MsToX(t));
            dc.DrawLine(MeasurePen, new Point(x, 0), new Point(x, rulerH * 0.5));
            dc.DrawText(Text(FormatTime(t), 10, RulerText), new Point(x + 3, 1));
        }
    }

    private Rect RectFor(double aMs, double bMs, double top, double height)
    {
        double x0 = Math.Max(-2, MsToX(aMs));
        double x1 = Math.Min(ActualWidth + 2, MsToX(bMs));
        if (x1 < x0) (x0, x1) = (x1, x0);
        return new Rect(x0, top, Math.Max(1, x1 - x0), height);
    }

    private static double Snap(double x) => Math.Round(x) + 0.5;

    public static string FormatTime(double ms)
    {
        var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return ms >= 3600000 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    public static string FormatBpm(double bpm) => Math.Abs(bpm - Math.Round(bpm)) < 0.005 ? ((int)Math.Round(bpm)).ToString(CultureInfo.InvariantCulture) : bpm.ToString("0.##", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------ interaction
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            AmplitudeScale = e.Delta > 0 ? _amplitudeScale * 1.15 : _amplitudeScale / 1.15;
            AmplitudeScaleChanged?.Invoke(_amplitudeScale);
            return;
        }
        if (IsOverview || _durationMs <= 0) return;
        var pos = e.GetPosition(this);
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            SetView(_viewStartMs - Math.Sign(e.Delta) * _viewLengthMs * 0.2, _viewLengthMs);
        }
        else
        {
            double anchorMs = XToMs(pos.X);
            double factor = e.Delta > 0 ? 0.8 : 1.25;
            double newLen = _viewLengthMs * factor;
            double newStart = anchorMs - (pos.X / ActualWidth) * newLen;
            SetView(newStart, newLen);
        }
    }

    private (int Region, bool IsStart)? HandleAt(Point pos)
    {
        foreach (var (rect, region, isStart) in _fadeHandles)
            if (rect.Contains(pos)) return (region, isStart);
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var pos = e.GetPosition(this);
        if (IsOverview)
        {
            // grab the viewport where it is, or centre it on the click; then follow the mouse smoothly
            var ms = XToMs(pos.X);
            bool inside = ms >= ViewportStartMs && ms <= ViewportStartMs + ViewportLengthMs;
            _grabOffsetMs = inside ? ms - ViewportStartMs : ViewportLengthMs / 2;
            _overviewDragging = true;
            CaptureMouse();
            ViewportMoveRequested?.Invoke(ms - _grabOffsetMs);
            e.Handled = true;
            return;
        }
        _pendingHandle = HandleAt(pos);
        if (_pendingHandle != null)
        {
            // the popup opens on mouse-up so the release does not close it again
            e.Handled = true;
            return;
        }
        _dragStart = pos;
        _dragging = false;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        if (_overviewDragging)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                ViewportMoveRequested?.Invoke(XToMs(Math.Clamp(pos.X, 0, ActualWidth)) - _grabOffsetMs);
            else
            {
                _overviewDragging = false;
                ReleaseMouseCapture();
            }
            return;
        }
        if (_panning)
        {
            double dxMs = (pos.X - _dragStart!.Value.X) / ActualWidth * _viewLengthMs;
            SetView(_panStartView - dxMs, _viewLengthMs);
            return;
        }
        if (_dragStart == null || e.LeftButton != MouseButtonState.Pressed) return;
        if (!_dragging && Math.Abs(pos.X - _dragStart.Value.X) < DragThresholdPx) return;
        _dragging = true;
        SelectionStartMs = SnapTime(XToMs(_dragStart.Value.X));
        SelectionEndMs = SnapTime(XToMs(pos.X));
        UpdateInsideFlag();
        InvalidateVisual();
    }

    private void UpdateInsideFlag()
    {
        if (SelectionStartMs == null || SelectionEndMs == null) { SelectionInsideRegion = false; return; }
        var a = Math.Min(SelectionStartMs.Value, SelectionEndMs.Value);
        var b = Math.Max(SelectionStartMs.Value, SelectionEndMs.Value);
        SelectionInsideRegion = Regions.Any(r => r.ContainsRange(a, b));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsOverview)
        {
            if (_overviewDragging)
            {
                _overviewDragging = false;
                ReleaseMouseCapture();
                e.Handled = true;
            }
            return;
        }
        var pos = e.GetPosition(this);
        if (_pendingHandle != null)
        {
            var pending = _pendingHandle.Value;
            _pendingHandle = null;
            var hit = HandleAt(pos);
            if (hit != null && hit.Value == pending) FadeHandleClicked?.Invoke(pending.Region, pending.IsStart, pos);
            e.Handled = true;
            return;
        }
        ReleaseMouseCapture();
        if (_dragging && SelectionStartMs != null && SelectionEndMs != null)
        {
            var a = Math.Min(SelectionStartMs.Value, SelectionEndMs.Value);
            var b = Math.Max(SelectionStartMs.Value, SelectionEndMs.Value);
            SelectionStartMs = a; SelectionEndMs = b;
            UpdateInsideFlag();
            if (b - a >= 1) SelectionChanged?.Invoke(a, b);
            else { SelectionStartMs = SelectionEndMs = null; SelectionCleared?.Invoke(); }
        }
        else if (_dragStart != null)
        {
            var ms = Math.Max(0, XToMs(pos.X));
            int hit = -1;
            for (int i = 0; i < Regions.Count; i++) if (Regions[i].Contains(ms)) { hit = i; break; }
            RegionClicked?.Invoke(hit);
            SeekRequested?.Invoke(ms);
        }
        _dragStart = null;
        _dragging = false;
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (IsOverview) return;
        // right click: drop an unconfirmed selection (and cancel a drag in progress)
        if (_dragging || _dragStart != null)
        {
            ReleaseMouseCapture();
            _dragStart = null;
            _dragging = false;
        }
        if (SelectionStartMs != null || SelectionEndMs != null)
        {
            SelectionStartMs = SelectionEndMs = null;
            SelectionInsideRegion = false;
            SelectionCleared?.Invoke();
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle && !IsOverview)
        {
            _panning = true;
            _dragStart = e.GetPosition(this);
            _panStartView = _viewStartMs;
            CaptureMouse();
            e.Handled = true;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Middle && _panning)
        {
            _panning = false;
            _dragStart = null;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
