using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities; // User
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;

namespace Gelato.Decorators;

public sealed class DtoServiceDecorator(IDtoService inner, Lazy<GelatoManager> manager, IHttpContextAccessor http)
    : IDtoService
{
    private readonly Lazy<GelatoManager> _manager = manager;
    private readonly IHttpContextAccessor _http = http;

    public double? GetPrimaryImageAspectRatio(BaseItem item) =>
        inner.GetPrimaryImageAspectRatio(item);

    public BaseItemDto GetBaseItemDto(
        BaseItem item,
        DtoOptions options,
        User? user = null,
        BaseItem? owner = null
    )
    {
        var dto = inner.GetBaseItemDto(item, options, user, owner);
        Patch(dto, item, _http.HttpContext?.IsApiListing() == true, user);
        return dto;
    }

    public IReadOnlyList<BaseItemDto> GetBaseItemDtos(
        IReadOnlyList<BaseItem> items,
        DtoOptions options,
        User? user = null,
        BaseItem? owner = null
    )
    {
        // im going to hell for this
        var item = items.FirstOrDefault();

        if (item != null && item.GetBaseItemKind() == BaseItemKind.BoxSet)
        {
            options.EnableUserData = false;
        }

        var list = inner.GetBaseItemDtos(items, options, user, owner);
        var itemsById = items.ToLookup(item => item.Id);
        foreach (var itemDto in list)
        {
            Patch(itemDto, itemsById[itemDto.Id].FirstOrDefault(), true, user);
        }
        return list;
    }

    public BaseItemDto GetItemByNameDto(
        BaseItem item,
        DtoOptions options,
        List<BaseItem>? taggedItems,
        User? user = null
    )
    {
        var dto = inner.GetItemByNameDto(item, options, taggedItems, user);
        Patch(dto, item, _http.HttpContext?.IsApiListing() == true, user);
        return dto;
    }

    static bool IsGelato(BaseItemDto dto)
    {
        return dto.LocationType == LocationType.Remote
            && (
                dto.Type == BaseItemKind.Movie
                || dto.Type == BaseItemKind.Episode
                || dto.Type == BaseItemKind.Series
                || dto.Type == BaseItemKind.Season
            );
    }

    private void Patch(BaseItemDto dto, BaseItem? item, bool isList, User? user)
    {
        var manager = _manager.Value;
        if (item is not null && user is not null && IsGelato(dto) && manager.CanDelete(item, user))
        {
            dto.CanDelete = true;
        }

        if (IsGelato(dto))
        {
            dto.CanDownload = true;
            if (!isList && dto.MediaSources?.Length == 1 && dto.Path is not null
                && GelatoMediaSourceInfo.IsPlaceholderPath(dto.MediaSources[0].Path))
            {
                dto.LocationType = LocationType.Virtual;
                dto.Path = null;
                dto.CanDownload = false;
            }
        }

        MaskStreamPaths(dto, item?.HasStreamTag() == true);
    }

    private static void MaskStreamPaths(BaseItemDto dto, bool isGelatoStream)
    {
        if (isGelatoStream && dto.Path?.IsUrl() == true)
        {
            dto.Path = GelatoMediaSourceInfo.ClientPath;
        }

        foreach (var source in dto.MediaSources ?? [])
        {
            if ((source is GelatoMediaSourceInfo && source.Path?.IsUrl() == true)
                || GelatoMediaSourceInfo.IsPlaceholderPath(source.Path))
            {
                GelatoMediaSourceInfo.RouteThroughJellyfin(source);
            }
        }
    }
}
