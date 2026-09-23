using CVmania.Core.Cut;
using CVmania.Core.Osu;
using Xunit;

namespace CVmania.Core.Tests;

public class WorkSessionTests
{
    [Fact]
    public void RoundTrip_KeepsRegionsAndIsKeyedBySong()
    {
        var map = OsuFile.Parse(OsuFileTests.Sample);
        var regions = new[] { new CutRegion(1000, 2000, 100, 200, EdgeMode.Extend, EdgeMode.Fade), new CutRegion(3000, 4500) };
        var s = WorkSession.From(map, @"C:\Songs\Set\a.osu", regions, 500, 20000);
        var dir = Path.Combine(Path.GetTempPath(), "cvmania-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = WorkSession.AutoPathFor(dir, @"C:\Songs\Set\a.osu", map.AudioFilename);
            s.Save(path);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));

            // any difficulty of the same set, in any case, finds the same session
            var back = WorkSession.LoadAuto(dir, @"c:\songs\set\OTHER DIFF.osu", "AUDIO.MP3");
            Assert.NotNull(back);
            Assert.Equal(regions, back!.ToRegions());
            Assert.Equal(500, back.ViewStartMs);
            Assert.Equal(20000, back.ViewLengthMs);
            Assert.Equal("Artist - Song [Hard]", back.Title);
            Assert.Equal(@"C:\Songs\Set\a.osu", back.BeatmapPath);
            Assert.Equal("CV!mania", back.Generator);

            Assert.Null(WorkSession.LoadAuto(dir, @"C:\Songs\Other\a.osu", "audio.mp3"));
            Assert.Null(WorkSession.LoadAuto(dir, @"C:\Songs\Set\a.osu", "other.mp3"));

            // explicit files use the same shape
            var explicitPath = Path.Combine(dir, "work" + WorkSession.Extension);
            s.Save(explicitPath);
            Assert.Equal(regions, WorkSession.Load(explicitPath)!.ToRegions());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
