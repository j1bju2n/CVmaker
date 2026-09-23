using CVmania.Core.Osu;
using CVmania.Core.Timing;
using Xunit;

namespace CVmania.Core.Tests;

public class TimingModelTests
{
    private static TimingModel Model() => new(OsuFile.Parse(OsuFileTests.Sample).ReadTimingPoints());

    [Fact]
    public void Sections_FollowRedLines()
    {
        var m = Model();
        Assert.Equal(2, m.Sections.Count);
        Assert.Equal(1000, m.Sections[0].Start);
        Assert.Equal(5000, m.Sections[0].End);
        Assert.Equal(120, m.Sections[0].Bpm, 6);
        Assert.Equal(150, m.Sections[1].Bpm, 6);
        Assert.True(double.IsPositiveInfinity(m.Sections[1].End));
    }

    [Fact]
    public void ActiveRedLine_BeforeFirstUsesFirst()
    {
        var m = Model();
        Assert.Equal(1000, m.ActiveRedLine(0)!.Time);
        Assert.Equal(1000, m.ActiveRedLine(4999)!.Time);
        Assert.Equal(5000, m.ActiveRedLine(5000)!.Time);
    }

    [Fact]
    public void ActiveGreenLine_IsResetByRedLine()
    {
        var m = Model();
        Assert.Null(m.ActiveGreenLine(2999));
        Assert.Equal(2.0, m.ActiveGreenLine(3000)!.SvMultiplier, 6);
        Assert.Null(m.ActiveGreenLine(5000));          // red line at 5000 resets SV
        Assert.Equal(0.5, m.ActiveGreenLine(7500)!.SvMultiplier, 6);
    }

    [Fact]
    public void StateAt_CarriesSamplesVolumeAndKiai()
    {
        var m = Model();
        var s = m.StateAt(3500)!.Value;
        Assert.True(s.Kiai);
        Assert.Equal(60, s.Volume);
        Assert.Equal(2.0, s.Sv, 6);
        var before = m.StateBefore(5000)!.Value;
        Assert.True(before.Kiai);
        var at = m.StateAt(5000)!.Value;
        Assert.False(at.Kiai);
        Assert.Equal(70, at.Volume);
        Assert.Equal(2, at.SampleSet);
    }

    [Fact]
    public void Snap_UsesSectionGrid()
    {
        var m = Model();
        Assert.Equal(1500, m.Snap(1520, 1), 6);
        Assert.Equal(1750, m.Snap(1700, 2), 6);
        Assert.Equal(1500, m.Snap(1990, 1, SnapMode.Floor), 6);
        Assert.Equal(2000, m.Snap(1510, 1, SnapMode.Ceiling), 6);
        Assert.Equal(5400, m.Snap(5380, 1), 6); // second section: 400ms beats
        Assert.Equal(0, m.Snap(10, 1), 6);      // grid extends before the first red line
        Assert.Equal(3000, m.SnapMeasure(3100), 6);
    }

    [Fact]
    public void Beats_EnumerateDownbeats()
    {
        var m = Model();
        var beats = m.Beats(1000, 5000).ToList();
        Assert.Equal(8, beats.Count);
        Assert.True(beats[0].Downbeat);
        Assert.False(beats[1].Downbeat);
        Assert.True(beats[4].Downbeat);
        Assert.Equal(4500, beats[7].Time, 6);
    }

    [Fact]
    public void KiaiRanges_AndMostCommonBpm()
    {
        var m = Model();
        var kiai = m.KiaiRanges(10000).ToList();
        Assert.Single(kiai);
        Assert.Equal((3000.0, 5000.0), kiai[0]);
        Assert.Equal(150, m.MostCommonBpm(20000), 6);
        Assert.Equal(120, m.MostCommonBpm(8000), 6);
    }

    [Fact]
    public void MostCommonBpm_FollowsStableRules()
    {
        // the first red line (at 1000) counts from 0: 120 BPM has 5000 ms, 150 BPM has 5000 ms => tie goes to the first
        var m = Model();
        Assert.Equal(120, m.MostCommonBpm(10000), 6);
        Assert.Equal(150, m.MostCommonBpm(10001), 6);
        // red lines after the last object count nothing
        Assert.Equal(120, m.MostCommonBpm(4000), 6);
        // the same BPM in several sections is summed (500 + 400 + 500 ms lines)
        var pts = new[] { "0,500,4,1,0,60,1,0", "1000,400,4,1,0,60,1,0", "1800,500,4,1,0,60,1,0" }
            .Select(l => { TimingPoint.TryParse(l, out var t); return t; });
        Assert.Equal(120, new TimingModel(pts).MostCommonBpm(2400), 6);
        Assert.Equal(7000, m.LastObjectTime(Array.Empty<HitObject>())); // no notes: the last timing point
        Assert.Equal(9000, m.LastObjectTime(OsuFile.Parse(OsuFileTests.Sample).ReadHitObjects()));
    }
}
