using System.Diagnostics;
using System.Text;
using Lidarr.Plugin.YtDlp.Runner;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests.Runner;

/// <summary>
/// Runs the REAL CliWrapYtDlpRunner against a fake yt-dlp shell script, verifying
/// process start, UTF-8 capture, exit codes, line streaming and cancellation
/// (SIGINT/SIGKILL escalation). Linux-only (needs a shell); CI image is Linux.
/// </summary>
[TestFixture]
public class CliWrapYtDlpRunnerTests
{
    private const string Script = """
        #!/usr/bin/env bash
        case "${1:-}" in
          --version) echo "2025.01.01"; exit 0 ;;
          --fail) echo "ERROR: [generic] simulated failure" >&2; exit 1 ;;
          --stream)
            echo "[download]  10.0% of 5.00MiB in 00:01 at 4.00MiB/s" >&2
            echo "[download]  50.0% of 5.00MiB in 00:02 at 2.00MiB/s" >&2
            echo "[ExtractAudio] Converting audio to m4a — ünïcødé" >&2
            echo "done-line"
            exit 2
            ;;
          --sleep-30) exec sleep 30 ;;
          *) echo "unknown args: $*" >&2; exit 64 ;;
        esac
        """;

    private string _scriptPath = null!;
    private CliWrapYtDlpRunner _runner = null!;

    [SetUp]
    public void SetUp()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Ignore("Integration tests require Linux (bash).");
        }

        _scriptPath = Path.Combine(Path.GetTempPath(), $"fake-ytdlp-{Guid.NewGuid():N}.sh");
        // new UTF8Encoding(false) = no BOM; a BOM before the shebang breaks exec.
        File.WriteAllText(_scriptPath, Script, new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo("chmod", $"+x {_scriptPath}")
        {
            UseShellExecute = false,
            RedirectStandardError = true
        })?.WaitForExit();

        _runner = new CliWrapYtDlpRunner(_scriptPath);
    }

    [TearDown]
    public void TearDown()
    {
        try { File.Delete(_scriptPath); } catch { /* best effort */ }
    }

    [Test]
    public async Task Buffered_runs_version_and_captures_stdout()
    {
        var result = await _runner.RunBufferedAsync(new[] { "--version" }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(0));
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.StandardOutput.Trim(), Is.EqualTo("2025.01.01"));
            Assert.That(result.StandardError, Is.Empty);
        });
    }

    [Test]
    public async Task Buffered_reports_nonzero_exit_without_throwing()
    {
        var result = await _runner.RunBufferedAsync(new[] { "--fail" }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(1));
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.StandardError, Does.Contain("simulated failure"));
        });
    }

    [Test]
    public async Task Buffered_unknown_args_exit_code_is_captured()
    {
        var result = await _runner.RunBufferedAsync(new[] { "--wat" }, CancellationToken.None);

        Assert.That(result.ExitCode, Is.EqualTo(64));
    }

    [Test]
    public async Task Buffered_nonexistent_binary_maps_to_start_failure()
    {
        var runner = new CliWrapYtDlpRunner("/nonexistent/path/yt-dlp");

        var result = await runner.RunBufferedAsync(new[] { "--version" }, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(-1));
            Assert.That(result.StandardError, Does.Contain("Could not start yt-dlp"));
        });
    }

    [Test]
    public async Task Streaming_invokes_line_handlers_and_captures_exit_code()
    {
        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var handlers = new YtDlpStreamHandlers(
            line => { lock (stdoutLines) { stdoutLines.Add(line); } },
            line => { lock (stderrLines) { stderrLines.Add(line); } });

        var result = await _runner.RunStreamingAsync(new[] { "--stream" }, handlers, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ExitCode, Is.EqualTo(2));
            lock (stdoutLines)
            {
                Assert.That(stdoutLines, Is.EqualTo(new[] { "done-line" }));
            }

            lock (stderrLines)
            {
                Assert.That(stderrLines.Count, Is.EqualTo(3));
                Assert.That(stderrLines[0], Does.StartWith("[download]  10.0%"));
                Assert.That(stderrLines[1], Does.StartWith("[download]  50.0%"));
                Assert.That(stderrLines[2], Does.StartWith("[ExtractAudio]"));
            }

            Assert.That(result.StandardOutput, Does.Contain("done-line"));
            Assert.That(result.StandardError, Does.Contain("[download]  10.0%"));
            // UTF-8 must survive the round trip (CliWrap default would mangle it).
            Assert.That(result.StandardError, Does.Contain("ünïcødé"));
        });
    }

    [Test]
    public async Task Streaming_cancel_kills_process_tree_and_throws_OCE()
    {
        var handlers = YtDlpStreamHandlers.Ignore;
        using var cts = new CancellationTokenSource();
        var run = _runner.RunStreamingAsync(new[] { "--sleep-30" }, handlers, cts.Token);

        // Give the process time to start, then cancel.
        await Task.Delay(500);
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () => await run);
    }

    [Test]
    public async Task Buffered_cancel_of_long_running_process_throws_OCE()
    {
        using var cts = new CancellationTokenSource();
        var run = _runner.RunBufferedAsync(new[] { "--sleep-30" }, cts.Token);

        await Task.Delay(300);
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(() => run);
    }
}
