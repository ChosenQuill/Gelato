namespace Gelato;

internal static class StreamUrlPolicy
{
    internal static bool IsSupportedRemoteStreamUrl(string? path)
    {
        if (
            !Uri.TryCreate(path, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.Host)
        )
        {
            return false;
        }

        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("rtp", StringComparison.OrdinalIgnoreCase);
    }
}
