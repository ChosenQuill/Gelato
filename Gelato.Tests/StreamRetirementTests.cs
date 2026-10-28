using System.Reflection;
using System.Text.Json;
using Gelato.Decorators;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Gelato.Tests;

public class StreamRetirementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementSourcesPersistAcrossReloadWithoutRetiringOtherUsersOrItems(bool episode)
    {
        using var fixture = new RepositoryFixture();
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        const string association = "test-current-item";
        var retired = Stream(association, [user], episode);
        var retained = Stream(association, [user, otherUser], episode);
        var shared = Stream(association, [user, otherUser], episode);
        var otherAssociation = Stream("test-other-item", [user], episode);
        var untagged = Stream(association, [user], episode);
        untagged.Tags = [];
        untagged.IsVirtualItem = true;
        var otherUserOnly = Stream(association, [otherUser], episode);
        var ownerless = Stream(association, [], episode);
        retired.Path = Path.Combine(fixture.DirectoryPath, "preserve-media.mkv");
        File.WriteAllText(retired.Path, "fixture media must survive metadata retirement");
        fixture.Seed(retired, retained, shared, otherAssociation, untagged, otherUserOnly, ownerless);

        var candidates = fixture.Reload().GetItemList(new InternalItemsQuery { IsDeadPerson = true })
            .OfType<Video>().ToList();
        Retire(candidates, [retained.Id], association, user, fixture.Repository);

        var reloaded = fixture.Reload();
        Assert.Null(reloaded.RetrieveItem(retired.Id));
        var savedShared = reloaded.RetrieveItem(shared.Id);
        Assert.NotSame(candidates.Single(item => item.Id == shared.Id), savedShared);
        Assert.Equal([otherUser], savedShared.GelatoData<List<Guid>>("userIds"));
        Assert.Equal("keep", savedShared.GelatoData<string>("unrelatedData"));
        Assert.Equal(otherAssociation.ExternalId, reloaded.RetrieveItem(otherAssociation.Id).ExternalId);
        Assert.Equal(untagged.ExternalId, reloaded.RetrieveItem(untagged.Id).ExternalId);
        Assert.Equal(otherUserOnly.ExternalId, reloaded.RetrieveItem(otherUserOnly.Id).ExternalId);
        Assert.Equal(ownerless.ExternalId, reloaded.RetrieveItem(ownerless.Id).ExternalId);
        Assert.Equal(retained.ExternalId, reloaded.RetrieveItem(retained.Id).ExternalId);
        Assert.Equal("fixture media must survive metadata retirement", File.ReadAllText(retired.Path));

        var selectable = reloaded.GetItemList(new InternalItemsQuery { IsDeadPerson = true })
            .OfType<Video>()
            .Where(item => item.HasStreamTag()
                && item.ProviderIds["Stremio"] == association
                && (item.GelatoData<List<Guid>>("userIds")?.Contains(user) ?? false))
            .ToList();
        Assert.Equal(retained.Id, Assert.Single(selectable).Id);
        Assert.Equal([retired.Id], Assert.Single(fixture.Backend.DeletedBatches));
    }

    [Fact]
    public void SharedSourceRemovalIsSavedWithoutAnEmptyDeleteCall()
    {
        using var fixture = new RepositoryFixture();
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var shared = Stream("test-item", [user, otherUser]);
        fixture.Seed(shared);

        Retire([Assert.IsAssignableFrom<Video>(fixture.Repository.RetrieveItem(shared.Id))],
            [], "test-item", user, fixture.Repository);

        Assert.Equal([otherUser], fixture.Reload().RetrieveItem(shared.Id).GelatoData<List<Guid>>("userIds"));
        Assert.Empty(fixture.Backend.DeletedBatches);
        Assert.Equal(1, fixture.Backend.SaveCalls);
    }

    [Fact]
    public void EmptyCandidatesDoNotCallRepositoryDeletionOrSave()
    {
        using var fixture = new RepositoryFixture();
        Retire([], [], "test-item", Guid.NewGuid(), fixture.Repository);
        Assert.Empty(fixture.Backend.DeletedBatches);
        Assert.Equal(0, fixture.Backend.SaveCalls);
    }

    [Fact]
    public void RepositoryDeletionFailureIsPropagatedWithoutPersistingPartialRetirement()
    {
        using var fixture = new RepositoryFixture();
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var retired = Stream("test-item", [user]);
        var shared = Stream("test-item", [user, otherUser]);
        fixture.Seed(retired, shared);
        fixture.Backend.RejectDeletion = true;
        var candidates = fixture.Reload().GetItemList(new InternalItemsQuery { IsDeadPerson = true })
            .OfType<Video>().ToList();

        var exception = Assert.Throws<TargetInvocationException>(() =>
            Retire(candidates, [], "test-item", user, fixture.Repository));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
        var reloaded = fixture.Reload();
        Assert.Equal([user], reloaded.RetrieveItem(retired.Id).GelatoData<List<Guid>>("userIds"));
        Assert.Equal([user, otherUser], reloaded.RetrieveItem(shared.Id).GelatoData<List<Guid>>("userIds"));
        Assert.Equal(0, fixture.Backend.SaveCalls);
    }

    private static Video Stream(string association, List<Guid> users, bool episode = false)
    {
        Video item = episode ? new Episode() : new Movie();
        item.Id = Guid.NewGuid();
        item.Path = $"https://provider.example.invalid/{item.Id:N}";
        item.Tags = [GelatoManager.StreamTag];
        item.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Stremio"] = association,
            ["Imdb"] = "test-shared-provider-id",
        };
        item.SetGelatoData("userIds", users);
        item.SetGelatoData("guid", Guid.NewGuid());
        item.SetGelatoData("unrelatedData", "keep");
        return item;
    }

    private static void Retire(
        IEnumerable<Video> candidates,
        HashSet<Guid> currentIds,
        string association,
        Guid userId,
        GelatoItemRepository repository)
    {
        var method = typeof(GelatoManager).GetMethod("RetireStaleStreams", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method!.Invoke(null, [candidates, currentIds, association, userId, repository, CancellationToken.None]);
    }

    private sealed class RepositoryFixture : IDisposable
    {
        private readonly string _storePath;
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "gelato-retirement-" + Guid.NewGuid().ToString("N"));
        public GelatoItemRepository Repository { get; }
        public SnapshotRepository Backend { get; }

        public RepositoryFixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            _storePath = Path.Combine(DirectoryPath, "items.json");
            File.WriteAllText(_storePath, "{}");
            Repository = CreateRepository(out var backend);
            Backend = backend;
        }

        private GelatoItemRepository CreateRepository(out SnapshotRepository backend)
        {
            var inner = DispatchProxy.Create<IItemRepository, SnapshotRepository>();
            backend = (SnapshotRepository)(object)inner;
            backend.StorePath = _storePath;
            return new GelatoItemRepository(inner, new HttpContextAccessor());
        }

        public GelatoItemRepository Reload() => CreateRepository(out _);

        public void Seed(params Video[] items)
        {
            Repository.SaveItems(items, CancellationToken.None);
            Backend.SaveCalls = 0;
        }

        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }

    // Serialize every repository operation so reloads cannot observe mutated aliases.
    public class SnapshotRepository : DispatchProxy
    {
        public string StorePath { get; set; } = "";
        public int SaveCalls { get; set; }
        public bool RejectDeletion { get; set; }
        public List<Guid[]> DeletedBatches { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var rows = JsonSerializer.Deserialize<Dictionary<Guid, PersistedVideo>>(File.ReadAllText(StorePath))!;
            switch (targetMethod?.Name)
            {
                case nameof(IItemRepository.SaveItems):
                    SaveCalls++;
                    foreach (var item in (IReadOnlyList<BaseItem>)args![0]!)
                    {
                        rows[item.Id] = new PersistedVideo(item.Id, item is Episode, item.Path,
                            item.Tags, item.ProviderIds, item.ExternalId, item.IsVirtualItem);
                    }
                    File.WriteAllText(StorePath, JsonSerializer.Serialize(rows));
                    return null;
                case nameof(IItemRepository.DeleteItem):
                    var ids = (IReadOnlyList<Guid>)args![0]!;
                    if (ids.Count == 0) throw new InvalidOperationException("Empty deletion is forbidden");
                    if (RejectDeletion) throw new InvalidOperationException("Fixture deletion rejected");
                    DeletedBatches.Add(ids.ToArray());
                    foreach (var id in ids) rows.Remove(id);
                    File.WriteAllText(StorePath, JsonSerializer.Serialize(rows));
                    return null;
                case nameof(IItemRepository.RetrieveItem):
                    return rows.TryGetValue((Guid)args![0]!, out var persisted) ? persisted.Reload() : null;
                case nameof(IItemRepository.GetItemList):
                    return rows.Values.Select(snapshot => (BaseItem)snapshot.Reload()).ToArray();
                default:
                    throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }

    public record PersistedVideo(Guid Id, bool IsEpisode, string Path, string[] Tags,
        Dictionary<string, string> ProviderIds, string? ExternalId, bool Virtual)
    {
        public Video Reload()
        {
            Video item = IsEpisode ? new Episode() : new Movie();
            item.Id = Id;
            item.Path = Path;
            item.Tags = Tags;
            item.ProviderIds = ProviderIds;
            item.ExternalId = ExternalId;
            item.IsVirtualItem = Virtual;
            return item;
        }
    }
}
