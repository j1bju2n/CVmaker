using CVmania.Core.Osu;
using Xunit;

namespace CVmania.Core.Tests;

public class OsuFileTests
{
    public const string Sample = "osu file format v14\r\n\r\n[General]\r\nAudioFilename: audio.mp3\r\nAudioLeadIn: 0\r\nPreviewTime: 21000\r\nMode: 3\r\n\r\n[Editor]\r\nBookmarks: 1000,5000,9000\r\nBeatDivisor: 4\r\n\r\n[Metadata]\r\nTitle:Song\r\nTitleUnicode:Song\r\nArtist:Artist\r\nArtistUnicode:Artist\r\nCreator:Mapper\r\nVersion:Hard\r\nSource:\r\nTags:tag1 tag2\r\nBeatmapID:123\r\nBeatmapSetID:456\r\n\r\n[Difficulty]\r\nHPDrainRate:8\r\nCircleSize:4\r\nOverallDifficulty:8\r\nApproachRate:5\r\nSliderMultiplier:1.4\r\nSliderTickRate:1\r\n\r\n[Events]\r\n//Background and Video events\r\n0,0,\"bg.jpg\",0,0\r\n//Break Periods\r\n2,3000,4500\r\n//Storyboard Layer 0 (Background)\r\n\r\n[TimingPoints]\r\n1000,500,4,1,0,60,1,0\r\n3000,-50,4,1,0,60,0,1\r\n5000,400,4,2,0,70,1,0\r\n7000,-200,4,2,0,70,0,0\r\n\r\n[HitObjects]\r\n64,192,1000,1,0,0:0:0:0:\r\n192,192,1500,1,0,0:0:0:0:\r\n320,192,2000,128,0,3000:0:0:0:0:\r\n448,192,4000,1,0,0:0:0:0:\r\n64,192,5000,1,0,0:0:0:0:\r\n192,192,5400,128,0,6200:0:0:0:0:\r\n320,192,7000,1,0,0:0:0:0:\r\n448,192,9000,1,0,0:0:0:0:\r\n";

    [Fact]
    public void RoundTrip_PreservesEveryLine()
    {
        var f = OsuFile.Parse(Sample);
        Assert.Equal(Sample, f.Serialize());
    }

    [Fact]
    public void TypedAccessors_ReadValues()
    {
        var f = OsuFile.Parse(Sample);
        Assert.Equal("audio.mp3", f.AudioFilename);
        Assert.Equal(21000, f.PreviewTime);
        Assert.Equal(3, f.Mode);
        Assert.Equal("Song", f.Title);
        Assert.Equal("Mapper", f.Creator);
        Assert.Equal(4, f.CircleSize);
        Assert.Equal(4, f.ReadTimingPoints().Count);
        Assert.Equal(8, f.ReadHitObjects().Count);
    }

    [Fact]
    public void SetValue_KeepsSeparatorStyle()
    {
        var f = OsuFile.Parse(Sample);
        f.Set("General", "AudioFilename", "cut.ogg");
        f.Set("Metadata", "Title", "Song (Cut Ver.)");
        var text = f.Serialize();
        Assert.Contains("AudioFilename: cut.ogg\r\n", text);
        Assert.Contains("Title:Song (Cut Ver.)\r\n", text);
    }

    [Fact]
    public void TimingPoint_ParsesLegacyTwoFieldLines()
    {
        Assert.True(TimingPoint.TryParse("1000,500", out var red));
        Assert.True(red.Uninherited);
        Assert.Equal(120, red.Bpm, 6);
        Assert.True(TimingPoint.TryParse("2000,-50", out var green));
        Assert.False(green.Uninherited);
        Assert.Equal(2.0, green.SvMultiplier, 6);
        Assert.Equal("1000,500,4,0,0,100,1,0", red.Serialize());
    }

    [Fact]
    public void TimingPoint_KeepsBeatLengthTextExactly()
    {
        Assert.True(TimingPoint.TryParse("2512,413.793103448276,6,1,0,30,1,0", out var tp));
        Assert.Equal("2512,413.793103448276,6,1,0,30,1,0", tp.Serialize());
        tp.Time += 100.5;
        Assert.Equal("2612.5,413.793103448276,6,1,0,30,1,0", tp.Serialize());
    }

    [Fact]
    public void HitObject_HoldEndTime_ReadWrite()
    {
        Assert.True(HitObject.TryParse("320,192,2000,128,0,3000:0:0:0:0:", out var ho));
        Assert.Equal(HitObjectKind.Hold, ho.Kind);
        Assert.Equal(3000, ho.EndTime);
        ho.EndTime = 2500;
        Assert.Equal("320,192,2000,128,0,2500:0:0:0:0:", ho.Serialize());
        Assert.Equal(2, ho.Column(4));
    }
}
