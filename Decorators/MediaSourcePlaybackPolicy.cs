using Jellyfin.Data.Enums;

namespace Gelato.Decorators;

internal static class MediaSourcePlaybackPolicy
{
    internal static bool ShouldHandlePlayback(
        BaseItemKind itemKind,
        bool enableMixed,
        bool isGelatoItem
    ) =>
        itemKind is BaseItemKind.Movie or BaseItemKind.Episode
        && (enableMixed || isGelatoItem);

    internal static bool IsGelatoOwnedItem(bool hasStreamTag, string? path) =>
        hasStreamTag || IsGelatoVirtualPath(path);

    private static bool IsGelatoVirtualPath(string? path)
    {
        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme.Equals("gelato", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("stremio", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldProbe(
        bool allowMediaProbe,
        bool isGelatoStream,
        string? path,
        bool needsProbe
    ) =>
        allowMediaProbe
        && isGelatoStream
        && needsProbe
        && StreamUrlPolicy.IsSupportedRemoteStreamUrl(path);

    internal static bool ShouldStub(bool isGelatoOwnedItem) => isGelatoOwnedItem;
}
