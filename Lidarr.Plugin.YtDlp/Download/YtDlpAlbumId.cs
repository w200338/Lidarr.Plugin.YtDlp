namespace Lidarr.Plugin.YtDlp.Download;

public enum YtDlpAlbumKind
{
    /// <summary>Single YouTube video treated as a one-track album.</summary>
    SingleTrack,
    /// <summary>YouTube playlist treated as an album.</summary>
    Playlist,
    /// <summary>YouTube Music album browse page (music.youtube.com/browse/...).</summary>
    Browse
}

/// <summary>
/// Parsed album-id token. The token format is the contract between the indexer
/// (Phase 4) and the download client (this phase):
///   <c>yt:&lt;videoId&gt;</c>    → single-track album
///   <c>playlist:&lt;id&gt;</c>   → playlist album
///   <c>browse:&lt;id&gt;</c>     → YouTube Music album browse page (fallback when
///                              the entry inspection could not resolve the
///                              canonical playlist id; yt-dlp handles the
///                              browse URL directly)
/// </summary>
public readonly record struct YtDlpAlbumRef(YtDlpAlbumKind Kind, string Id)
{
    public override string ToString() => Kind switch
    {
        YtDlpAlbumKind.SingleTrack => $"yt:{Id}",
        YtDlpAlbumKind.Playlist => $"playlist:{Id}",
        YtDlpAlbumKind.Browse => $"browse:{Id}",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind))
    };
}

public static class YtDlpAlbumId
{
    private const string SingleTrackPrefix = "yt:";
    private const string PlaylistPrefix = "playlist:";
    private const string BrowsePrefix = "browse:";
    private const char PlaylistSeparator = '#';

    public static readonly string SingleTrackUrlBase = "https://www.youtube.com/watch?v=";
    public static readonly string PlaylistUrlBase = "https://www.youtube.com/playlist?list=";
    public static readonly string BrowseUrlBase = "https://music.youtube.com/browse/";

    /// <summary>
    /// Parses a token. Accepts an optional '#0' track index suffix (e.g.
    /// <c>playlist:PL123#3</c>) which the indexer emits for "download one track
    /// of a playlist" results; the index is ignored by the downloader (it
    /// re-downloads the whole playlist, and the download archive dedupes it).
    /// </summary>
    public static bool TryParse(string? token, out YtDlpAlbumRef result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        token = token.Trim();

        var idPart = token;
        if (token.StartsWith(SingleTrackPrefix, StringComparison.Ordinal))
        {
            idPart = token[SingleTrackPrefix.Length..];
            if (TryValidateId(idPart, out var singleId))
            {
                result = new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, singleId);
                return true;
            }

            return false;
        }

        if (token.StartsWith(PlaylistPrefix, StringComparison.Ordinal))
        {
            idPart = token[PlaylistPrefix.Length..];
            var hash = idPart.IndexOf('#');
            if (hash >= 0)
            {
                // "#N" suffix = requested track index; strip, keep as plain playlist.
                var suffix = idPart[(hash + 1)..];
                if (suffix.Length == 0 || !suffix.All(char.IsDigit))
                {
                    return false;
                }

                idPart = idPart[..hash];
            }

            if (TryValidateId(idPart, out var playlistId))
            {
                result = new YtDlpAlbumRef(YtDlpAlbumKind.Playlist, playlistId);
                return true;
            }

            return false;
        }

        if (token.StartsWith(BrowsePrefix, StringComparison.Ordinal))
        {
            idPart = token[BrowsePrefix.Length..];
            if (TryValidateId(idPart, out var browseId))
            {
                result = new YtDlpAlbumRef(YtDlpAlbumKind.Browse, browseId);
                return true;
            }

            return false;
        }

        return false;
    }

    public static string ToUrl(YtDlpAlbumRef album) =>
        album.Kind switch
        {
            YtDlpAlbumKind.SingleTrack => SingleTrackUrlBase + album.Id,
            YtDlpAlbumKind.Playlist => PlaylistUrlBase + album.Id,
            YtDlpAlbumKind.Browse => BrowseUrlBase + album.Id,
            _ => throw new ArgumentOutOfRangeException(nameof(album))
        };

    /// <summary>
    /// Inverse of <see cref="ToUrl"/> for the standard watch/playlist URLs.
    /// </summary>
    public static bool TryFromUrl(string? url, out YtDlpAlbumRef result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (url.StartsWith(SingleTrackUrlBase, StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://youtu.be/", StringComparison.OrdinalIgnoreCase))
        {
            var id = url.StartsWith(SingleTrackUrlBase, StringComparison.OrdinalIgnoreCase)
                ? url[SingleTrackUrlBase.Length..]
                : url["https://youtu.be/".Length..];

            var amp = id.IndexOf('&');
            if (amp >= 0)
            {
                id = id[..amp]; // strip ?v=...&t=... etc.
            }

            // youtu.be links carry no other query; watch links may have ?t=
            var q = id.IndexOf('?');
            if (q >= 0)
            {
                id = id[..q];
            }

            if (TryValidateId(id, out var videoId))
            {
                result = new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, videoId);
                return true;
            }

            return false;
        }

        if (url.StartsWith(PlaylistUrlBase, StringComparison.OrdinalIgnoreCase))
        {
            var id = url[PlaylistUrlBase.Length..];
            var amp = id.IndexOf('&');
            if (amp >= 0)
            {
                id = id[..amp];
            }

            if (TryValidateId(id, out var playlistUrlId))
            {
                result = new YtDlpAlbumRef(YtDlpAlbumKind.Playlist, playlistUrlId);
                return true;
            }

            return false;
        }

        // YouTube Music album browse pages ("https://music.youtube.com/browse/MPREb_...").
        // yt-dlp downloads the album playlist behind such a page directly, so the
        // browse URL is a valid download target even when the entry inspection
        // could not resolve the canonical playlist id.
        if (url.StartsWith(BrowseUrlBase, StringComparison.OrdinalIgnoreCase))
        {
            var id = url[BrowseUrlBase.Length..];
            var q = id.IndexOf('?');
            if (q >= 0)
            {
                id = id[..q];
            }

            if (TryValidateId(id, out var browseUrlId))
            {
                result = new YtDlpAlbumRef(YtDlpAlbumKind.Browse, browseUrlId);
                return true;
            }

            return false;
        }

        return false;
    }

    // YouTube video IDs: 11 chars [A-Za-z0-9_-]. Playlist IDs: longer, [A-Za-z0-9_-].
    private static bool TryValidateId(string? id, out string validated)
    {
        validated = string.Empty;
        if (string.IsNullOrEmpty(id) || id.Length > 64)
        {
            return false;
        }

        if (id.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_'))
        {
            return false;
        }

        validated = id;
        return true;
    }
}
