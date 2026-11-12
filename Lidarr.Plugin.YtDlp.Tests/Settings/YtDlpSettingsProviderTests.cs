using FluentValidation.Results;
using Lidarr.Plugin.YtDlp.Settings;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests.Settings;

/// <summary>
/// Phase 7 (native model): the host provider-settings POCOs replace the old
/// Abstractions ISettingsProvider. Validation is pure and deterministic (the
/// live connection test is a separate step, see YtDlpConnectionTestTests).
/// </summary>
public class YtDlpSettingsTests
{
    [Test]
    public void Defaults_match_documented_surface()
    {
        var s = new YtDlpSettings();

        Assert.Multiple(() =>
        {
            Assert.That(s.YtDlpPath, Is.Empty);
            Assert.That(s.AdditionalArgs, Is.Empty);
            Assert.That(s.UseSponsorBlock, Is.False);
            Assert.That(s.SponsorBlockApiEndpoint, Is.EqualTo("https://sponsor.ajay.app"));
            Assert.That(s.Reencode, Is.EqualTo(ReencodeFormat.Disabled));
            Assert.That(s.MinDownloadDelaySeconds, Is.EqualTo(5));
            Assert.That(s.MaxDownloadDelaySeconds, Is.EqualTo(30));
            Assert.That(s.SearchLimit, Is.EqualTo(25));
        });
    }

    [Test]
    public void Equality_is_value_based()
    {
        var a = new YtDlpSettings { YtDlpPath = "yt-dlp", SearchLimit = 10 };
        var b = new YtDlpSettings { YtDlpPath = "yt-dlp", SearchLimit = 10 };
        b.Reencode = ReencodeFormat.Mp3;

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(new YtDlpSettings { YtDlpPath = "yt-dlp", SearchLimit = 10 }));
            Assert.That(a, Is.Not.EqualTo(b));
            Assert.That(a.GetHashCode(), Is.EqualTo(new YtDlpSettings { YtDlpPath = "yt-dlp", SearchLimit = 10 }.GetHashCode()));
            Assert.That(a, Is.Not.Null);
            Assert.That(a.Equals(null), Is.False);
        });
    }

    [Test]
    public void Indexer_settings_carry_host_required_members()
    {
        var s = new YtDlpIndexerSettings();

        Assert.Multiple(() =>
        {
            Assert.That(s, Is.InstanceOf<NzbDrone.Core.Indexers.IIndexerSettings>());
            Assert.That(s.BaseUrl, Is.Empty);
            Assert.That(s.EarlyReleaseLimit, Is.Null);
        });
    }

    [Test]
    public void Client_settings_carry_download_path()
    {
        var s = new YtDlpClientSettings();

        Assert.Multiple(() =>
        {
            Assert.That(s, Is.InstanceOf<NzbDrone.Core.ThingiProvider.IProviderConfig>());
            Assert.That(s.DownloadPath, Is.Empty);
        });
    }
}

public class YtDlpSettingsValidatorTests
{
    private static YtDlpIndexerSettings Valid() => new()
    {
        YtDlpPath = "yt-dlp",
        MinDownloadDelaySeconds = 5,
        MaxDownloadDelaySeconds = 30,
        SearchLimit = 25,
        SponsorBlockApiEndpoint = YtDlpSettings.DefaultSponsorBlockEndpoint
    };

    [Test]
    public void Valid_settings_pass()
    {
        var result = Valid().Validate();

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public void Missing_ytDlpPath_fails_with_property_tag()
    {
        var s = Valid();
        s.YtDlpPath = string.Empty;

        var result = s.Validate();

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Any(e => e.PropertyName == "YtDlpPath"));
    }

    [Test]
    public void Unmatched_quote_in_additional_args_fails()
    {
        var s = Valid();
        s.AdditionalArgs = "--cookies \"unterminated";

        var result = s.Validate();

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Any(e => e.PropertyName == "AdditionalArgs"));
    }

    [Test]
    public void Well_formed_additional_args_pass()
    {
        var s = Valid();
        s.AdditionalArgs = "--cookies \"/home/u/cookies.txt\" --sleep-requests 2";

        Assert.That(s.Validate().IsValid, Is.True);
    }

    [Test]
    public void Min_greater_than_max_delay_fails()
    {
        var s = Valid();
        s.MinDownloadDelaySeconds = 40;
        s.MaxDownloadDelaySeconds = 30;

        Assert.That(s.Validate().IsValid, Is.False);
    }

    [Test]
    public void Equal_delays_pass()
    {
        var s = Valid();
        s.MinDownloadDelaySeconds = 0;
        s.MaxDownloadDelaySeconds = 0;

        Assert.That(s.Validate().IsValid, Is.True);
    }

    [Test]
    public void Search_limit_out_of_range_fails()
    {
        var s = Valid();
        s.SearchLimit = 101;

        var result = s.Validate();

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Any(e => e.PropertyName == "SearchLimit"));
    }

    [Test]
    public void SponsorBlock_enabled_rejects_non_http_endpoint()
    {
        var s = Valid();
        s.UseSponsorBlock = true;
        s.SponsorBlockApiEndpoint = "file:///etc/passwd";

        var result = s.Validate();

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Any(e => e.PropertyName == "SponsorBlockApiEndpoint"));
    }

    [Test]
    public void SponsorBlock_enabled_accepts_scheme_less_endpoint()
    {
        var s = Valid();
        s.UseSponsorBlock = true;
        s.SponsorBlockApiEndpoint = "sponsor.ajay.app";

        Assert.That(s.Validate().IsValid, Is.True);
    }

    [Test]
    public void SponsorBlock_disabled_tolerates_garbage_endpoint()
    {
        var s = Valid();
        s.UseSponsorBlock = false;
        s.SponsorBlockApiEndpoint = "not a url at all";

        Assert.That(s.Validate().IsValid, Is.True);
    }

    [Test]
    public void SponsorBlock_enabled_rejects_empty_endpoint()
    {
        var s = Valid();
        s.UseSponsorBlock = true;
        s.SponsorBlockApiEndpoint = string.Empty;

        Assert.That(s.Validate().IsValid, Is.False);
    }

    [TestCase(null, false)]
    [TestCase("", false)]
    [TestCase("https://sponsor.ajay.app", true)]
    [TestCase("http://localhost:8080", true)]
    [TestCase("sponsor.ajay.app", true)]
    [TestCase("  https://x.y/z  ", true)]
    [TestCase("file:///x", false)]
    [TestCase("ftp://x", false)]
    [TestCase("https://", false)]
    public void Endpoint_normalization_matrix(string? raw, bool expected)
    {
        Assert.That(YtDlpSettingsValidator.IsValidSponsorBlockEndpoint(raw), Is.EqualTo(expected), $"raw='{raw}'");
    }
}

public class YtDlpClientSettingsValidatorTests
{
    private static YtDlpClientSettings Valid(string downloadPath) => new()
    {
        YtDlpPath = "yt-dlp",
        DownloadPath = downloadPath
    };

    [Test]
    public void Valid_client_settings_pass()
    {
        var result = Valid("/var/lib/lidarr/ytdlp-downloads").Validate();

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public void Missing_download_path_fails()
    {
        var result = Valid(string.Empty).Validate();

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Any(e => e.PropertyName == "DownloadPath"));
    }

    [Test]
    public void Absolute_path_passes_syntactic_validation()
    {
        // IsValidPath is a syntactic CurrentOs check (absolute on Linux), no filesystem access.
        Assert.That(Valid("/nonexistent/downloads/ytdlp").Validate().IsValid, Is.True);
    }

    [Test]
    public void Relative_path_fails_syntactic_validation()
    {
        // Host IsPathValidForNonWindows requires a leading '/', relative paths are rejected.
        Assert.That(Valid("downloads/ytdlp").Validate().IsValid, Is.False);
    }

    [Test]
    public void Shared_rules_apply_to_client_settings_too()
    {
        var s = Valid("/tmp/x");
        s.YtDlpPath = string.Empty;

        var result = s.Validate();

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Any(e => e.PropertyName == "YtDlpPath"),
            "shared yt-dlp rules must apply to the client settings as well");
    }
}
