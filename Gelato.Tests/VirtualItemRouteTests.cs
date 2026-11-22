using Gelato.Filters;
using Jellyfin.Data.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Gelato.Tests;

public class VirtualItemRouteTests
{
    [Fact]
    public void SearchResultDetailAndPlaybackRoutesResolveTheMaterializedItemId()
    {
        var searchMeta = new StremioMeta
        {
            Id = "tt0938283",
            ImdbId = "tt0938283",
            Type = StremioMediaType.Movie,
        };
        var virtualItemId = new StremioUri(
            searchMeta.Type,
            searchMeta.ImdbId ?? searchMeta.Id
        ).ToGuid();
        var userId = Guid.NewGuid();
        var materializedItemId = Guid.NewGuid();

        var itemDetail = CreateContext("GetItem", virtualItemId, userId);

        Assert.True(itemDetail.IsInsertableAction());
        Assert.True(itemDetail.TryGetRouteGuid(out var detailVirtualId));
        Assert.Equal(virtualItemId, detailVirtualId);
        Assert.True(itemDetail.TryGetUserId(out var detailUserId));
        Assert.Equal(userId, detailUserId);

        itemDetail.ReplaceGuid(virtualItemId, materializedItemId);

        Assert.True(itemDetail.TryGetRouteGuid(out var resolvedDetailId));
        Assert.Equal(materializedItemId, resolvedDetailId);
        Assert.Equal(materializedItemId, itemDetail.ActionArguments["itemId"]);

        foreach (var actionName in new[] { "GetPlaybackInfo", "GetVideoStream" })
        {
            var playbackOrHls = CreateContext(actionName, materializedItemId, userId);

            Assert.True(playbackOrHls.IsInsertableAction());
            Assert.True(playbackOrHls.TryGetRouteGuid(out var resolvedPlaybackId));
            Assert.Equal(materializedItemId, resolvedPlaybackId);
        }
    }

    [Fact]
    public void MaterializedSearchAliasesAreScopedToTheViewer()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var manager = new GelatoManager(
            NullLoggerFactory.Instance, null!, null!, null!, cache, null!, null!, null!, null!);
        var remember = typeof(GelatoManager).GetMethod(
            "RememberInsertedId", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
        var lookup = typeof(GelatoManager).GetMethod(
            "GetInsertedId", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(remember);
        Assert.NotNull(lookup);
        var searchId = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var aliceItem = Guid.NewGuid();
        var bobItem = Guid.NewGuid();

        remember.Invoke(manager, [searchId, aliceItem, alice]);
        Assert.Equal(aliceItem, lookup.Invoke(manager, [searchId, alice]));
        Assert.Null(lookup.Invoke(manager, [searchId, bob]));

        remember.Invoke(manager, [searchId, bobItem, bob]);
        Assert.Equal(bobItem, lookup.Invoke(manager, [searchId, bob]));
        Assert.Equal(aliceItem, lookup.Invoke(manager, [searchId, alice]));

        manager.ClearCache();
        Assert.Null(lookup.Invoke(manager, [searchId, alice]));
        Assert.Null(lookup.Invoke(manager, [searchId, bob]));
    }

    [Theory]
    [InlineData("GetVideoStream", "HEAD", true)]
    [InlineData("GetVideoStream", "GET", true)]
    [InlineData("GetVideoStreamByContainer", "HEAD", true)]
    [InlineData("GetVideoStreamByContainer", "GET", true)]
    [InlineData("GetMasterHlsVideoPlaylist", "GET", true)]
    [InlineData("GetVariantHlsVideoPlaylist", "GET", false)]
    [InlineData("GetHlsVideoSegment", "GET", false)]
    public async Task NativePlaybackResolvesTheSearchIdAndDefaultSource(
        string action,
        string method,
        bool mayMaterialize)
    {
        var searchId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var context = CreateContext(action, searchId, Guid.NewGuid());
        context.HttpContext.Request.Method = method;
        context.ActionArguments["mediaSourceId"] = searchId.ToString("D");

        Assert.Equal(mayMaterialize, context.IsInsertableAction());
        Assert.Equal(!mayMaterialize, context.IsHlsPlaybackContinuation());
        Assert.True(context.TryGetRouteGuid(out var originalId));
        Assert.Equal(searchId, originalId);
        context.ReplaceGuid(originalId, itemId);

        await CheckSourceArgumentAsync(context, itemId, itemId.ToString("N"));
    }

    [Fact]
    public async Task NativePlaybackPreservesAnExplicitAlternateSource()
    {
        var searchId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var sourceId = Guid.NewGuid().ToString("N");
        var context = CreateContext("GetVideoStream", searchId, Guid.NewGuid());
        context.ActionArguments["mediaSourceId"] = sourceId;
        context.ReplaceGuid(searchId, itemId);

        await CheckSourceArgumentAsync(context, itemId, sourceId);
    }

    [Fact]
    public async Task PostedPlaybackInfoWithTheDefaultSearchSourceUsesTheCanonicalId()
    {
        var searchId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var context = CreateContext("GetPostedPlaybackInfo", searchId, Guid.NewGuid());
        context.ActionArguments["playbackInfoDto"] = new { MediaSourceId = searchId.ToString("N") };
        context.ReplaceGuid(searchId, itemId);

        await CheckSourceArgumentAsync(context, itemId, itemId.ToString("N"));
    }

    private static Task CheckSourceArgumentAsync(
        ActionExecutingContext context,
        Guid itemId,
        string expectedSource)
    {
        return new PlaybackInfoFilter().OnActionExecutionAsync(context, () =>
        {
            Assert.Equal(itemId, context.ActionArguments["itemId"]);
            Assert.Equal(expectedSource, context.ActionArguments["mediaSourceId"]);
            Assert.Equal(expectedSource, context.HttpContext.Items["MediaSourceId"]);
            return Task.FromResult(new ActionExecutedContext(context, [], context.Controller));
        });
    }

    private static ActionExecutingContext CreateContext(
        string actionName,
        Guid itemId,
        Guid userId
    )
    {
        var http = new DefaultHttpContext();
        var action = new ControllerActionDescriptor { ActionName = actionName };
        http.SetEndpoint(
            new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(action),
                actionName
            )
        );

        var routeData = new RouteData();
        routeData.Values["userId"] = userId.ToString();
        routeData.Values["itemId"] = itemId.ToString();
        http.Request.RouteValues["userId"] = userId.ToString();
        http.Request.RouteValues["itemId"] = itemId.ToString();

        return new ActionExecutingContext(
            new ActionContext(http, routeData, action),
            [],
            new Dictionary<string, object?> { ["itemId"] = itemId },
            new object()
        );
    }
}
