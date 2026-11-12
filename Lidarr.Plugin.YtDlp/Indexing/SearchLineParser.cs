using System.Text.Json;

namespace Lidarr.Plugin.YtDlp.Indexing;

/// <summary>One search/list entry parsed from a yt-dlp JSON print line.</summary>
public sealed record SearchEntry(
    string Id,
    string Url,
    string Title,
    string Channel,
    double? Duration,
    string? Type);

/// <summary>
/// What one flat entry inspection (browse/playlist page re-fetch) resolved:
/// the real album title, the page's channel, and - when the page embeds an
/// album playlist - its playlist id. The playlist id lets the indexer emit
/// a canonical <c>youtube.com/playlist?list=</c> download URL that the
/// download client accepts without any extra URL handling.
/// </summary>
public sealed record EntryResolution(
    string Title,
    string? Channel,
    string? PlaylistId);

/// <summary>
/// Parses the JSON-per-line output of <see cref="Commands.YtDlpCommandBuilder.PrintTemplate"/>
/// (one compact JSON object per line). Non-JSON lines (yt-dlp warnings etc.) and
/// entries without a usable id are skipped, never thrown.
/// </summary>
public static class SearchLineParser
{
    public static bool TryParseLine(string line, out SearchEntry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed[0] != '{')
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            var id = GetString(root, "id");
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            entry = new SearchEntry(
                Id: id,
                Url: GetString(root, "url"),
                Title: GetString(root, "title"),
                Channel: GetString(root, "channel"),
                Duration: root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? d.GetDouble()
                    : null,
                Type: root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The music search page prefixes album titles (e.g. "Album - 21").
    /// Stripped so the composed release title matches the catalog album.
    /// </summary>
    private const string AlbumTitlePrefix = "Album - ";

    /// <summary>
    /// Parses one JSON line of <see cref="Commands.YtDlpCommandBuilder.InspectPrintTemplate"/>
    /// into the entry's real title/channel/playlist-id. The "Album - " page
    /// prefix is stripped from the title. Returns false for non-JSON lines or
    /// lines without a playlist_title (e.g. track entries with no metadata).
    /// </summary>
    public static bool TryParseInspectLine(string line, out EntryResolution? resolution)
    {
        resolution = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed[0] != '{')
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            var rawTitle = root.TryGetProperty("playlist_title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(rawTitle))
            {
                return false;
            }

            var title = rawTitle.StartsWith(AlbumTitlePrefix, StringComparison.Ordinal)
                ? rawTitle[AlbumTitlePrefix.Length..].Trim()
                : rawTitle.Trim();

            if (title.Length == 0)
            {
                return false;
            }

            var channel = root.TryGetProperty("playlist_channel", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;

            var playlistId = root.TryGetProperty("playlist_id", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(playlistId))
            {
                playlistId = playlistId!.Trim();
            }

            resolution = new EntryResolution(title, channel, playlistId);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Parses every line of a buffered output, skipping unparseable ones.</summary>
    public static IReadOnlyList<SearchEntry> ParseAll(string output)
    {
        var results = new List<SearchEntry>();
        if (string.IsNullOrEmpty(output))
        {
            return results;
        }

        foreach (var line in output.Split('\n'))
        {
            if (TryParseLine(line, out var entry))
            {
                results.Add(entry);
            }
        }

        return results;
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;
}
