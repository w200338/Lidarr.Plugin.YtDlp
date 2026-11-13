using Lidarr.Plugin.YtDlp.Probing;
using Lidarr.Plugin.YtDlp.Runner;
using Lidarr.Plugin.YtDlp.Settings;
using Lidarr.Plugin.YtDlp.Tests.Support;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests.Settings;

/// <summary>
/// Phase 7 (native model): the shared YtDlpConnectionTest entry point replaces
/// the old ISettingsProvider.ExternalConnectionTest hook. Both the indexer and
/// the download client route their host Test() through it.
/// </summary>
public class YtDlpConnectionTestTests
{
    private Func<string, IYtDlpRunner>? _previousFactory;

    [SetUp]
    public void SetUp()
    {
        YtDlpConnectionTest.Override = null;
        _previousFactory = YtDlpRunners.Factory;
    }

    [TearDown]
    public void TearDown()
    {
        YtDlpConnectionTest.Override = null;
        YtDlpRunners.Factory = _previousFactory!;
        YtDlpRunners.ClearCache();
    }

    private static YtDlpClientSettings Settings(bool sponsorBlock = false, ReencodeFormat reencode = ReencodeFormat.Disabled) =>
        new()
        {
            YtDlpPath = "yt-dlp",
            UseSponsorBlock = sponsorBlock,
            Reencode = reencode,
            SponsorBlockApiEndpoint = YtDlpSettings.DefaultSponsorBlockEndpoint
        };

    /// <summary>Endpoint as the download client provider would pass it (null unless SponsorBlock is enabled).</summary>
    private static string? SbEndpoint(YtDlpClientSettings s) =>
        s.UseSponsorBlock ? s.SponsorBlockApiEndpoint : null;

    [Test]
    public async Task Override_short_circuits_the_default_pipeline()
    {
        var calls = 0;
        YtDlpConnectionTest.Override = (s, ct) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ConnectionTestResult(
                new[] { new ConnectionTestError("YtDlpPath", "forced failure") },
                Array.Empty<string>()));
        };

        var result = await YtDlpConnectionTest.RunAsync(Settings(), null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(result.Success, Is.False);
            Assert.That(result.Errors[0].Property, Is.EqualTo("YtDlpPath"));
            Assert.That(result.Errors[0].Message, Does.Contain("forced failure"));
        });
    }

    [Test]
    public async Task Passing_override_reports_success()
    {
        YtDlpConnectionTest.Override = (s, ct) =>
            Task.FromResult(new ConnectionTestResult(
                Array.Empty<ConnectionTestError>(),
                new[] { "slow but working" }));

        var result = await YtDlpConnectionTest.RunAsync(Settings(), null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.Warnings, Does.Contain("slow but working"));
        });
    }

    [Test]
    public void Synchronous_run_maps_cancellation_to_timeout_error()
    {
        var canceled = new CancellationTokenSource();
        canceled.Cancel();
        YtDlpConnectionTest.Override = (s, ct) => Task.FromCanceled<ConnectionTestResult>(canceled.Token);

        var result = YtDlpConnectionTest.Run(Settings(), null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Errors, Has.Count.EqualTo(1));
            Assert.That(result.Errors[0].Property, Is.EqualTo("YtDlpPath"));
            Assert.That(result.Errors[0].Message, Does.Contain("timed out"));
        });
    }

    [Test]
    public async Task Default_pipeline_uses_per_path_runner_cache_and_real_probe()
    {
        var fake = new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) => args.Contains("--version")
                ? Task.FromResult(new YtDlpRunResult(0, "2025.01.15\n", string.Empty))
                : Task.FromResult(new YtDlpRunResult(0, string.Empty, string.Empty))
        };
        YtDlpRunners.Factory = _ => fake;

        var result = await YtDlpConnectionTest.RunAsync(Settings(), null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, string.Join("; ", result.Errors.Select(e => e.Message)));
            Assert.That(fake.Calls.Count(c => c.Contains("--version")), Is.EqualTo(1));
        });

        // Same path must hit the cache (one runner instance, not a new one).
        var again = await YtDlpConnectionTest.RunAsync(Settings(), null, CancellationToken.None);
        Assert.That(again.Success, Is.True);
        Assert.That(fake.Calls.Count(c => c.Contains("--version")), Is.EqualTo(2), "second test → second probe");
    }

    [Test]
    public async Task Default_pipeline_fails_when_binary_missing()
    {
        YtDlpRunners.Factory = _ => new FakeYtDlpRunner
        {
            BufferBehavior = (args, ct) =>
            {
                if (args.Contains("--version"))
                {
                    return Task.FromResult(YtDlpRunResult.StartFailed("No such file or directory"));
                }

                return Task.FromResult(new YtDlpRunResult(0, string.Empty, string.Empty));
            }
        };

        var result = await YtDlpConnectionTest.RunAsync(Settings(), null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Errors[0].Property, Is.EqualTo("YtDlpPath"));
            Assert.That(result.Errors[0].Message, Does.Contain("yt-dlp"));
        });
    }
}
