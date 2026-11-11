using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using FluentValidation;
using Lidarr.Plugin.Abstractions.Contracts;

namespace Lidarr.Plugin.YtDlp.Settings;

/// <summary>
/// Bridges the host's flat string-keyed settings dictionary and the typed
/// <see cref="YtDlpSettings"/> POCO, and owns validation.
/// </summary>
public sealed class YtDlpSettingsProvider : ISettingsProvider
{
    private readonly object _lock = new();
    private readonly YtDlpSettingsValidator _validator = new();
    private YtDlpSettings _current;

    public YtDlpSettingsProvider()
        : this(new YtDlpSettings())
    {
    }

    public YtDlpSettingsProvider(YtDlpSettings initial)
    {
        _current = initial.Clone();
    }

    /// <summary>Most recently applied settings snapshot.</summary>
    public YtDlpSettings Current
    {
        get { lock (_lock) { return _current.Clone(); } }
    }

    public IReadOnlyCollection<SettingDefinition> Describe() => new[]
    {
        new SettingDefinition { Key = "ytDlpPath", DisplayName = "yt-dlp Path", Description = "Full path to the yt-dlp executable (or 'yt-dlp' when it is on PATH).", DataType = SettingDataType.String, IsRequired = true },
        new SettingDefinition { Key = "additionalArgs", DisplayName = "Additional yt-dlp Arguments", Description = "Extra yt-dlp command line arguments appended to every invocation. Quote values that contain spaces.", DataType = SettingDataType.String },
        new SettingDefinition { Key = "useSponsorBlock", DisplayName = "Use SponsorBlock", Description = "Trim non-music parts (intros, outros, off-topic, sponsors, ...) from downloaded songs using the SponsorBlock API.", DataType = SettingDataType.Boolean },
        new SettingDefinition { Key = "sponsorBlockApiEndpoint", DisplayName = "SponsorBlock API Endpoint", Description = "Base URL of the SponsorBlock server (default https://sponsor.ajay.app; self-hosted instances supported).", DataType = SettingDataType.String, DefaultValue = YtDlpSettings.DefaultSponsorBlockEndpoint },
        new SettingDefinition { Key = "reencode", DisplayName = "Re-encode Audio", Description = "Re-encode downloaded audio to the selected format at maximum quality (requires ffmpeg). Disabled keeps the best source audio.", DataType = SettingDataType.Enum, DefaultValue = "disabled", AllowedValues = new[] { "disabled", "mp3", "aac", "opus", "vorbis" } },
        new SettingDefinition { Key = "minDownloadDelaySeconds", DisplayName = "Minimum Time Between Downloads (seconds)", Description = "Minimum seconds to wait before starting the next download (randomized up to the maximum) to reduce bot-detection risk.", DataType = SettingDataType.Number, DefaultValue = 5.0 },
        new SettingDefinition { Key = "maxDownloadDelaySeconds", DisplayName = "Maximum Time Between Downloads (seconds)", Description = "Maximum seconds to wait before starting the next download (randomized between the minimum and this value).", DataType = SettingDataType.Number, DefaultValue = 30.0 },
        new SettingDefinition { Key = "searchLimit", DisplayName = "Search Result Limit", Description = "Maximum number of results returned per search.", DataType = SettingDataType.Integer, DefaultValue = 25 }
    };

    public IReadOnlyDictionary<string, object?> GetDefaults()
    {
        var dict = ToDictionary(new YtDlpSettings());
        return new Dictionary<string, object?>(dict);
    }

    public PluginValidationResult Validate(IDictionary<string, object?> settings)
    {
        if (!TryMap((IReadOnlyDictionary<string, object?>)settings, out var mapped, out var errors))
        {
            return PluginValidationResult.Failure(errors);
        }

        var result = _validator.Validate(mapped);
        if (result.IsValid)
        {
            return PluginValidationResult.Success();
        }

        return PluginValidationResult.Failure(result.Errors.Select(e => e.ErrorMessage));
    }

    public PluginValidationResult Apply(IDictionary<string, object?> settings)
    {
        var validation = Validate(settings);
        if (!validation.IsValid)
        {
            return validation;
        }

        lock (_lock)
        {
            _current = ToSettings((IReadOnlyDictionary<string, object?>)settings);
        }

        return validation;
    }

    /// <summary>Maps a (possibly partial) settings dictionary onto a fresh POCO with defaults.</summary>
    public static YtDlpSettings ToSettings(IReadOnlyDictionary<string, object?> settings)
    {
        TryMap(settings, out var mapped, out _);
        return mapped ?? new YtDlpSettings();
    }

    public static IReadOnlyDictionary<string, object?> ToDictionary(YtDlpSettings s)
    {
        var dict = new Dictionary<string, object?>
        {
            ["ytDlpPath"] = s.YtDlpPath,
            ["additionalArgs"] = s.AdditionalArgs,
            ["useSponsorBlock"] = s.UseSponsorBlock,
            ["sponsorBlockApiEndpoint"] = s.SponsorBlockApiEndpoint,
            ["reencode"] = ReencodeNames.Get(s.Reencode),
            ["minDownloadDelaySeconds"] = s.MinDownloadDelaySeconds,
            ["maxDownloadDelaySeconds"] = s.MaxDownloadDelaySeconds,
            ["searchLimit"] = s.SearchLimit
        };
        return (IReadOnlyDictionary<string, object?>)dict;
    }

    internal static bool TryMap(IReadOnlyDictionary<string, object?>? settings, [NotNullWhen(true)] out YtDlpSettings? mapped, out IReadOnlyList<string> errors)
    {
        var list = new List<string>();
        errors = list;
        mapped = new YtDlpSettings();
        var s = settings ?? (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>();

        string GetString(string key, string fallback) =>
            s.TryGetValue(key, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? fallback : fallback;

        mapped.YtDlpPath = GetString("ytDlpPath", mapped.YtDlpPath);
        mapped.AdditionalArgs = GetString("additionalArgs", mapped.AdditionalArgs);
        mapped.SponsorBlockApiEndpoint = GetString("sponsorBlockApiEndpoint", mapped.SponsorBlockApiEndpoint);

        if (s.TryGetValue("useSponsorBlock", out var sbRaw) && sbRaw != null)
        {
            if (sbRaw is bool b)
            {
                mapped.UseSponsorBlock = b;
            }
            else if (bool.TryParse(Convert.ToString(sbRaw, CultureInfo.InvariantCulture), out var b2))
            {
                mapped.UseSponsorBlock = b2;
            }
            else
            {
                list.Add("'useSponsorBlock' must be a boolean.");
            }
        }

        if (s.TryGetValue("reencode", out var reRaw) && reRaw != null)
        {
            var reName = Convert.ToString(reRaw, CultureInfo.InvariantCulture) ?? string.Empty;
            if (!ReencodeNames.TryGet(reName, out var re))
            {
                list.Add($"'reencode' value '{reName}' is not one of: disabled, mp3, aac, opus, vorbis.");
            }
            else if (re.HasValue)
            {
                mapped.Reencode = re.Value;
            }
        }

        if (s.TryGetValue("minDownloadDelaySeconds", out var minRaw) && minRaw != null)
        {
            if (TryDouble(minRaw, out var min))
            {
                mapped.MinDownloadDelaySeconds = min;
            }
            else
            {
                list.Add("'minDownloadDelaySeconds' must be a number.");
            }
        }

        if (s.TryGetValue("maxDownloadDelaySeconds", out var maxRaw) && maxRaw != null)
        {
            if (TryDouble(maxRaw, out var max))
            {
                mapped.MaxDownloadDelaySeconds = max;
            }
            else
            {
                list.Add("'maxDownloadDelaySeconds' must be a number.");
            }
        }

        if (s.TryGetValue("searchLimit", out var slRaw) && slRaw != null)
        {
            if (slRaw is int or long)
            {
                mapped.SearchLimit = Convert.ToInt32(slRaw, CultureInfo.InvariantCulture);
            }
            else if (int.TryParse(Convert.ToString(slRaw, CultureInfo.InvariantCulture), out var sl))
            {
                mapped.SearchLimit = sl;
            }
            else
            {
                list.Add("'searchLimit' must be an integer.");
            }
        }

        if (list.Count > 0)
        {
            mapped = null;
        }

        return list.Count == 0;
    }

    private static bool TryDouble(object? value, out double result)
    {
        switch (value)
        {
            case double d: result = d; return true;
            case float f: result = f; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            default:
                return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }
    }
}

/// <summary>String names for <see cref="ReencodeFormat"/> as exposed in settings.</summary>
public static class ReencodeNames
{
    private static readonly Dictionary<string, ReencodeFormat> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["disabled"] = ReencodeFormat.Disabled,
        ["mp3"] = ReencodeFormat.Mp3,
        ["aac"] = ReencodeFormat.Aac,
        ["opus"] = ReencodeFormat.Opus,
        ["vorbis"] = ReencodeFormat.Vorbis
    };

    public static string Get(ReencodeFormat format) => format switch
    {
        ReencodeFormat.Disabled => "disabled",
        ReencodeFormat.Mp3 => "mp3",
        ReencodeFormat.Aac => "aac",
        ReencodeFormat.Opus => "opus",
        ReencodeFormat.Vorbis => "vorbis",
        _ => "disabled"
    };

    public static bool TryGet(string? name, [NotNullWhen(true)] out ReencodeFormat? format)
    {
        if (name != null && Map.TryGetValue(name.Trim(), out var f))
        {
            format = f;
            return true;
        }

        format = null;
        return false;
    }
}
