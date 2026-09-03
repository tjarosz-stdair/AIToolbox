using AIToolbox.Web.Services.CallEntityExtraction;

namespace AIToolbox.Web.Tests.Services;

public sealed class CallTranscriptWindowTests
{
    [Fact]
    public void AppendFinalSegment_StitchesAcrossSegmentBoundaries()
    {
        var window = new CallTranscriptWindow("call-1");

        window.AppendFinalSegment("the model number is 24 ABB", "u-1", "External", "acme:jsmith", 1, DateTime.UtcNow);
        var stitched = window.AppendFinalSegment("324 ABN 3 if that helps", "u-2", "External", "acme:jsmith", 2, DateTime.UtcNow);

        Assert.Equal("the model number is 24 ABB 324 ABN 3 if that helps", stitched);
    }

    [Fact]
    public void AppendFinalSegment_DropsOldestWordsPastWindowLimit()
    {
        var window = new CallTranscriptWindow("call-1");
        for (var i = 0; i < 40; i++)
        {
            window.AppendFinalSegment($"word{i}", $"u-{i}", "External", "acme:jsmith", i, DateTime.UtcNow);
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
        first.AppendFinalSegment("hello world", "u-1", "External", "acme:jsmith", 1, DateTime.UtcNow);
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

    [Fact]
    public void AppendFinalSegment_AgentKeyIsSetOnceAndNotOverwritten()
    {
        var window = new CallTranscriptWindow("call-1");

        window.AppendFinalSegment("hello", "u-1", "Agent", "acme:jsmith", 1, DateTime.UtcNow);
        window.AppendFinalSegment("world", "u-2", "External", "acme:someoneelse", 2, DateTime.UtcNow);

        Assert.Equal("acme:jsmith", window.AgentKey);
    }

    [Fact]
    public void RecentLines_DropsOldestPastRingBufferLimit()
    {
        var window = new CallTranscriptWindow("call-1");
        for (var i = 0; i < 60; i++)
        {
            window.AppendFinalSegment($"word{i}", $"u-{i}", "External", "acme:jsmith", i, DateTime.UtcNow);
        }

        var lines = window.RecentLines();

        Assert.Equal(50, lines.Count);
        Assert.Equal("u-10", lines[0].UtteranceId);
        Assert.Equal("u-59", lines[^1].UtteranceId);
    }
}
