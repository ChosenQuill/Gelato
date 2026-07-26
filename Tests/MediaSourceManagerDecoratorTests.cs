using Gelato.Config;
using Gelato.Decorators;
using Gelato.Providers;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Gelato.Tests;

public sealed class MediaSourceManagerDecoratorTests
{
    [Fact]
    public async Task MixedDisabledLocalMovieDelegatesBeforeGelatoSourceResolution()
    {
        var item = LocalMovie();
        var user = new User("test", "default", "default") { Id = Guid.NewGuid() };
        var expected = new List<MediaSourceInfo> { LocalSource(item) };
        var inner = Substitute.For<IMediaSourceManager>();
        inner
            .GetPlaybackMediaSources(item, user, true, false, CancellationToken.None)
            .Returns(Task.FromResult<IReadOnlyList<MediaSourceInfo>>(expected));

        var managerCreated = false;
        var harness = CreateHarness(
            inner,
            new PluginConfiguration { EnableMixed = false },
            new Lazy<GelatoManager>(() =>
            {
                managerCreated = true;
                throw new InvalidOperationException("Gelato manager should not be resolved");
            })
        );

        var actual = await harness.Decorator.GetPlaybackMediaSources(
            item,
            user,
            true,
            false,
            CancellationToken.None
        );

        Assert.Same(expected, actual);
        Assert.False(managerCreated);
        await inner
            .Received(1)
            .GetPlaybackMediaSources(item, user, true, false, CancellationToken.None);
        inner
            .DidNotReceive()
            .GetStaticMediaSources(item, Arg.Any<bool>(), Arg.Any<User?>());
        harness.RepositoryInner
            .DidNotReceiveWithAnyArgs()
            .GetItemList(default!);
        Assert.Empty(harness.SegmentManager.ReceivedCalls());
    }

    [Fact]
    public async Task MixedEnabledLocalSelectionDelegatesEvenWithStremioProviderId()
    {
        var item = LocalMovie();
        item.ProviderIds["Stremio"] = "tt1234567";
        var originalPath = item.Path;
        var user = new User("test", "default", "default") { Id = Guid.NewGuid() };
        var localSource = LocalSource(item);
        var expected = new List<MediaSourceInfo> { LocalSource(item) };
        var inner = Substitute.For<IMediaSourceManager>();
        inner
            .GetStaticMediaSources(item, false, user)
            .Returns(new List<MediaSourceInfo> { localSource });
        inner
            .GetPlaybackMediaSources(item, user, true, false, CancellationToken.None)
            .Returns(Task.FromResult<IReadOnlyList<MediaSourceInfo>>(expected));

        var harness = CreateHarness(
            inner,
            new PluginConfiguration { EnableMixed = true }
        );
        harness.RepositoryInner
            .GetItemList(Arg.Any<InternalItemsQuery>())
            .Returns(Array.Empty<BaseItem>());

        var actual = await harness.Decorator.GetPlaybackMediaSources(
            item,
            user,
            true,
            false,
            CancellationToken.None
        );

        Assert.True(item.IsGelato());
        Assert.False(item.HasStreamTag());
        Assert.Equal(originalPath, item.Path);
        Assert.Same(expected, actual);
        inner
            .Received(1)
            .GetStaticMediaSources(item, false, user);
        await inner
            .Received(1)
            .GetPlaybackMediaSources(item, user, true, false, CancellationToken.None);
        Assert.Empty(harness.SegmentManager.ReceivedCalls());
    }

    [Fact]
    public async Task GelatoRemoteSelectionRemainsOnGelatoPlaybackPath()
    {
        var user = new User("test", "default", "default") { Id = Guid.NewGuid() };
        var item = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Gelato movie",
            Path = "gelato://stub/tt1234567",
            RunTimeTicks = TimeSpan.FromMinutes(90).Ticks,
        };
        item.ProviderIds["Stremio"] = "tt1234567";
        var remote = new Movie
        {
            Id = Guid.NewGuid(),
            Name = item.Name,
            Path = "https://example.test/video.mkv",
            RunTimeTicks = item.RunTimeTicks,
            Tags = [GelatoManager.StreamTag],
        };
        remote.ProviderIds["Stremio"] = "tt1234567";
        remote.SetGelatoData("userIds", new List<Guid> { user.Id });
        remote.SetGelatoData("name", "Remote source");

        var inner = Substitute.For<IMediaSourceManager>();
        inner
            .GetStaticMediaSources(item, false, user)
            .Returns(new List<MediaSourceInfo>
            {
                new()
                {
                    Id = item.Id.ToString("N"),
                    ETag = item.Id.ToString("N"),
                    Path = item.Path,
                    Protocol = MediaProtocol.File,
                },
            });
        inner
            .GetMediaStreams(remote.Id)
            .Returns(new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                },
            });
        inner.GetMediaAttachments(remote.Id).Returns(Array.Empty<MediaAttachment>());

        var harness = CreateHarness(
            inner,
            new PluginConfiguration { EnableMixed = false }
        );
        harness.RepositoryInner
            .GetItemList(Arg.Any<InternalItemsQuery>())
            .Returns(new List<BaseItem> { remote });
        harness.LibraryManager.GetItemById(remote.Id).Returns(remote);

        var actual = await harness.Decorator.GetPlaybackMediaSources(
            item,
            user,
            allowMediaProbe: true,
            enablePathSubstitution: false,
            CancellationToken.None
        );

        var selected = Assert.Single(actual);
        Assert.Equal(remote.Path, selected.Path);
        Assert.Equal(remote.Id.ToString("N"), selected.ETag);
        await inner
            .DidNotReceiveWithAnyArgs()
            .GetPlaybackMediaSources(default!, default!, default, default, default);
        Assert.Empty(harness.SegmentManager.ReceivedCalls());
    }

    private static Harness CreateHarness(
        IMediaSourceManager inner,
        PluginConfiguration configuration,
        Lazy<GelatoManager>? manager = null
    )
    {
        var http = Substitute.For<IHttpContextAccessor>();
        http.HttpContext.Returns(new DefaultHttpContext());
        var repositoryInner = Substitute.For<IItemRepository>();
        var repository = new GelatoItemRepository(repositoryInner, http);
        var libraryManager = Substitute.For<ILibraryManager>();
        var directoryService = Substitute.For<IDirectoryService>();
        var serverConfiguration = Substitute.For<IServerConfigurationManager>();
        var segmentManager = Substitute.For<IMediaSegmentManager>();

        manager ??= new Lazy<GelatoManager>(() =>
            new GelatoManager(
                NullLoggerFactory.Instance,
                Substitute.For<IProviderManager>(),
                repository,
                Substitute.For<IFileSystem>(),
                Substitute.For<IMemoryCache>(),
                serverConfiguration,
                libraryManager,
                directoryService,
                Substitute.For<IApplicationPaths>()
            )
        );

        var decorator = new MediaSourceManagerDecorator(
            inner,
            libraryManager,
            NullLogger<MediaSourceManagerDecorator>.Instance,
            http,
            repository,
            directoryService,
            serverConfiguration,
            manager,
            new Lazy<SubtitleProvider>(() =>
                throw new InvalidOperationException("Subtitle provider should not be resolved")
            ),
            segmentManager,
            Array.Empty<ICustomMetadataProvider<Video>>(),
            _ => configuration
        );

        return new Harness(decorator, repositoryInner, segmentManager, libraryManager);
    }

    private static Movie LocalMovie() =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "Local movie",
            Path = "/rd/movie.mkv",
            RunTimeTicks = TimeSpan.FromMinutes(90).Ticks,
        };

    private static MediaSourceInfo LocalSource(Movie item) =>
        new()
        {
            Id = item.Id.ToString("N"),
            Path = item.Path,
            Protocol = MediaProtocol.File,
            RunTimeTicks = item.RunTimeTicks,
            MediaStreams =
            [
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = 0,
                },
            ],
        };

    private sealed record Harness(
        MediaSourceManagerDecorator Decorator,
        IItemRepository RepositoryInner,
        IMediaSegmentManager SegmentManager,
        ILibraryManager LibraryManager
    );
}
