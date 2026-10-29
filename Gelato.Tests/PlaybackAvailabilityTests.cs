using System.Reflection;
using Gelato.Decorators;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Gelato.Tests;

public class PlaybackAvailabilityTests
{
    [Theory]
    [InlineData("GetVideoStream", false)]
    [InlineData("GetVideoStreamByContainer", false)]
    [InlineData("GetPlaybackInfo", true)]
    [InlineData("GetPostedPlaybackInfo", true)]
    public async Task UnavailableSelectionUsesClientContractWithoutProbing(string action, bool playbackInfo)
    {
        MediaSourceInfo?[] candidates =
        [
            new() { Path = "gelato://movie/test-item" },
            new() { Path = "stremio://movie/test-item" },
            new() { Path = "/stub" },
            new() { Path = null },
            new() { Path = "" },
            null,
        ];

        foreach (var candidate in candidates)
        {
            var probeCalled = false;
            Task<MediaSourceInfo> Prepare(MediaSourceInfo source)
            {
                probeCalled = true;
                return Task.FromResult(source);
            }

            if (playbackInfo)
            {
                Assert.Null(await PrepareSelection(candidate, action, Prepare));
            }
            else
            {
                await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
                    PrepareSelection(candidate, action, Prepare));
            }

            Assert.False(probeCalled);
        }
    }

    [Theory]
    [InlineData("GetVideoStream")]
    [InlineData("GetPostedPlaybackInfo")]
    public async Task AvailableHttpSourceReturnsPreparedMetadataAndPreservesSourceIdentity(string action)
    {
        var selected = new MediaSourceInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            ETag = Guid.NewGuid().ToString("N"),
            Path = "https://provider.example.invalid/stream.mkv",
        };
        var prepared = new MediaSourceInfo
        {
            Id = selected.Id,
            ETag = selected.ETag,
            Path = selected.Path,
            Container = "mkv",
            MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Codec = "h264" }],
            RunTimeTicks = TimeSpan.FromMinutes(5).Ticks,
        };
        var calls = 0;

        var result = await PrepareSelection(selected, action, source =>
        {
            Assert.Same(selected, source);
            calls++;
            return Task.FromResult(prepared);
        });

        Assert.Same(prepared, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AvailableHttpProbeFailureIsNotConvertedToUnavailable()
    {
        var selected = new MediaSourceInfo { Path = "https://provider.example.invalid/stream.mkv" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareSelection(selected,
            "GetVideoStream", _ => Task.FromException<MediaSourceInfo>(
                new InvalidOperationException("fixture probe failure"))));
    }

    private static Task<MediaSourceInfo?> PrepareSelection(MediaSourceInfo? selected, string action,
        Func<MediaSourceInfo, Task<MediaSourceInfo>> prepare)
    {
        var method = typeof(MediaSourceManagerDecorator).GetMethod("PrepareSelectedSourceAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (Task<MediaSourceInfo?>)method!.Invoke(null, [selected, action, prepare])!;
    }
}
