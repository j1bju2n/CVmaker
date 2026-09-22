using CVmaker.Core.Cut;
using CVmaker.Core.Osu;
using Xunit;

namespace CVmaker.Core.Tests;

public class CutPlannerTests
{
    private static OsuFile Source() => OsuFile.Parse(OsuFileTests.Sample);

    private static CutResult Run(params CutRegion[] regions) =>
        CutPlanner.Transform(Source(), regions, new CutOptions(), "audio.mp3");

    [Fact]
    public void SingleRegionFromZero_IsIdentityForTimingAndObjects()
    {
        var r = Run(new CutRegion(0, 30000));
        var tps = r.Output.ReadTimingPoints();
        var src = Source().ReadTimingPoints();
        Assert.Equal(src.Select(t => t.Serialize()), tps.Select(t => t.Serialize()));
        Assert.Equal(Source().ReadHitObjects().Select(h => h.Serialize()), r.Output.ReadHitObjects().Select(h => h.Serialize()));
        Assert.Equal(8, r.KeptObjects);
        Assert.Equal(0, r.DroppedObjects);
        Assert.Equal("Song (Cut Ver.)", r.Output.Title);
        Assert.Equal("0", r.Output.Get("Metadata", "BeatmapID"));
        Assert.Equal("-1", r.Output.Get("Metadata", "BeatmapSetID"));
        Assert.Equal(21000, r.Output.PreviewTime);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void BeatAlignedGap_InSameSection_NeedsNoExtraRedLine()
    {
        // keep [1000,2000) and [3000,4500): the gap of 1000 ms is exactly two 500 ms beats
        var r = Run(new CutRegion(1000, 2000), new CutRegion(3000, 4500));
        var tps = r.Output.ReadTimingPoints();
        // red line at 0 (region 1 starts on the red line itself), green (kiai, 2.0x) carried to the join at 1000
        Assert.Equal("0,500,4,1,0,60,1,0", tps[0].Serialize());
        Assert.Equal("1000,-50,4,1,0,60,0,1", tps[1].Serialize());
        Assert.Equal(2, tps.Count);
        Assert.Equal(1, r.InsertedRedLines);

        var objs = r.Output.ReadHitObjects();
        // 1000 -> 0, 1500 -> 500, 3000..4000 -> 1000..2000; the hold at 2000 is outside
        Assert.Equal(new[] { 0, 500, 2000 }, objs.Select(o => o.Time).ToArray());
        Assert.Equal(3, r.KeptObjects);
        Assert.Equal(5, r.DroppedObjects);
        Assert.Equal(2500, r.OutputLengthMs);
        Assert.Equal(-1, r.Output.PreviewTime);
        // break 3000-4500 becomes 1000-2500
        Assert.Contains("2,1000,2500", r.Output.GetSection("Events")!.Lines);
        // bookmarks 1000 -> 0; 5000 and 9000 are dropped
        Assert.Equal("0", r.Output.Get("Editor", "Bookmarks"));
    }

    [Fact]
    public void NonBeatAlignedJoin_InsertsRedLineAndWarns()
    {
        var r = Run(new CutRegion(1000, 2000), new CutRegion(3250, 4500));
        var tps = r.Output.ReadTimingPoints();
        Assert.Equal(2, r.InsertedRedLines);
        Assert.Contains(tps, t => t.IsRedLine && t.Time == 1000);
        Assert.Contains(r.Warnings, w => w.Contains("not beat-aligned"));
    }

    [Fact]
    public void FirstRegionOffBeat_KeepsPhaseWithNegativeOffset()
    {
        var r = Run(new CutRegion(1200, 4000));
        var tps = r.Output.ReadTimingPoints();
        Assert.Equal(-200, tps[0].Time, 6);
        Assert.True(tps[0].IsRedLine);
        // object at 1500 (on a beat) -> 300, which is on the shifted grid (-200 + 500)
        Assert.Equal(300, r.Output.ReadHitObjects()[0].Time);
    }

    [Fact]
    public void HoldCrossingCut_IsClampedToRegionEnd()
    {
        var r = Run(new CutRegion(1000, 2500));
        var hold = r.Output.ReadHitObjects().Single(o => o.Kind == HitObjectKind.Hold);
        Assert.Equal(1000, hold.Time);
        Assert.Equal(1500, hold.EndTime);
        Assert.Equal(1, r.ClampedObjects);
    }

    [Fact]
    public void RegionAcrossRedLineChange_ShiftsInteriorPoints()
    {
        var r = Run(new CutRegion(3000, 8000));
        var tps = r.Output.ReadTimingPoints();
        // region starts on the green line at 3000 (beat 4 of the first red line): red at 0 + green at 0
        Assert.Equal("0,500,4,1,0,60,1,1", tps[0].Serialize());
        Assert.Equal("0,-50,4,1,0,60,0,1", tps[1].Serialize());
        Assert.Equal("2000,400,4,2,0,70,1,0", tps[2].Serialize());
        Assert.Equal("4000,-200,4,2,0,70,0,0", tps[3].Serialize());
        Assert.Equal(4, tps.Count);
        var objs = r.Output.ReadHitObjects();
        Assert.Equal(new[] { 1000, 2000, 2400, 4000 }, objs.Select(o => o.Time).ToArray());
        Assert.Equal(3200, objs[2].EndTime);
    }

    [Fact]
    public void JoinAcrossDifferentSections_InsertsRedLineWithNewBpm()
    {
        var r = Run(new CutRegion(1000, 3000), new CutRegion(5000, 7000));
        var tps = r.Output.ReadTimingPoints();
        Assert.Equal("0,500,4,1,0,60,1,0", tps[0].Serialize());
        Assert.Equal("2000,400,4,2,0,70,1,0", tps[1].Serialize());
        Assert.Equal(2, tps.Count);
        Assert.Equal(4000, r.OutputLengthMs);
    }

    [Fact]
    public void Normalize_MergesOverlapsKeepsTouchingAndDropsEmpty()
    {
        var n = CutRegion.Normalize(new[] { new CutRegion(500, 400), new CutRegion(300, 600), new CutRegion(100, 400), new CutRegion(900, 1000), new CutRegion(600, 700) });
        Assert.Equal(new[] { new CutRegion(100, 600), new CutRegion(600, 700), new CutRegion(900, 1000) }, n);
    }

    [Fact]
    public void Split_MakesTheSelectionItsOwnRegion()
    {
        var list = new List<CutRegion> { new(1000, 5000, 200, 300) };
        var idx = CutRegion.Split(list, 2000, 3000);
        Assert.Equal(1, idx);
        Assert.Equal(new[] { new CutRegion(1000, 2000, 200, 0), new CutRegion(2000, 3000), new CutRegion(3000, 5000, 0, 300) }, list);
        // touching pieces survive normalization
        Assert.Equal(3, CutRegion.Normalize(list).Count);
        Assert.Equal(-1, CutRegion.Split(list, 4000, 6000));
    }

    [Fact]
    public void Subtract_TrimsAndSplits()
    {
        var list = new List<CutRegion> { new(1000, 5000, 200, 300), new(6000, 7000) };
        var r = CutRegion.Subtract(list, 2000, 3000);
        Assert.Equal(new[] { new CutRegion(1000, 2000, 200, 0), new CutRegion(3000, 5000, 0, 300), new CutRegion(6000, 7000) }, r);
        r = CutRegion.Subtract(r, 0, 1500);
        Assert.Equal(new CutRegion(1500, 2000), r[0]);
        r = CutRegion.Subtract(r, 6500, 9000);
        Assert.Equal(new CutRegion(6000, 6500), r[^1]);
    }

    [Fact]
    public void TouchingRegions_ContinueTimingWithoutExtraRedLine()
    {
        var r = Run(new CutRegion(1000, 2000), new CutRegion(2000, 3000));
        var tps = r.Output.ReadTimingPoints();
        Assert.Single(tps.Where(t => t.IsRedLine));
        Assert.Equal(2000, r.OutputLengthMs);
        Assert.Equal(new[] { 0, 500, 1000 }, r.Output.ReadHitObjects().Select(o => o.Time).ToArray());
    }

    [Fact]
    public void LeadIn_ShiftsOutputTimes_AndEmptyOptionDropsNotes()
    {
        var r = CutPlanner.Transform(Source(), new[] { new CutRegion(1000, 2000, 250, 400, EdgeMode.Extend) }, new CutOptions(), "audio.mp3");
        Assert.Equal(250, r.Map.LeadInMs);
        Assert.Equal(400, r.Map.TailOutMs);
        Assert.Equal(1650, r.OutputLengthMs);
        Assert.Equal(250, r.Output.ReadTimingPoints()[0].Time, 6);
        Assert.Equal(250, r.Output.ReadHitObjects()[0].Time);

        var empty = CutPlanner.Transform(Source(), new[] { new CutRegion(1000, 2000) }, new CutOptions { KeepHitObjects = false }, "audio.mp3");
        Assert.Empty(empty.Output.ReadHitObjects());
        Assert.Single(empty.Output.ReadTimingPoints().Where(t => t.IsRedLine));
    }

    [Fact]
    public void NoteIssues_ReportOverlapsShortHoldsAndDuplicates()
    {
        // hold in column 2 (x=320) from 2000 to 3000 is clamped at 2500 and becomes 500 ms of a 500 ms beat: fine,
        // but the next region starts with a note in the same column at exactly the join
        var text = OsuFileTests.Sample.Replace("64,192,5000,1,0,0:0:0:0:", "320,192,5000,1,0,0:0:0:0:");
        var r = CutPlanner.Transform(OsuFile.Parse(text), new[] { new CutRegion(1000, 2500), new CutRegion(5000, 7000) }, new CutOptions(), "audio.mp3");
        Assert.Contains(r.Issues, i => i.Kind == NoteIssueKind.Overlap && i.Column == 3 && i.OutputTimeMs == 1500 && i.OriginalTimeMs == 5000);

        // clamping to a quarter of the hold (100 ms of a 500 ms beat) is reported as a short hold
        var s = CutPlanner.Transform(Source(), new[] { new CutRegion(1000, 2100), new CutRegion(5000, 7000) }, new CutOptions(), "audio.mp3");
        Assert.Contains(s.Issues, i => i.Kind == NoteIssueKind.ShortHold && i.Column == 3 && i.LengthMs == 100);

        // two notes at the same time in one column
        var dup = OsuFileTests.Sample.Replace("192,192,1500,1,0,0:0:0:0:", "64,192,1000,1,0,0:0:0:0:");
        var d = CutPlanner.Transform(OsuFile.Parse(dup), new[] { new CutRegion(1000, 2000) }, new CutOptions(), "audio.mp3");
        Assert.Contains(d.Issues, i => i.Kind == NoteIssueKind.Duplicate && i.Column == 1 && i.OutputTimeMs == 0);

        // a clean cut has no issues
        Assert.Empty(Run(new CutRegion(0, 30000)).Issues);
    }

    [Fact]
    public void CreatorAndVersionOverrides_AreWrittenToMetadata()
    {
        var r = CutPlanner.Transform(Source(), new[] { new CutRegion(1000, 2000) },
            new CutOptions { CreatorOverride = " Tamania ", VersionOverride = "Cut" }, "audio.mp3");
        Assert.Equal("Tamania", r.Output.Creator);
        Assert.Equal("Cut", r.Output.Version);
        Assert.Equal("Artist - Song (Cut Ver.) (Tamania) [Cut].osu", Export.CutExporter.BuildOsuFileName(r.Output));
        var untouched = CutPlanner.Transform(Source(), new[] { new CutRegion(1000, 2000) }, new CutOptions(), "audio.mp3");
        Assert.Equal("Mapper", untouched.Output.Creator);
    }

    [Fact]
    public void TimeMap_MapsAndUnmaps()
    {
        var map = new TimeMap(CutRegion.Normalize(new[] { new CutRegion(1000, 2000), new CutRegion(3000, 4500) }));
        Assert.Equal(0, map.Map(1000));
        Assert.Equal(999, map.Map(1999));
        Assert.Null(map.Map(2000));
        Assert.Equal(1000, map.Map(2000, inclusiveEnd: true));
        Assert.Equal(1000, map.Map(3000));
        Assert.Equal(3500, map.Unmap(1500));
        Assert.Null(map.Unmap(2500));
        Assert.Equal(2500, map.OutputLengthMs);

        var withFades = new TimeMap(CutRegion.Normalize(new[] { new CutRegion(1000, 2000, 300, 0, EdgeMode.Extend), new CutRegion(3000, 4500, 0, 500) }));
        Assert.Equal(300, withFades.LeadInMs);
        Assert.Equal(300, withFades.OutputStarts[0]);
        Assert.Equal(3300, withFades.OutputLengthMs);
        Assert.Equal(800, withFades.Unmap(100));   // inside the lead-in: 200 ms before the first region
        Assert.Equal(4600, withFades.Unmap(2900)); // inside the tail
        Assert.Null(withFades.Unmap(3300));

        // edge audio at a join never moves the cut points: regions stay butted together
        var butted = new TimeMap(CutRegion.Normalize(new[] { new CutRegion(1000, 2000, 300, 250), new CutRegion(3000, 4500, 100, 0, EdgeMode.Extend) }));
        Assert.Equal(0, butted.LeadInMs);
        Assert.Equal(0, butted.OutputStarts[0]);
        Assert.Equal(1000, butted.OutputStarts[1]);
        Assert.Equal(2500, butted.OutputLengthMs);
        Assert.Equal(3100, butted.Unmap(1100));
        Assert.Equal(1000, butted.Map(3000));
    }

    [Fact]
    public void EdgeAudioAtAJoin_KeepsRegionsButted_AndTimingContinuous()
    {
        // fade-out of region 1 is mixed over region 2; the join itself is still exactly on the grid
        var r = Run(new CutRegion(1000, 2000, 0, 250), new CutRegion(3000, 4500));
        Assert.Single(r.Output.ReadTimingPoints().Where(t => t.IsRedLine));
        Assert.Equal(2500, r.OutputLengthMs);
        Assert.Equal(new[] { 0, 500, 2000 }, r.Output.ReadHitObjects().Select(o => o.Time).ToArray());
    }
}
