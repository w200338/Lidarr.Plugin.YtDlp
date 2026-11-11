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
