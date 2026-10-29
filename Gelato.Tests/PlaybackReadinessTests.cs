using System.Reflection;
using Gelato.Decorators;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Gelato.Tests;

public class PlaybackReadinessTests
{
    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    public void PreparedHttpMediaDoesNotRequireAnotherProbeBasedOnItsDuration(int seconds)
    {
        var source = PreparedSource();
        source.RunTimeTicks = TimeSpan.FromSeconds(seconds).Ticks;

        // Movie and Episode preparation share this predicate before and after probing.
        Assert.False(NeedsProbe(source));
    }

    [Theory]
    [InlineData("container")]
    [InlineData("video")]
    [InlineData("streams-null")]
    [InlineData("runtime-null")]
    [InlineData("runtime-zero")]
    [InlineData("runtime-negative")]
    public void IncompleteMediaMetadataStillRequiresProbing(string missing)
    {
        var source = PreparedSource();
        switch (missing)
        {
            case "container": source.Container = null; break;
            case "video": source.MediaStreams = []; break;
            case "streams-null": source.MediaStreams = null; break;
            case "runtime-null": source.RunTimeTicks = null; break;
            case "runtime-zero": source.RunTimeTicks = 0; break;
            case "runtime-negative": source.RunTimeTicks = -1; break;
        }

        Assert.True(NeedsProbe(source));
    }

    private static MediaSourceInfo PreparedSource() => new()
    {
        Path = "https://provider.example.invalid/short-stream.mkv",
        Container = "mkv",
        MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Codec = "h264" }],
        RunTimeTicks = TimeSpan.FromMinutes(5).Ticks,
    };

    private static bool NeedsProbe(MediaSourceInfo source)
    {
        var method = typeof(MediaSourceManagerDecorator).GetMethod("NeedsProbe",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (bool)method!.Invoke(null, [source])!;
    }
}
