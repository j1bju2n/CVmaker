using System.Diagnostics;
using System.IO;
using OsuMemoryDataProvider;
using OsuMemoryDataProvider.OsuMemoryModels.Direct;

namespace CVmaker.App.Services;

public sealed class OsuSnapshot
{
    public bool ProcessFound { get; init; }
    public bool CanRead { get; init; }
    public OsuMemoryStatus Status { get; init; } = OsuMemoryStatus.NotRunning;
    public string? OsuVersion { get; init; }
    public string? SongsFolder { get; init; }
    public string? BeatmapPath { get; init; }
    public string? MapString { get; init; }
    public string? Md5 { get; init; }
    public int AudioTimeMs { get; init; }
    public int GameMode { get; init; }
}

/// <summary>
/// Polls the running osu! stable process (via OsuMemoryDataProvider) for the currently selected
/// beatmap. Works in song select, the editor's song select and while playing/editing.
/// </summary>
public sealed class OsuWatcher : IDisposable
{
    private readonly StructuredOsuMemoryReader _reader = StructuredOsuMemoryReader.Instance;
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private int _cachedPid = -1;
    private string? _cachedSongs;
    private string? _lastPath;
    private bool _busy;

    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(400);
    public OsuSnapshot Latest { get; private set; } = new();

    /// <summary>Raised on a thread-pool thread whenever a poll completes.</summary>
    public event Action<OsuSnapshot>? Polled;
    /// <summary>Raised on a thread-pool thread when the selected beatmap path changes (may be null).</summary>
    public event Action<string?>? BeatmapChanged;

    public void Start()
    {
        _timer ??= new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, Interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public OsuSnapshot Poll()
    {
        lock (_gate)
        {
            if (_busy) return Latest;
            _busy = true;
        }
        try
        {
            var snap = ReadSnapshot();
            Latest = snap;
            Polled?.Invoke(snap);
            if (!string.Equals(snap.BeatmapPath, _lastPath, StringComparison.OrdinalIgnoreCase))
            {
                _lastPath = snap.BeatmapPath;
                BeatmapChanged?.Invoke(snap.BeatmapPath);
            }
            return snap;
        }
        finally
        {
            lock (_gate) _busy = false;
        }
    }

    private OsuSnapshot ReadSnapshot()
    {
        var procs = Process.GetProcessesByName("osu!");
        if (procs.Length == 0)
        {
            _cachedPid = -1;
            return new OsuSnapshot();
        }
        var proc = procs[0];
        var songs = ResolveSongsFolder(proc);

        if (!_reader.CanRead)
            return new OsuSnapshot { ProcessFound = true, CanRead = false, SongsFolder = songs };

        var gd = new GeneralData();
        var bm = new CurrentBeatmap();
        bool ok1, ok2;
        try
        {
            ok1 = _reader.TryRead(gd);
            ok2 = _reader.TryRead(bm);
        }
        catch
        {
            return new OsuSnapshot { ProcessFound = true, CanRead = false, SongsFolder = songs };
        }

        string? path = null;
        if (ok2 && songs != null && !string.IsNullOrWhiteSpace(bm.FolderName) && !string.IsNullOrWhiteSpace(bm.OsuFileName))
        {
            var invalid = Path.GetInvalidPathChars();
            if (!bm.FolderName.Any(invalid.Contains) && !bm.OsuFileName.Any(invalid.Contains))
            {
                var candidate = Path.Combine(songs, bm.FolderName.Trim(), bm.OsuFileName.Trim());
                if (File.Exists(candidate)) path = candidate;
            }
        }

        return new OsuSnapshot
        {
            ProcessFound = true,
            CanRead = ok1 || ok2,
            Status = ok1 ? gd.OsuStatus : OsuMemoryStatus.Unknown,
            OsuVersion = ok1 ? gd.OsuVersion : null,
            SongsFolder = songs,
            BeatmapPath = path,
            MapString = ok2 ? bm.MapString : null,
            Md5 = ok2 ? bm.Md5 : null,
            AudioTimeMs = ok1 ? gd.AudioTime : 0,
            GameMode = ok1 ? gd.GameMode : 0,
        };
    }

    /// <summary>osu! directory + BeatmapDirectory from osu!.&lt;user&gt;.cfg (defaults to "Songs").</summary>
    private string? ResolveSongsFolder(Process proc)
    {
        if (proc.Id == _cachedPid && _cachedSongs != null) return _cachedSongs;
        string? osuDir = null;
        try { osuDir = Path.GetDirectoryName(proc.MainModule?.FileName); }
        catch { /* access denied (elevated osu!) */ }
        osuDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!");
        if (!Directory.Exists(osuDir)) return null;

        string songs = Path.Combine(osuDir, "Songs");
        try
        {
            foreach (var cfg in Directory.GetFiles(osuDir, "osu!.*.cfg"))
            {
                if (Path.GetFileName(cfg).Equals("osu!.cfg", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var line in File.ReadLines(cfg))
                {
                    if (!line.StartsWith("BeatmapDirectory", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;
                    var v = parts[1].Trim();
                    if (v.Length == 0) continue;
                    songs = Path.IsPathRooted(v) ? v : Path.Combine(osuDir, v);
                }
            }
        }
        catch { /* ignore unreadable config */ }

        if (!Directory.Exists(songs)) return null;
        _cachedPid = proc.Id;
        _cachedSongs = songs;
        return songs;
    }

    public void Dispose() => Stop();
}
