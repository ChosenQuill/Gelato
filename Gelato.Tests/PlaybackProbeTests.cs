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
using MediaBrowser.Model.Configuration;
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
    public async Task ProductionProbeResolvesRegisteredProviderLazilyAndProbesCanonicalHttpItem()
    {
        var owner = Owner();
        var canonical = Snapshot.From(owner);
        var options = new LibraryOptions();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var provider = new TestProbeProvider((item, refresh, token) =>
        {
            calls++;
            Assert.Same(owner, item);
            Assert.Equal(canonical, Snapshot.From(item));
            Assert.True(refresh.EnableRemoteContentProbe);
            Assert.Equal(cancellation.Token, token);
            item.Container = "mkv";
            return Task.FromResult(ItemUpdateType.MetadataEdit);
        });
        var library = DispatchProxy.Create<ILibraryManager, LibraryOptionsDependency>();
        ((LibraryOptionsDependency)(object)library).Options = options;
        var providers = DispatchProxy.Create<IProviderManager, ProbeProviderDependency>();
        var lookup = (ProbeProviderDependency)(object)providers;
        lookup.Owner = owner;
        lookup.Options = options;
        lookup.Provider = provider;
        var lazy = new Lazy<IProviderManager>(() => providers);
        var decorator = CreateDecorator(library, lazy);

        Assert.False(lazy.IsValueCreated);
        await ProductionProbe(decorator, owner, cancellation.Token);

        Assert.True(lazy.IsValueCreated);
        Assert.Equal(1, lookup.Calls);
        Assert.Equal(1, calls);
        Assert.Equal(canonical, Snapshot.From(owner));
        Assert.Equal("mkv", owner.Container);
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

    private static async Task ProductionProbe(MediaSourceManagerDecorator decorator, Video owner,
        CancellationToken token)
    {
        var method = typeof(MediaSourceManagerDecorator).GetMethod("ProbeStreamAsync",
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            [typeof(Video), typeof(CancellationToken)], null);
        Assert.NotNull(method);
        await (Task)method!.Invoke(decorator, [owner, token])!;
    }

    private static MediaSourceManagerDecorator CreateDecorator(ILibraryManager? library = null,
        Lazy<IProviderManager>? providers = null) => new(
        Unused<IMediaSourceManager>(), library ?? Unused<ILibraryManager>(),
        NullLogger<MediaSourceManagerDecorator>.Instance, new HttpContextAccessor(),
        new GelatoItemRepository(Unused<IItemRepository>(), new HttpContextAccessor()),
        Unused<IDirectoryService>(), Unused<IServerConfigurationManager>(),
        new Lazy<GelatoManager>(() => throw new InvalidOperationException("Unexpected manager access")),
        new Lazy<SubtitleProvider>(() => throw new InvalidOperationException("Unexpected subtitle access")),
        Unused<IMediaSegmentManager>(),
        providers ?? new Lazy<IProviderManager>(() => throw new InvalidOperationException("Unexpected provider access")));

    private static T Unused<T>() where T : class => DispatchProxy.Create<T, UnusedDependency>();

    public class UnusedDependency : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected dependency call: {method?.Name}");
    }

    public class LibraryOptionsDependency : DispatchProxy
    {
        public LibraryOptions Options { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method?.Name == nameof(ILibraryManager.GetLibraryOptions)
                ? Options : throw new InvalidOperationException("Unexpected library operation");
    }

    public class ProbeProviderDependency : DispatchProxy
    {
        public Video Owner { get; set; } = null!;
        public LibraryOptions Options { get; set; } = null!;
        public ICustomMetadataProvider<Video> Provider { get; set; } = null!;
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Assert.Equal(nameof(IProviderManager.GetMetadataProviders), method?.Name);
            Assert.Equal([typeof(Video)], method!.GetGenericArguments());
            Assert.Equal(2, args!.Length);
            Assert.Same(Owner, args[0]);
            Assert.Same(Options, args[1]);
            Calls++;
            return new IMetadataProvider<Video>[] { Provider };
        }
    }

    private sealed class TestProbeProvider(
        Func<Video, MetadataRefreshOptions, CancellationToken, Task<ItemUpdateType>> fetch)
        : ICustomMetadataProvider<Video>
    {
        public string Name => "Probe Provider";
        public Task<ItemUpdateType> FetchAsync(Video item, MetadataRefreshOptions options,
            CancellationToken cancellationToken) => fetch(item, options, cancellationToken);
    }

    public record Snapshot(string Path, bool IsShortcut, string? ShortcutPath, DateTime DateModified)
    {
        public static Snapshot From(Video item) =>
            new(item.Path, item.IsShortcut, item.ShortcutPath, item.DateModified);
    }
}
