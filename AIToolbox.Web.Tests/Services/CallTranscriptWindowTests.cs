using AIToolbox.Web.Services.CallEntityExtraction;

namespace AIToolbox.Web.Tests.Services;

public sealed class CallTranscriptWindowTests
{
    [Fact]
    public void AppendFinalSegment_StitchesAcrossSegmentBoundaries()
    {
        var window = new CallTranscriptWindow("call-1");

        window.AppendFinalSegment("the model number is 24 ABB", "u-1", "Customer");
        var stitched = window.AppendFinalSegment("324 ABN 3 if that helps", "u-2", "Customer");

        Assert.Equal("the model number is 24 ABB 324 ABN 3 if that helps", stitched);
    }

    [Fact]
    public void AppendFinalSegment_DropsOldestWordsPastWindowLimit()
    {
        var window = new CallTranscriptWindow("call-1");
        for (var i = 0; i < 40; i++)
        {
            window.AppendFinalSegment($"word{i}", $"u-{i}", "Customer");
        }

        var stitched = window.CurrentText();
        Assert.DoesNotContain("word0 ", stitched + " ");
        Assert.Contains("word39", stitched);
    }

    [Fact]
    public void TryMarkEmitted_OnlyTrueOnFirstOccurrence()
    {
        var window = new CallTranscriptWindow("call-1");

        Assert.True(window.TryMarkEmitted("24ABB324ABN3"));
        Assert.False(window.TryMarkEmitted("24ABB324ABN3"));
        Assert.False(window.TryMarkEmitted("24abb324abn3"));
    }

    [Fact]
    public void GetOrCreate_ReturnsSameWindowForSameCallId()
    {
        var store = new CallTranscriptWindowStore();

        var first = store.GetOrCreate("call-1");
        first.AppendFinalSegment("hello world", "u-1", "Customer");
        var second = store.GetOrCreate("call-1");

        Assert.Equal("hello world", second.CurrentText());
    }

    [Fact]
    public void RemoveIdle_DropsOnlyWindowsPastThreshold()
    {
        var store = new CallTranscriptWindowStore();
        store.GetOrCreate("stale-call");

        var removed = store.RemoveIdle(TimeSpan.Zero);

        Assert.Equal(1, removed);
    }
}
