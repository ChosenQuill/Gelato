using System.Reflection;
using Gelato.Decorators;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Gelato.Tests;

public class MediaSourceDtoTests
{
    [Fact]
    public void LibraryDetailsMaskOwnedStreamPathsAndKeepNativeSourcesIntact()
    {
        var sourceType = typeof(DtoServiceDecorator).Assembly.GetType("Gelato.Decorators.GelatoMediaSourceInfo");
        Assert.NotNull(sourceType);
        var owned = Assert.IsAssignableFrom<MediaSourceInfo>(Activator.CreateInstance(sourceType!, nonPublic: true));
        owned.Id = Guid.NewGuid().ToString("N");
        owned.ETag = Guid.NewGuid().ToString("N");
        owned.Path = "https://provider.example.invalid/stream.mkv";
        owned.Container = "mkv";
        owned.Protocol = MediaProtocol.Http;
        owned.IsRemote = true;
        var id = owned.Id;
        var etag = owned.ETag;

        var local = new MediaSourceInfo { Path = "/media/movie.mkv", Protocol = MediaProtocol.File };
        var nativeHttp = new MediaSourceInfo
        {
            Path = "https://native.example.invalid/movie.mp4",
            Protocol = MediaProtocol.Http,
        };
        var placeholder = new MediaSourceInfo { Path = "gelato://stub/movie", Protocol = MediaProtocol.Http };
        var dto = new BaseItemDto
        {
            Path = local.Path,
            MediaSources = [owned, local, nativeHttp, placeholder],
        };

        MaskStreamPaths(dto, false);

        Assert.Equal(local.Path, dto.Path);
        Assert.Equal("/stub", owned.Path);
        Assert.Equal(MediaProtocol.File, owned.Protocol);
        Assert.False(owned.IsRemote);
        Assert.Equal(id, owned.Id);
        Assert.Equal(etag, owned.ETag);
        Assert.Equal("mkv", owned.Container);
        Assert.Equal("/media/movie.mkv", local.Path);
        Assert.Equal("https://native.example.invalid/movie.mp4", nativeHttp.Path);
        Assert.Equal(MediaProtocol.Http, nativeHttp.Protocol);
        Assert.Equal("/stub", placeholder.Path);
    }

    [Fact]
    public void IndividualStreamDetailsMaskTheirPathWithoutMediaSources()
    {
        var dto = new BaseItemDto { Path = "https://provider.example.invalid/stream.mkv" };

        MaskStreamPaths(dto, true);

        Assert.Equal("/stub", dto.Path);
        Assert.Null(dto.MediaSources);
    }

    private static void MaskStreamPaths(BaseItemDto dto, bool isGelatoStream)
    {
        var method = typeof(DtoServiceDecorator).GetMethod(
            "MaskStreamPaths",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method.Invoke(null, [dto, isGelatoStream]);
    }
}
