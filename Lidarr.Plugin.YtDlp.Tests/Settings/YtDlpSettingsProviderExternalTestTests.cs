using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.YtDlp.Settings;

namespace Lidarr.Plugin.YtDlp.Tests.Settings;

/// <summary>
/// Phase 5: the live connection-test hook wired into Validate/Apply —
/// cached per path, bounded, and degrading to warnings on hook crashes.
/// </summary>
public class YtDlpSettingsProviderExternalTestTests
{
    private static Dictionary<string, object?> Dict(string ytDlpPath = "yt-dlp", bool sponsorBlock = false) =>
        new()
        {
            ["ytDlpPath"] = ytDlpPath,
            ["useSponsorBlock"] = sponsorBlock,
            ["reencode"] = "disabled"
        };

    [Test]
    public void No_hook_keeps_pure_behavior()
    {
        var provider = new YtDlpSettingsProvider();
        var result = provider.Validate(Dict());

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void Failing_external_test_fails_validation()
    {
        var provider = new YtDlpSettingsProvider();
        provider.ExternalConnectionTest = _ =>
            PluginValidationResult.Failure(new[] { "yt-dlp not found or not runnable at 'yt-dlp'." });

        var result = provider.Validate(Dict());

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Does.Contain("yt-dlp not found or not runnable at 'yt-dlp'."));
    }

    [Test]
    public void Warnings_from_external_test_are_preserved()
    {
        var provider = new YtDlpSettingsProvider();
        provider.ExternalConnectionTest = _ =>
            PluginValidationResult.Success(new[] { "unexpected yt-dlp --version output" });

        var result = provider.Validate(Dict());

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Warnings, Does.Contain("unexpected yt-dlp --version output"));
    }

    [Test]
    public void Hook_exception_degrades_to_warning()
    {
        var provider = new YtDlpSettingsProvider();
        provider.ExternalConnectionTest = _ => throw new InvalidOperationException("boom");

        var result = provider.Validate(Dict());

        Assert.That(result.IsValid, Is.True, "a crashed live test must not block a save");
        Assert.That(result.Warnings, Does.Contain("connection test failed to run: boom"));
    }

    [Test]
    public void Result_is_cached_per_path()
    {
        var provider = new YtDlpSettingsProvider();
        var calls = 0;
        provider.ExternalConnectionTest = _ =>
        {
            Interlocked.Increment(ref calls);
            return PluginValidationResult.Success();
        };

        provider.Validate(Dict("yt-dlp"));
        provider.Validate(Dict("yt-dlp"));
        provider.Validate(Dict("yt-dlp"));

        Assert.That(calls, Is.EqualTo(1), "same path within TTL must hit the cache");

        // A different path re-probes.
        provider.Validate(Dict("/usr/local/bin/yt-dlp"));
        Assert.That(calls, Is.EqualTo(2));
    }

    [Test]
    public void Hook_is_not_called_without_ytDlpPath()
    {
        var provider = new YtDlpSettingsProvider();
        var calls = 0;
        provider.ExternalConnectionTest = _ =>
        {
            Interlocked.Increment(ref calls);
            return PluginValidationResult.Success();
        };

        var dict = Dict();
        dict["ytDlpPath"] = string.Empty;
        // Pure validation fails (required field) — external must not run anyway.
        var result = provider.Validate(dict);

        Assert.That(calls, Is.EqualTo(0));
        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void Apply_propagates_external_failure_and_does_not_apply()
    {
        var provider = new YtDlpSettingsProvider();
        provider.ExternalConnectionTest = _ =>
            PluginValidationResult.Failure(new[] { "yt-dlp at 'nope' exited with code 127." });

        var result = provider.Apply(Dict("nope"));

        Assert.That(result.IsValid, Is.False);
        Assert.That(provider.Current.YtDlpPath, Is.Not.EqualTo("nope"), "failed save must not mutate state");
    }

    [Test]
    public void Apply_with_passing_external_test_applies()
    {
        var provider = new YtDlpSettingsProvider();
        provider.ExternalConnectionTest = _ => PluginValidationResult.Success();

        var result = provider.Apply(Dict("/ok/path"));

        Assert.That(result.IsValid, Is.True);
        Assert.That(provider.Current.YtDlpPath, Is.EqualTo("/ok/path"));
    }
}
