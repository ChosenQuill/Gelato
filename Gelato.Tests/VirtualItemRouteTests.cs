using Jellyfin.Data.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
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

        itemDetail.ReplaceGuid(materializedItemId);

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
