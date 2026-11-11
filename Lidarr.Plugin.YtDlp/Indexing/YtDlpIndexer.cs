using System.Runtime.CompilerServices;
using System.Text.Json;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.YtDlp.Commands;
using Lidarr.Plugin.YtDlp.Download;
using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Settings;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.YtDlp.Indexing;

/// <summary>
/// Lidarr <see cref="IIndexer"/> (and <see cref="IIndexerWithMetadata"/>) over
/// yt-dlp. Deliberately does NOT derive from <c>BaseStreamingIndexer</c> — that
/// base is HTTP-API-shaped (HttpClient + JSON APIs); this indexer is
/// process-driven. We borrow only the base's query pre/post-processing ideas
/// (trim/normalize the query, dedupe + cap results) and keep every behavior
/// unit-testable behind <see cref="IYtDlpRunner"/>.
///
/// All search calls are serialized through one semaphore (rate-limit courtesy)
/// and capped at <see cref="YtDlpSettings.SearchLimit"/>. Search failures never
/// throw to the host — they return an empty list.
///
/// Album-id tokens emitted here are the Phase 3 download contract:
///   <c>yt:&lt;videoId&gt;</c> (single-track album) / <c>playlist:&lt;id&gt;</c>.
/// </summary>
public sealed class YtDlpIndexer : IIndexer, IIndexerWithMetadata, IAsyncDisposable
{
    public const string ProtocolId = "ytdlp";
    public const string TracksMetadataKey = "ytdlp.tracks";

    private readonly YtDlpSettings _settings;
    private readonly IYtDlpRunner _runner;
    private readonly YtDlpCommandBuilder _builder = new();
    private readonly SemaphoreSlim _searchGate = new(1, 1);
    private readonly ILogger? _logger;

    public YtDlpIndexer(YtDlpSettings settings, IYtDlpRunner runner, ILogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger;
    }

    // ----- IIndexerWithMetadata -----

    public string Protocol => ProtocolId;
    public int PageSize { get; } = 25;
    public int MaxPageSize { get; } = 25;
    public bool SupportsRss => false;
    public string ServiceName => "YouTube (yt-dlp)";

    // ----- IIndexer -----

    public ValueTask<PluginValidationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Structural check only; full external verification (yt-dlp --version,
        // reachability) is the Phase 5 connection test.
        if (string.IsNullOrWhiteSpace(_settings.YtDlpPath))
        {
            return ValueTask.FromResult(
                PluginValidationResult.Failure(new[] { "ytDlpPath is not configured." }));
        }

        return ValueTask.FromResult(PluginValidationResult.Success());
    }

    public async ValueTask<IReadOnlyList<StreamingAlbum>> SearchAlbumsAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<StreamingAlbum>();
        }

        var entries = await SearchAsync(_builder.BuildAlbumSearchArgs(NormalizeQuery(query), _limit), cancellationToken)
            .ConfigureAwait(false);

        var results = new List<StreamingAlbum>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var album = MapAlbum(entry);
            if (album != null && seen.Add(album.Id!))
            {
                results.Add(album);
            }
        }

        return results;
    }

    public async ValueTask<IReadOnlyList<StreamingTrack>> SearchTracksAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<StreamingTrack>();
        }

        var entries = await SearchAsync(_builder.BuildSearchArgs(NormalizeQuery(query), _limit), cancellationToken)
            .ConfigureAwait(false);

        var results = new List<StreamingTrack>(entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var track = MapTrack(entry, trackNumber: null);
            if (track != null && seen.Add(track.Id!))
            {
                results.Add(track);
            }
        }

        return results;
    }

    public async ValueTask<StreamingAlbum?> GetAlbumAsync(string albumId, CancellationToken cancellationToken = default)
    {
        if (!YtDlpAlbumId.TryParse(albumId, out var album))
        {
            return null;
        }

        var url = YtDlpAlbumId.ToUrl(album);
        var args = album.Kind == YtDlpAlbumKind.SingleTrack
            ? _builder.BuildProbeArgs(url)
            : _builder.BuildPlaylistProbeArgs(url);

        try
        {
            await _searchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var result = await _runner.RunBufferedAsync(args, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    _logger?.LogWarning("GetAlbumAsync probe for {AlbumId} failed (exit {ExitCode}): {StdErr}",
                        albumId, result.ExitCode, Truncate(result.StandardError, 300));
                    return null;
                }

                return album.Kind == YtDlpAlbumKind.SingleTrack
                    ? MapSingleTrackAlbum(result.StandardOutput)
                    : MapPlaylistAlbum(result.StandardOutput, album);
            }
            finally
            {
                _searchGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GetAlbumAsync failed for {AlbumId}", albumId);
            return null;
        }
    }

    public async IAsyncEnumerable<StreamingAlbum> SearchAlbumsStreamAsync(
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var album in await SearchAlbumsAsync(query, cancellationToken).ConfigureAwait(false))
        {
            yield return album;
        }
    }

    public async IAsyncEnumerable<StreamingTrack> SearchTracksStreamAsync(
        string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var track in await SearchTracksAsync(query, cancellationToken).ConfigureAwait(false))
        {
            yield return track;
        }
    }

    public ValueTask DisposeAsync()
    {
        _searchGate.Dispose();
        return ValueTask.CompletedTask;
    }

    // ----- internals -----

    private int _limit => Math.Clamp(_settings.SearchLimit, 1, 100);

    /// <summary>Runs one buffered yt-dlp search; failures → empty list (search never throws).</summary>
    private async Task<IReadOnlyList<SearchEntry>> SearchAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        YtDlpRunResult result;
        await _searchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            result = await _runner.RunBufferedAsync(args, ct).ConfigureAwait(false);
        }
        finally
        {
            _searchGate.Release();
        }

        if (!result.IsSuccess)
        {
            _logger?.LogWarning("yt-dlp search failed (exit {ExitCode}): {StdErr}",
                result.ExitCode, Truncate(result.StandardError, 300));
            return Array.Empty<SearchEntry>();
        }

        var entries = SearchLineParser.ParseAll(result.StandardOutput);
        return entries.Count > _limit ? entries.Take(_limit).ToList() : entries;
    }

    internal static StreamingAlbum? MapAlbum(SearchEntry entry)
    {
        if (string.IsNullOrEmpty(entry.Id))
        {
            return null;
        }

        var isPlaylist = string.Equals(entry.Type, "playlist", StringComparison.OrdinalIgnoreCase)
                         || entry.Url.Contains("list=", StringComparison.Ordinal);

        if (!isPlaylist && !entry.Url.StartsWith("https://", StringComparison.Ordinal))
        {
            // No usable form for this entry — skip.
            return null;
        }

        var artist = new StreamingArtist
        {
            Id = string.Empty,
            Name = string.IsNullOrWhiteSpace(entry.Channel) ? "Unknown Artist" : entry.Channel
        };

        if (isPlaylist)
        {
            return new StreamingAlbum
            {
                Id = $"playlist:{entry.Id}",
                Title = entry.Title,
                Artist = artist,
                Type = StreamingAlbumType.Album,
                TrackCount = 0, // unknown until GetAlbumAsync expands it
                Metadata =
                {
                    ["url"] = entry.Url
                }
            };
        }

        // A plain video entry is a single-track "album".
        return new StreamingAlbum
        {
            Id = $"yt:{entry.Id}",
            Title = entry.Title,
            Artist = artist,
            Type = StreamingAlbumType.Single,
            TrackCount = 1,
            Duration = entry.Duration != null ? TimeSpan.FromSeconds(entry.Duration.Value) : null,
            Metadata =
            {
                ["url"] = entry.Url
            }
        };
    }

    internal static StreamingTrack? MapTrack(SearchEntry entry, int? trackNumber)
    {
        if (string.IsNullOrEmpty(entry.Id))
        {
            return null;
        }

        return new StreamingTrack
        {
            Id = $"yt:{entry.Id}",
            Title = entry.Title,
            Artist = new StreamingArtist
            {
                Id = string.Empty,
                Name = string.IsNullOrWhiteSpace(entry.Channel) ? "Unknown Artist" : entry.Channel
            },
            TrackNumber = trackNumber,
            Duration = entry.Duration != null ? TimeSpan.FromSeconds(entry.Duration.Value) : null,
            Album = new StreamingAlbum
            {
                Id = $"yt:{entry.Id}",
                Title = entry.Title,
                TrackCount = 1
            },
            Metadata =
            {
                ["url"] = entry.Url,
                ["type"] = entry.Type ?? string.Empty
            }
        };
    }

    /// <summary>Single-video probe JSON → one-track album with the track attached.</summary>
    private StreamingAlbum? MapSingleTrackAlbum(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var title = GetString(root, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var channel = GetString(root, "channel");
        if (string.IsNullOrWhiteSpace(channel))
        {
            channel = GetString(root, "uploader");
        }

        TimeSpan? duration = null;
        if (root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
        {
            duration = TimeSpan.FromSeconds(d.GetDouble());
        }

        var id = GetString(root, "id");
        var url = GetString(root, "webpage_url");
        if (string.IsNullOrEmpty(url))
        {
            url = GetString(root, "url");
        }

        var track = new StreamingTrack
        {
            Id = string.IsNullOrEmpty(id) ? string.Empty : $"yt:{id}",
            Title = title,
            Artist = new StreamingArtist
            {
                Id = string.Empty,
                Name = string.IsNullOrWhiteSpace(channel) ? "Unknown Artist" : channel
            },
            TrackNumber = 1,
            Duration = duration,
            Metadata =
            {
                ["url"] = url
            }
        };

        return new StreamingAlbum
        {
            Id = string.IsNullOrEmpty(id) ? string.Empty : $"yt:{id}",
            Title = title,
            Artist = track.Artist,
            Type = StreamingAlbumType.Single,
            TrackCount = 1,
            Duration = duration,
            Metadata =
            {
                ["url"] = url,
                [TracksMetadataKey] = new List<StreamingTrack> { track }
            }
        };
    }

    /// <summary>
    /// Flat playlist probe JSON → album shell (title/artist/track count) with the
    /// expanded track list in Metadata[TracksMetadataKey]. Flat entries lack per-track
    /// channels, so track artist falls back to the playlist owner.
    /// </summary>
    private StreamingAlbum? MapPlaylistAlbum(string json, YtDlpAlbumRef album)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var title = GetString(root, "title");
        var owner = GetString(root, "channel");
        if (string.IsNullOrWhiteSpace(owner))
        {
            owner = GetString(root, "uploader");
        }

        var tracks = new List<StreamingTrack>();
        if (root.TryGetProperty("playlist", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            var number = 0;
            foreach (var el in entries.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = GetString(el, "id");
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                number++;
                var t = new StreamingTrack
                {
                    Id = $"yt:{id}",
                    Title = GetString(el, "title"),
                    Artist = new StreamingArtist
                    {
                        Id = string.Empty,
                        Name = string.IsNullOrWhiteSpace(owner) ? "Unknown Artist" : owner
                    },
                    TrackNumber = number,
                    Duration = el.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                        ? TimeSpan.FromSeconds(d.GetDouble())
                        : null,
                    Metadata =
                    {
                        ["url"] = GetString(el, "url")
                    }
                };
                tracks.Add(t);
            }
        }

        if (tracks.Count == 0)
        {
            return null;
        }

        return new StreamingAlbum
        {
            Id = album.ToString(),
            Title = string.IsNullOrWhiteSpace(title) ? $"Playlist {album.Id}" : title,
            Artist = new StreamingArtist
            {
                Id = string.Empty,
                Name = string.IsNullOrWhiteSpace(owner) ? "Unknown Artist" : owner
            },
            Type = StreamingAlbumType.Album,
            TrackCount = tracks.Count,
            Metadata =
            {
                [TracksMetadataKey] = tracks
            }
        };
    }

    private static string NormalizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        return System.Text.RegularExpressions.Regex.Replace(query.Trim(), @"\s+", " ");
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() ?? string.Empty
            : string.Empty;

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[^max..];
}
