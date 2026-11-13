using FluentValidation.Results;
using Lidarr.Http.ClientSchema;
using Lidarr.Plugin.YtDlp.Settings;
using NSubstitute;
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
    public void Indexer_defaults_match_documented_surface()
    {
        var s = new YtDlpIndexerSettings();

        Assert.Multiple(() =>
        {
            Assert.That(s.YtDlpPath, Is.Empty);
            Assert.That(s.SearchLimit, Is.EqualTo(25));
            Assert.That(s.EarlyReleaseLimit, Is.Null);
            Assert.That(s.BaseUrl, Is.Empty);
        });
    }

    [Test]
    public void Client_defaults_match_documented_surface()
    {
        var s = new YtDlpClientSettings();

        Assert.Multiple(() =>
        {
            Assert.That(s.YtDlpPath, Is.Empty);
            Assert.That(s.AdditionalArgs, Is.Empty);
            Assert.That(s.UseSponsorBlock, Is.False);
            Assert.That(s.SponsorBlockApiEndpoint, Is.EqualTo("https://sponsor.ajay.app"));
            Assert.That(s.Reencode, Is.EqualTo(ReencodeFormat.Disabled));
            Assert.That(s.MinDownloadDelaySeconds, Is.EqualTo(5));
            Assert.That(s.MaxDownloadDelaySeconds, Is.EqualTo(30));
            Assert.That(s.DownloadPath, Is.Empty);
        });
    }

    [Test]
    public void Equality_is_value_based()
    {
        // YtDlpClientSettings only has value-type/string properties, so the
        // property-wise Equals/GetHashCode behave as value equality. (The
        // indexer settings carry the host-required ArtistIds list, a reference
        // type, so its value equality is not instance-independent.)
        var a = new YtDlpClientSettings { YtDlpPath = "yt-dlp", Reencode = ReencodeFormat.Mp3 };
        var b = new YtDlpClientSettings { YtDlpPath = "yt-dlp", Reencode = ReencodeFormat.Mp3 };
        b.Reencode = ReencodeFormat.Aac;

        Assert.Multiple(() =>
        {
            Assert.That(a, Is.EqualTo(new YtDlpClientSettings { YtDlpPath = "yt-dlp", Reencode = ReencodeFormat.Mp3 }));
            Assert.That(a, Is.Not.EqualTo(b));
            Assert.That(a.GetHashCode(), Is.EqualTo(new YtDlpClientSettings { YtDlpPath = "yt-dlp", Reencode = ReencodeFormat.Mp3 }.GetHashCode()));
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

    /// <summary>
    /// Host-form shape via the host's own SchemaBuilder: SearchLimit is an
    /// indexer-only field and must NOT appear in the download client schema
    /// (it did, in v0.2.x, because it lived on the shared settings base).
    /// </summary>
    [Test]
    public void SearchLimit_appears_in_indexer_schema_but_not_client_schema()
    {
        // The host's SchemaBuilder resolves an ILocalizationService from DI;
        // stub it so labels/help text flow through verbatim.
        var localization = Substitute.For<NzbDrone.Core.Localization.ILocalizationService>();
        localization.GetLocalizedString(Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(0));
        localization.GetLocalizedString(Arg.Any<string>(), Arg.Any<Dictionary<string, object>>())
            .Returns(ci => ci.ArgAt<string>(0));
        var container = Substitute.For<DryIoc.IContainer>();
        // DryIoc's generic Resolve<T>() is an extension over IResolver.Resolve(Type, IfUnresolved).
        container.Resolve(typeof(NzbDrone.Core.Localization.ILocalizationService), Arg.Any<DryIoc.IfUnresolved>())
            .Returns(localization);
        SchemaBuilder.Initialize(container);

        // Field names are camelCased by the host schema builder.
        var indexerFields = SchemaBuilder.ToSchema(new YtDlpIndexerSettings()).Select(f => f.Name).ToList();
        var clientFields = SchemaBuilder.ToSchema(new YtDlpClientSettings()).Select(f => f.Name).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(indexerFields.Contains("ytDlpPath"), Is.True, "shared field stays on both forms");
            Assert.That(clientFields.Contains("ytDlpPath"), Is.True);
            Assert.That(indexerFields.Contains("searchLimit"), Is.True, "indexer keeps its search limit");
            Assert.That(clientFields.Contains("searchLimit"), Is.False, "indexer-only SearchLimit must not leak into the download client form");
            Assert.That(clientFields.Contains("reencode"), Is.True, "client keeps its re-encode field");
            Assert.That(indexerFields.Contains("reencode"), Is.False, "download-side field must not leak into the indexer form");
        });
    }

}

public class YtDlpIndexerSettingsValidatorTests
{
    private static YtDlpIndexerSettings Valid() => new()
    {
        YtDlpPath = "yt-dlp",
        SearchLimit = 25
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

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(101)]
    [TestCase(1000)]
    public void Search_limit_out_of_range_fails(int limit)
    {
        var s = Valid();
        s.SearchLimit = limit;

        var result = s.Validate();

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Any(e => e.PropertyName == "SearchLimit"));
    }

    [TestCase(1)]
    [TestCase(100)]
    public void Search_limit_at_bounds_passes(int limit)
    {
        var s = Valid();
        s.SearchLimit = limit;

        Assert.That(s.Validate().IsValid, Is.True);
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

    [Test]
    public void Unmatched_quote_in_additional_args_fails()
    {
        var s = Valid("/tmp/x");
        s.AdditionalArgs = "--cookies \"unterminated";

        var result = s.Validate();

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Any(e => e.PropertyName == "AdditionalArgs"));
    }

    [Test]
    public void Well_formed_additional_args_pass()
    {
        var s = Valid("/tmp/x");
        s.AdditionalArgs = "--cookies \"/home/u/cookies.txt\" --sleep-requests 2";

        Assert.That(s.Validate().IsValid, Is.True);
    }

    [Test]
    public void Min_greater_than_max_delay_fails()
    {
        var s = Valid("/tmp/x");
        s.MinDownloadDelaySeconds = 40;
        s.MaxDownloadDelaySeconds = 30;

        Assert.That(s.Validate().IsValid, Is.False);
    }

    [Test]
    public void Equal_delays_pass()
    {
        var s = Valid("/tmp/x");
        s.MinDownloadDelaySeconds = 0;
        s.MaxDownloadDelaySeconds = 0;

        Assert.That(s.Validate().IsValid, Is.True);
    }

    [Test]
    public void SponsorBlock_enabled_rejects_non_http_endpoint()
    {
        var s = Valid("/tmp/x");
        s.UseSponsorBlock = true;
        s.SponsorBlockApiEndpoint = "file:///etc/passwd";

        var result = s.Validate();

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Any(e => e.PropertyName == "SponsorBlockApiEndpoint"));
    }

    [Test]
    public void SponsorBlock_enabled_accepts_scheme_less_endpoint()
    {
        var s = Valid("/tmp/x");
        s.UseSponsorBlock = true;
        s.SponsorBlockApiEndpoint = "sponsor.ajay.app";

        Assert.That(s.Validate().IsValid, Is.True);
    }

    [Test]
    public void SponsorBlock_disabled_tolerates_garbage_endpoint()
    {
        var s = Valid("/tmp/x");
        s.UseSponsorBlock = false;
        s.SponsorBlockApiEndpoint = "not a url at all";

        Assert.That(s.Validate().IsValid, Is.True);
    }

    [Test]
    public void SponsorBlock_enabled_rejects_empty_endpoint()
    {
        var s = Valid("/tmp/x");
        s.UseSponsorBlock = true;
        s.SponsorBlockApiEndpoint = string.Empty;

        Assert.That(s.Validate().IsValid, Is.False);
    }
}
