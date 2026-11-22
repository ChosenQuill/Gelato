using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;

namespace Gelato.Decorators;

// Marks Gelato sources so a mixed library DTO can mask them while preserving
// native Jellyfin sources. The server creates its own copy for each request.
internal sealed class GelatoMediaSourceInfo : MediaSourceInfo
{
    internal const string ClientPath = "/stub";

    internal static bool IsPlaceholderPath(string? path) =>
        path?.StartsWith("gelato://", StringComparison.OrdinalIgnoreCase) == true
        || path?.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase) == true;

    internal static void RouteThroughJellyfin(MediaSourceInfo source)
    {
        source.Path = ClientPath;
        source.IsRemote = false;
        source.Protocol = MediaProtocol.File;
    }
}
