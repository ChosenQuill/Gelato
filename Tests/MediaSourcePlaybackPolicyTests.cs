using Gelato.Decorators;
using Jellyfin.Data.Enums;
using Xunit;

namespace Gelato.Tests;

public sealed class MediaSourcePlaybackPolicyTests
{
    [Fact]
    public void LocalMovieIsDelegatedWhenMixedPlaybackIsDisabled()
    {
        Assert.False(
            MediaSourcePlaybackPolicy.ShouldHandlePlayback(
                BaseItemKind.Movie,
                enableMixed: false,
                isGelatoItem: false
            )
        );
    }

    [Fact]
    public void GelatoMovieIsHandledWhenMixedPlaybackIsDisabled()
    {
        Assert.True(
            MediaSourcePlaybackPolicy.ShouldHandlePlayback(
                BaseItemKind.Movie,
                enableMixed: false,
                isGelatoItem: true
            )
        );
    }

    [Fact]
    public void NonVideoItemsAreAlwaysDelegated()
    {
        Assert.False(
            MediaSourcePlaybackPolicy.ShouldHandlePlayback(
                BaseItemKind.Audio,
                enableMixed: true,
                isGelatoItem: true
            )
        );
    }

    [Theory]
    [InlineData("https://example.test/video.mkv")]
    [InlineData("http://example.test/video.mkv")]
    [InlineData("rtsp://example.test/video")]
    [InlineData("rtp://239.0.0.1:5004")]
    public void SupportedRemoteProtocolsCanBeProbed(string path)
    {
        Assert.True(StreamUrlPolicy.IsSupportedRemoteStreamUrl(path));
    }

    [Theory]
    [InlineData("/rd/movie.mkv")]
    [InlineData("relative/movie.mkv")]
    [InlineData("file:///rd/movie.mkv")]
    [InlineData("gelato://stub/item")]
    [InlineData("stremio://item")]
    [InlineData("")]
    [InlineData(null)]
    public void LocalVirtualAndMissingPathsCannotBeProbed(string? path)
    {
        Assert.False(StreamUrlPolicy.IsSupportedRemoteStreamUrl(path));
    }

    [Fact]
    public void ProviderIdsDoNotMakeALocalFileGelatoOwned()
    {
        Assert.False(
            MediaSourcePlaybackPolicy.IsGelatoOwnedItem(
                hasStreamTag: false,
                "/rd/movie.mkv"
            )
        );
        Assert.True(
            MediaSourcePlaybackPolicy.IsGelatoOwnedItem(
                hasStreamTag: true,
                "https://example.test/video.mkv"
            )
        );
        Assert.True(
            MediaSourcePlaybackPolicy.IsGelatoOwnedItem(
                hasStreamTag: false,
                "gelato://stub/item"
            )
        );
    }

    [Fact]
    public void ProbeRequiresPermissionGelatoOwnershipAndSupportedRemoteUrl()
    {
        Assert.True(
            MediaSourcePlaybackPolicy.ShouldProbe(
                allowMediaProbe: true,
                isGelatoStream: true,
                "https://example.test/video.mkv",
                needsProbe: true
            )
        );
        Assert.False(
            MediaSourcePlaybackPolicy.ShouldProbe(
                allowMediaProbe: false,
                isGelatoStream: true,
                "https://example.test/video.mkv",
                needsProbe: true
            )
        );
        Assert.False(
            MediaSourcePlaybackPolicy.ShouldProbe(
                allowMediaProbe: true,
                isGelatoStream: false,
                "/rd/movie.mkv",
                needsProbe: true
            )
        );
        Assert.False(
            MediaSourcePlaybackPolicy.ShouldProbe(
                allowMediaProbe: true,
                isGelatoStream: true,
                "https://example.test/video.mkv",
                needsProbe: false
            )
        );
    }

    [Fact]
    public void OnlyGelatoOwnedSourcesAreStubbed()
    {
        Assert.True(
            MediaSourcePlaybackPolicy.ShouldStub(isGelatoOwnedItem: true)
        );
        Assert.False(MediaSourcePlaybackPolicy.ShouldStub(isGelatoOwnedItem: false));
    }
}
