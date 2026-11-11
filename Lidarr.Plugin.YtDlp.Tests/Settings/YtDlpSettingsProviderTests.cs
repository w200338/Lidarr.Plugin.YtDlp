using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.YtDlp.Settings;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests.Settings;

[TestFixture]
public class YtDlpSettingsProviderTests
{
    private static Dictionary<string, object?> ValidSettings() => new Dictionary<string, object?>
    {
        ["ytDlpPath"] = "/usr/local/bin/yt-dlp",
        ["additionalArgs"] = "",
        ["useSponsorBlock"] = false,
        ["sponsorBlockApiEndpoint"] = "https://sponsor.ajay.app",
        ["reencode"] = "disabled",
        ["minDownloadDelaySeconds"] = 5.0,
        ["maxDownloadDelaySeconds"] = 30.0,
        ["searchLimit"] = 25
    };

    private static Dictionary<string, object?> With(IReadOnlyDictionary<string, object?> baseDict, params (string Key, object? Value)[] overrides)
    {
        var dict = new Dictionary<string, object?>(baseDict);
        foreach (var (key, value) in overrides)
        {
            dict[key] = value;
        }

        return dict;
    }

    [Test]
    public void GetDefaults_returns_all_keys_with_documented_defaults()
    {
        var defaults = new YtDlpSettingsProvider().GetDefaults();

        Assert.That(defaults.Count, Is.EqualTo(8));
        Assert.That(defaults["ytDlpPath"], Is.EqualTo(""));
        Assert.That(defaults["additionalArgs"], Is.EqualTo(""));
        Assert.That(defaults["useSponsorBlock"], Is.EqualTo(false));
        Assert.That(defaults["sponsorBlockApiEndpoint"], Is.EqualTo("https://sponsor.ajay.app"));
        Assert.That(defaults["reencode"], Is.EqualTo("disabled"));
        Assert.That(defaults["minDownloadDelaySeconds"], Is.EqualTo(5.0));
        Assert.That(defaults["maxDownloadDelaySeconds"], Is.EqualTo(30.0));
        Assert.That(defaults["searchLimit"], Is.EqualTo(25));
    }

    [Test]
    public void Describe_exposes_all_settings_with_correct_types()
    {
        var definitions = new YtDlpSettingsProvider().Describe().ToDictionary(d => d.Key, d => d);

        Assert.That(definitions.Count, Is.EqualTo(8));
        Assert.That(definitions["ytDlpPath"].DataType, Is.EqualTo(SettingDataType.String));
        Assert.That(definitions["ytDlpPath"].IsRequired, Is.True);
        Assert.That(definitions["additionalArgs"].DataType, Is.EqualTo(SettingDataType.String));
        Assert.That(definitions["useSponsorBlock"].DataType, Is.EqualTo(SettingDataType.Boolean));
        Assert.That(definitions["sponsorBlockApiEndpoint"].DataType, Is.EqualTo(SettingDataType.String));
        Assert.That(definitions["reencode"].DataType, Is.EqualTo(SettingDataType.Enum));
        Assert.That(definitions["reencode"].AllowedValues, Is.EquivalentTo(new[] { "disabled", "mp3", "aac", "opus", "vorbis" }));
        Assert.That(definitions["minDownloadDelaySeconds"].DataType, Is.EqualTo(SettingDataType.Number));
        Assert.That(definitions["maxDownloadDelaySeconds"].DataType, Is.EqualTo(SettingDataType.Number));
        Assert.That(definitions["searchLimit"].DataType, Is.EqualTo(SettingDataType.Integer));
    }

    [Test]
    public void Validate_accepts_a_valid_settings_dictionary()
    {
        var result = new YtDlpSettingsProvider().Validate(ValidSettings());

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public void Validate_rejects_missing_yt_dlp_path()
    {
        var result = new YtDlpSettingsProvider().Validate(With(ValidSettings(), ("ytDlpPath", "")));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Does.Contain("Path to yt-dlp is required."));
    }

    [TestCase(30.0, 5.0)]      // min > max
    [TestCase(-1.0, 30.0)]    // negative min
    [TestCase(5.0, -5.0)]     // negative max
    [TestCase(5.0, 200000.0)] // max beyond 24h ceiling
    public void Validate_rejects_invalid_delay_values(double min, double max)
    {
        var dict = With(ValidSettings(),
            ("minDownloadDelaySeconds", min),
            ("maxDownloadDelaySeconds", max));

        var result = new YtDlpSettingsProvider().Validate(dict);

        Assert.That(result.IsValid, Is.False, $"expected failure for min={min} max={max}");
    }

    [Test]
    public void Validate_accepts_zero_delay() // 0/0 = no spacing, a legal config
    {
        var dict = With(ValidSettings(),
            ("minDownloadDelaySeconds", 0.0),
            ("maxDownloadDelaySeconds", 0.0));

        var result = new YtDlpSettingsProvider().Validate(dict);

        Assert.That(result.IsValid, Is.True);
    }

    [TestCase(0)]
    [TestCase(101)]
    [TestCase(-3)]
    public void Validate_rejects_search_limit_out_of_range(int limit)
    {
        var result = new YtDlpSettingsProvider().Validate(With(ValidSettings(), ("searchLimit", limit)));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Does.Contain("Search limit must be between 1 and 100."));
    }

    [TestCase("wma")]
    [TestCase("MP3;extra")]
    [TestCase("")]
    public void Validate_rejects_unknown_reencode_value(string reencode)
    {
        var result = new YtDlpSettingsProvider().Validate(With(ValidSettings(), ("reencode", reencode)));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Count(e => e.Contains("reencode")), Is.GreaterThan(0));
    }

    [TestCase("not a url", true)]
    [TestCase("file:///tmp/sb", true)]
    public void Validate_rejects_bad_sponsorblock_endpoint_when_enabled(string endpoint, bool enabled)
    {
        var result = new YtDlpSettingsProvider().Validate(With(ValidSettings(),
            ("sponsorBlockApiEndpoint", endpoint), ("useSponsorBlock", enabled)));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Count(e => e.Contains("SponsorBlock")), Is.GreaterThan(0));
    }

    [Test]
    public void Validate_allows_schemeless_sponsorblock_endpoint_when_enabled()
    {
        var result = new YtDlpSettingsProvider().Validate(With(ValidSettings(),
            ("sponsorBlockApiEndpoint", "sponsor.ajay.app"), ("useSponsorBlock", true)));

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public void Validate_rejects_unparseable_additional_args()
    {
        var result = new YtDlpSettingsProvider().Validate(With(ValidSettings(), ("additionalArgs", "--foo \"unterminated")));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Does.Contain("Additional arguments could not be parsed. Quote values that contain spaces."));
    }

    [Test]
    public void Validate_rejects_non_boolean_use_sponsorblock()
    {
        var result = new YtDlpSettingsProvider().Validate(With(ValidSettings(), ("useSponsorBlock", "yes please")));

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Does.Contain("'useSponsorBlock' must be a boolean."));
    }

    [Test]
    public void Apply_stores_snapshot_and_rejects_bad_input_without_mutation()
    {
        var provider = new YtDlpSettingsProvider();
        var good = With(ValidSettings(),
            ("ytDlpPath", "/opt/yt-dlp/yt-dlp"),
            ("reencode", "opus"),
            ("useSponsorBlock", true),
            ("minDownloadDelaySeconds", 12),
            ("searchLimit", 42));

        var applied = provider.Apply(good);

        Assert.That(applied.IsValid, Is.True);
        var current = provider.Current;
        Assert.That(current.YtDlpPath, Is.EqualTo("/opt/yt-dlp/yt-dlp"));
        Assert.That(current.Reencode, Is.EqualTo(ReencodeFormat.Opus));
        Assert.That(current.UseSponsorBlock, Is.True);
        Assert.That(current.MinDownloadDelaySeconds, Is.EqualTo(12));
        Assert.That(current.SearchLimit, Is.EqualTo(42));

        var failed = provider.Apply(With(good, ("ytDlpPath", "")));

        Assert.That(failed.IsValid, Is.False);
        Assert.That(provider.Current.YtDlpPath, Is.EqualTo("/opt/yt-dlp/yt-dlp"), "failed Apply must not mutate stored settings");
    }

    [Test]
    public void ToSettings_fills_defaults_for_missing_keys()
    {
        var settings = YtDlpSettingsProvider.ToSettings(new Dictionary<string, object?>
        {
            ["ytDlpPath"] = "yt-dlp"
        });

        Assert.That(settings.YtDlpPath, Is.EqualTo("yt-dlp"));
        Assert.That(settings.SponsorBlockApiEndpoint, Is.EqualTo("https://sponsor.ajay.app"));
        Assert.That(settings.Reencode, Is.EqualTo(ReencodeFormat.Disabled));
        Assert.That(settings.MinDownloadDelaySeconds, Is.EqualTo(5));
        Assert.That(settings.MaxDownloadDelaySeconds, Is.EqualTo(30));
        Assert.That(settings.SearchLimit, Is.EqualTo(25));
    }

    [Test]
    public void ToDictionary_and_ToSettings_roundtrip()
    {
        var original = new YtDlpSettings
        {
            YtDlpPath = "/bin/yt-dlp",
            AdditionalArgs = "--proxy \"https://user:pass@host:3128\"",
            UseSponsorBlock = true,
            SponsorBlockApiEndpoint = "http://sb.lan:1234",
            Reencode = ReencodeFormat.Vorbis,
            MinDownloadDelaySeconds = 1.5,
            MaxDownloadDelaySeconds = 45,
            SearchLimit = 10
        };

        var roundTripped = YtDlpSettingsProvider.ToSettings(YtDlpSettingsProvider.ToDictionary(original));

        Assert.That(roundTripped, Is.EqualTo(original));
    }

    [Test]
    public void ToSettings_reads_string_encoded_values_from_host_style_dicts()
    {
        var settings = YtDlpSettingsProvider.ToSettings(new Dictionary<string, object?>
        {
            ["ytDlpPath"] = "/usr/bin/yt-dlp",
            ["useSponsorBlock"] = "True",
            ["reencode"] = "AAC",
            ["minDownloadDelaySeconds"] = "7.5",
            ["maxDownloadDelaySeconds"] = "60",
            ["searchLimit"] = "50"
        });

        Assert.That(settings.UseSponsorBlock, Is.True);
        Assert.That(settings.Reencode, Is.EqualTo(ReencodeFormat.Aac));
        Assert.That(settings.MinDownloadDelaySeconds, Is.EqualTo(7.5));
        Assert.That(settings.MaxDownloadDelaySeconds, Is.EqualTo(60));
        Assert.That(settings.SearchLimit, Is.EqualTo(50));
    }
}
