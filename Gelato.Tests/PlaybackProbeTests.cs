using System.Reflection;
using System.Text.Json;
using Gelato.Decorators;
using Gelato.Providers;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gelato.Tests;

public class PlaybackProbeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingProbesNeverExposeOrPersistTemporaryShortcutState(bool episode)
    {
        Video owner = episode ? new Episode() : new Movie();
        owner.Id = Guid.NewGuid();
        owner.Path = "https://provider.example.invalid/stream.mkv";
        owner.IsShortcut = false;
        owner.Tags = [GelatoManager.StreamTag];
        owner.DateModified = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        owner.SetGelatoData("filename", $"probe-{owner.Id:N}.mkv");
        var canonical = Snapshot.From(owner);
        var enteredFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var directory = Path.Combine(Path.GetTempPath(), $"gelato-probe-test-{owner.Id:N}");
        Directory.CreateDirectory(directory);
        var snapshots = new List<Snapshot>();

        async Task Fetch(Video item, MetadataRefreshOptions options, CancellationToken token)
        {
            Assert.Same(owner, item);
            Assert.True(options.EnableRemoteContentProbe);
            var first = Interlocked.Increment(ref calls) == 1;
            (first ? enteredFirst : enteredSecond).SetResult();
            await (first ? releaseFirst : releaseSecond).Task.WaitAsync(token);
            item.Container = "mkv";
            item.RunTimeTicks = TimeSpan.FromMinutes(5).Ticks;
        }

        // A concurrent repository writer must not capture a temporary STRM path.
        void SaveAndReload()
        {
            var path = Path.Combine(directory, $"snapshot-{snapshots.Count}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(Snapshot.From(owner)));
            snapshots.Add(JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path))!);
        }

        var decorator = CreateDecorator();
        Task? second = null;
        var first = Probe(decorator, owner, Fetch);
        try
        {
            await enteredFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SaveAndReload();
            second = Probe(decorator, owner, Fetch);
            await enteredSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SaveAndReload();
            releaseFirst.SetResult();
            await first;
            SaveAndReload();
            releaseSecond.SetResult();
            await second;
            SaveAndReload();

            Assert.All(snapshots, snapshot => Assert.Equal(canonical, snapshot));
            Assert.Equal("mkv", owner.Container);
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
            await first;
            if (second is not null) await second;
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ProbeFailurePropagatesWithoutChangingCanonicalFields()
    {
        var owner = Owner();
        var canonical = Snapshot.From(owner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Probe(CreateDecorator(), owner,
            (_, _, _) => Task.FromException(new InvalidOperationException("fixture probe failure"))));

        Assert.Equal(canonical, Snapshot.From(owner));
    }

    [Fact]
    public async Task ProbeCancellationPropagatesWithoutChangingCanonicalFields()
    {
        using var cancellation = new CancellationTokenSource();
        var owner = Owner();
        var canonical = Snapshot.From(owner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Probe(CreateDecorator(), owner,
            (_, _, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            }, cancellation.Token));

        Assert.Equal(canonical, Snapshot.From(owner));
    }

    [Fact]
    public async Task ProbeWithoutAContainerDoesNotReturnPreparedSuccess()
    {
        var owner = Owner();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Probe(CreateDecorator(), owner,
            (_, _, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task UntaggedPrimaryAliasIsNotRewrittenOrSentToTheProbe()
    {
        var owner = Owner();
        owner.Tags = [];
        owner.Path = "gelato://movie/test-item";
        var canonical = Snapshot.From(owner);
        var called = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Probe(CreateDecorator(), owner,
            (_, _, _) =>
            {
                called = true;
                return Task.CompletedTask;
            }));

        Assert.False(called);
        Assert.Equal(canonical, Snapshot.From(owner));
    }

    private static Video Owner() => new Movie
    {
        Id = Guid.NewGuid(),
        Tags = [GelatoManager.StreamTag],
        Path = "https://provider.example.invalid/stream.mkv",
        DateModified = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc),
    };

    private static Task Probe(MediaSourceManagerDecorator decorator, Video owner,
        Func<Video, MetadataRefreshOptions, CancellationToken, Task> fetch,
        CancellationToken token = default)
    {
        var method = typeof(MediaSourceManagerDecorator).GetMethod("ProbeStreamAsync",
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            [typeof(Video), typeof(Func<Video, MetadataRefreshOptions, CancellationToken, Task>),
                typeof(CancellationToken)], null);
        Assert.NotNull(method);
        return (Task)method!.Invoke(decorator, [owner, fetch, token])!;
    }

    private static MediaSourceManagerDecorator CreateDecorator() => new(
        Unused<IMediaSourceManager>(), Unused<ILibraryManager>(),
        NullLogger<MediaSourceManagerDecorator>.Instance, new HttpContextAccessor(),
        new GelatoItemRepository(Unused<IItemRepository>(), new HttpContextAccessor()),
        Unused<IDirectoryService>(), Unused<IServerConfigurationManager>(),
        new Lazy<GelatoManager>(() => throw new InvalidOperationException("Unexpected manager access")),
        new Lazy<SubtitleProvider>(() => throw new InvalidOperationException("Unexpected subtitle access")),
        Unused<IMediaSegmentManager>(),
        new Lazy<IProviderManager>(() => throw new InvalidOperationException("Unexpected provider access")));

    private static T Unused<T>() where T : class => DispatchProxy.Create<T, UnusedDependency>();

    public class UnusedDependency : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected dependency call: {method?.Name}");
    }

    public record Snapshot(string Path, bool IsShortcut, string? ShortcutPath, DateTime DateModified)
    {
        public static Snapshot From(Video item) =>
            new(item.Path, item.IsShortcut, item.ShortcutPath, item.DateModified);
    }
}
