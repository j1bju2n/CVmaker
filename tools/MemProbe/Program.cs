using System.Diagnostics;
using OsuMemoryDataProvider;
using OsuMemoryDataProvider.OsuMemoryModels.Direct;

// Smoke test: can we read the currently selected beatmap from a running osu! stable?
var reader = StructuredOsuMemoryReader.Instance;
var procs = Process.GetProcessesByName("osu!");
Console.WriteLine($"osu! processes: {procs.Length}");
string? songs = null;
if (procs.Length > 0)
{
    var exe = procs[0].MainModule?.FileName;
    Console.WriteLine($"osu! exe: {exe}");
    var dir = Path.GetDirectoryName(exe)!;
    // BeatmapDirectory from osu!.<user>.cfg (may be relative or absolute)
    foreach (var cfg in Directory.GetFiles(dir, "osu!.*.cfg"))
    {
        foreach (var line in File.ReadLines(cfg))
        {
            if (line.StartsWith("BeatmapDirectory", StringComparison.OrdinalIgnoreCase))
            {
                var v = line.Split('=', 2)[1].Trim();
                songs = Path.IsPathRooted(v) ? v : Path.Combine(dir, v);
                Console.WriteLine($"{Path.GetFileName(cfg)}: BeatmapDirectory = {v} -> {songs}");
            }
        }
    }
    songs ??= Path.Combine(dir, "Songs");
}

for (int i = 0; i < 5; i++)
{
    Console.WriteLine($"--- read #{i + 1}  CanRead={reader.CanRead}");
    var gd = new GeneralData();
    var ok1 = reader.TryRead(gd);
    Console.WriteLine($"GeneralData ok={ok1} status={gd.OsuStatus}({gd.RawStatus}) mode={gd.GameMode} audioTime={gd.AudioTime} mods={gd.Mods} ver={gd.OsuVersion}");
    var bm = new CurrentBeatmap();
    var ok2 = reader.TryRead(bm);
    Console.WriteLine($"Beatmap ok={ok2} id={bm.Id} setId={bm.SetId} md5={bm.Md5}");
    Console.WriteLine($"  folder=[{bm.FolderName}]");
    Console.WriteLine($"  file  =[{bm.OsuFileName}]");
    Console.WriteLine($"  string=[{bm.MapString}] cs={bm.Cs} od={bm.Od} status={bm.Status}");
    if (songs != null && !string.IsNullOrEmpty(bm.FolderName) && !string.IsNullOrEmpty(bm.OsuFileName))
    {
        var full = Path.Combine(songs, bm.FolderName.Trim(), bm.OsuFileName);
        Console.WriteLine($"  path exists={File.Exists(full)}: {full}");
    }
    Thread.Sleep(500);
}
