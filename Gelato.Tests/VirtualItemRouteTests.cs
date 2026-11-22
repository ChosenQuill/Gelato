using System.Reflection;
using System.Security.Claims;
using Gelato.Decorators;
using Gelato.Filters;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
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
        var viewerId = Guid.NewGuid();
        var context = CreateContext(action, searchId, viewerId);
        context.HttpContext.User = Viewer(viewerId);
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
        var viewerId = Guid.NewGuid();
        var context = CreateContext("GetVideoStream", searchId, viewerId);
        context.HttpContext.User = Viewer(viewerId);
        context.ActionArguments["mediaSourceId"] = sourceId;
        context.ReplaceGuid(searchId, itemId);

        await CheckSourceArgumentAsync(context, itemId, sourceId);
    }

    [Fact]
    public async Task PostedPlaybackInfoWithTheDefaultSearchSourceUsesTheCanonicalId()
    {
        var searchId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var context = CreateContext("GetPostedPlaybackInfo", searchId, viewerId);
        context.HttpContext.User = Viewer(viewerId);
        context.ActionArguments["playbackInfoDto"] = new { MediaSourceId = searchId.ToString("N") };
        context.ReplaceGuid(searchId, itemId);

        await CheckSourceArgumentAsync(context, itemId, itemId.ToString("N"));
    }

    [Theory]
    [InlineData("GetVideoStream")]
    [InlineData("GetVideoStreamByContainer")]
    [InlineData("GetMasterHlsVideoPlaylist")]
    [InlineData("GetVariantHlsVideoPlaylist")]
    [InlineData("GetHlsVideoSegment")]
    public async Task NativeRequestsWithOnlyACallerSuppliedUserDelegateWithoutPreparingMedia(
        string action)
    {
        var requestedId = Guid.NewGuid();
        var suppliedUser = Guid.NewGuid();
        var context = CreateContext(action, requestedId, suppliedUser);
        context.HttpContext.Request.QueryString = new QueryString($"?userId={suppliedUser:N}");
        Assert.True(context.TryGetUserId(out _));

        Assert.Equal(Guid.Empty, ResolveMediaSourceUserId(context));
        await AssertDelegatesUnchanged(context, requestedId);

        var unverifiedClaim = new Claim("Jellyfin-UserId", suppliedUser.ToString("N"));
        context.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([unverifiedClaim]));
        Assert.Equal(Guid.Empty, ResolveMediaSourceUserId(context));
        await AssertDelegatesUnchanged(context, requestedId);

        context.HttpContext.User = Viewer(Guid.Empty);
        Assert.Equal(Guid.Empty, ResolveMediaSourceUserId(context));
        await AssertDelegatesUnchanged(context, requestedId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticatedViewerUsesTheirOwnVisibleAlias(bool isVisible)
    {
        var callerSupplied = Guid.NewGuid();
        var searchId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var viewer = new User("fixture", "fixture-auth", "fixture-reset");
        var context = CreateContext("GetVariantHlsVideoPlaylist", searchId, callerSupplied);
        context.HttpContext.Request.QueryString = new QueryString($"?userId={callerSupplied:N}");
        context.HttpContext.User = Viewer(viewer.Id);
        Assert.Equal(viewer.Id, ResolveMediaSourceUserId(context));

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var manager = new GelatoManager(
            NullLoggerFactory.Instance, null!, null!, null!, cache, null!, null!, null!, null!);
        typeof(GelatoManager).GetMethod("RememberInsertedId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, [searchId, itemId, viewer.Id]);
        var userManager = Fake<IUserManager>((method, arguments) =>
        {
            Assert.Equal(nameof(IUserManager.GetUserById), method.Name);
            Assert.Equal(viewer.Id, arguments[0]);
            return viewer;
        });
        var library = Fake<ILibraryManager>((method, arguments) =>
        {
            Assert.Equal(nameof(ILibraryManager.GetItemById), method.Name);
            Assert.Equal(itemId, arguments[0]);
            Assert.Same(viewer, arguments[1]);
            return isVisible ? new Movie { Id = itemId } : null;
        });

        var filter = new InsertActionFilter(manager, userManager, library, NullLogger<InsertActionFilter>.Instance);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            Assert.Equal(isVisible ? itemId : searchId, context.ActionArguments["itemId"]);
            return Task.FromResult(new ActionExecutedContext(context, [], context.Controller));
        });
        Assert.True(nextCalled);
    }

    [Fact]
    public void ProtectedDetailKeepsTheExistingRoutedUserContract()
    {
        var userId = Guid.NewGuid();
        var context = CreateContext("GetItem", Guid.NewGuid(), userId);

        Assert.Equal(userId, ResolveMediaSourceUserId(context));
    }

    private static ClaimsPrincipal Viewer(Guid id) => new(new ClaimsIdentity(
        [new Claim("Jellyfin-UserId", id.ToString("N"))], "CustomAuthentication"));

    private static Guid ResolveMediaSourceUserId(ActionExecutingContext context)
    {
        var method = typeof(MediaSourceManagerDecorator).GetMethod(
            "ResolveMediaSourceUserId", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<Guid>(method.Invoke(null, [context.HttpContext, null]));
    }

    private static async Task AssertDelegatesUnchanged(ActionExecutingContext context, Guid itemId)
    {
        var filter = new InsertActionFilter(null!, null!, null!, NullLogger<InsertActionFilter>.Instance);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            Assert.Equal(itemId, context.ActionArguments["itemId"]);
            return Task.FromResult(new ActionExecutedContext(context, [], context.Controller));
        });
        Assert.True(nextCalled);
    }

    private static T Fake<T>(Func<MethodInfo, object?[], object?> invoke) where T : class
    {
        var result = DispatchProxy.Create<T, FixtureProxy>();
        ((FixtureProxy)(object)result).Handler = invoke;
        return result;
    }

    public class FixtureProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments) =>
            Handler(method!, arguments ?? []);
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
