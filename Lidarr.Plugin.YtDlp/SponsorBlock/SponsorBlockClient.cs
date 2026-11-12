using System.Net;
using System.Text.Json;
using Lidarr.Plugin.YtDlp.Probing;
using NLog;

namespace Lidarr.Plugin.YtDlp.SponsorBlock;

/// <summary>One SponsorBlock segment (per the /api/skipSegments response shape).</summary>
public sealed record SponsorBlockSegment(
    string Category,
    string ActionType,
    double Start,
    double End,
    string Uuid,
    double VideoDuration);

/// <summary>
/// Minimal SponsorBlock read client. Endpoints follow the public API:
///   GET {base}/api/status                          → liveness (uptime field)
///   GET {base}/api/skipSegments?videoID=..&categories=["..",..]
///                                                   → JSON array of segments;
///                                                     404 means "no segments" (not an error).
/// Categories are JSON-encoded array strings per the server's param contract.
/// </summary>
public interface ISponsorBlockClient
{
    Task<ProbeCheckResult> CheckEndpointAsync(string endpoint, CancellationToken ct);

    /// <summary>
    /// Fetches segments for a video. Returns an empty list when the video has
    /// no segments (server 404). Throws on other transport/HTTP failures —
    /// callers decide how to degrade.
    /// </summary>
    Task<IReadOnlyList<SponsorBlockSegment>> GetSegmentsAsync(
        string endpoint, string videoId, IReadOnlyList<string> categories, CancellationToken ct);
}

public sealed class SponsorBlockClient : ISponsorBlockClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TimeSpan _requestTimeout;
    private readonly Logger? _logger;

    public SponsorBlockClient(HttpClient? client = null, TimeSpan? requestTimeout = null, Logger? logger = null)
    {
        _ownsHttp = client is null;
        _http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        _logger = logger;
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    public async Task<ProbeCheckResult> CheckEndpointAsync(string endpoint, CancellationToken ct)
    {
        var baseUri = NormalizeEndpoint(endpoint);
        if (baseUri is null)
        {
            return ProbeCheckResult.Fail("SponsorBlock endpoint is empty.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_requestTimeout);

        try
        {
            using var response = await _http.GetAsync($"{baseUri}/api/status", cts.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ProbeCheckResult.Fail($"{endpoint} responded 404 — it does not look like a SponsorBlock server.");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return ProbeCheckResult.Fail(
                    $"SponsorBlock endpoint {endpoint} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var hasUptime = doc.RootElement.ValueKind == JsonValueKind.Object
                            && doc.RootElement.TryGetProperty("uptime", out _);

            return hasUptime
                ? ProbeCheckResult.Pass()
                : ProbeCheckResult.Warn("SponsorBlock endpoint responded but had no 'uptime' field; double-check it is the SponsorBlock API.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProbeCheckResult.Fail($"SponsorBlock endpoint {endpoint} timed out after {(int)_requestTimeout.TotalSeconds}s.");
        }
        catch (JsonException)
        {
            return ProbeCheckResult.Fail($"SponsorBlock endpoint {endpoint} returned invalid JSON.");
        }
        catch (HttpRequestException ex)
        {
            return ProbeCheckResult.Fail($"SponsorBlock endpoint {endpoint} is unreachable: {Shorten(ex.Message)}");
        }
    }

    public async Task<IReadOnlyList<SponsorBlockSegment>> GetSegmentsAsync(
        string endpoint, string videoId, IReadOnlyList<string> categories, CancellationToken ct)
    {
        var baseUri = NormalizeEndpoint(endpoint)
            ?? throw new ArgumentException("SponsorBlock endpoint is empty.", nameof(endpoint));

        var categoriesJson = JsonSerializer.Serialize(categories);
        var url = $"{baseUri}/api/skipSegments?videoID={Uri.EscapeDataString(videoId)}" +
                  $"&categories={Uri.EscapeDataString(categoriesJson)}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_requestTimeout);

        try
        {
            using var response = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Per the API contract: no segments for this video is a 404, not an error.
                return Array.Empty<SponsorBlockSegment>();
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ParseSegments(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
    }

    /// <summary>Parses the /api/skipSegments JSON array (exposed for tests).</summary>
    internal static IReadOnlyList<SponsorBlockSegment> ParseSegments(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var segments = new List<SponsorBlockSegment>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return segments;
        }

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object
                || !el.TryGetProperty("segment", out var span)
                || span.ValueKind != JsonValueKind.Array
                || span.GetArrayLength() < 2)
            {
                continue;
            }

            segments.Add(new SponsorBlockSegment(
                Category: el.TryGetProperty("category", out var c) ? c.GetString() ?? string.Empty : string.Empty,
                ActionType: el.TryGetProperty("actionType", out var a) ? a.GetString() ?? string.Empty : string.Empty,
                Start: span[0].GetDouble(),
                End: span[1].GetDouble(),
                Uuid: el.TryGetProperty("UUID", out var u) ? u.GetString() ?? string.Empty : string.Empty,
                VideoDuration: el.TryGetProperty("videoDuration", out var d) ? d.GetDouble() : 0));
        }

        return segments;
    }

    /// <summary>
    /// Trims, strips a trailing '/', and prepends 'https://' when the user
    /// entered a scheme-less host (mirrors yt-dlp's --sponsorblock-api handling).
    /// Returns null for empty input.
    /// </summary>
    internal static string? NormalizeEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        var e = endpoint.Trim();
        if (!e.Contains("://", StringComparison.Ordinal))
        {
            e = "https://" + e;
        }

        while (e.EndsWith('/'))
        {
            e = e[..^1];
        }

        return e.Length == 0 ? null : e;
    }

    private static string Shorten(string s)
    {
        s = s.Replace(Environment.NewLine, " ").Trim();
        return s.Length <= 160 ? s : s[..160];
    }
}
