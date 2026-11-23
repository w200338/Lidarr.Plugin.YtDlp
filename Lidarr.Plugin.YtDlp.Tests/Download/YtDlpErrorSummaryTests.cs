using Lidarr.Plugin.YtDlp.Download;

namespace Lidarr.Plugin.YtDlp.Tests.Download;

/// <summary>
/// Unit tests for <see cref="YtDlpErrorSummary.Parse"/> (host-free, no runner
/// dependency): line-shape parsing, anchoring, dedup and the maxListed cap.
/// </summary>
public class YtDlpErrorSummaryTests
{
    [Test]
    public void Parse_extracts_video_id_and_message()
    {
        var summary = YtDlpErrorSummary.Parse(new[]
        {
            "ERROR: [youtube] Knt8RVzBiuU: Video unavailable. This video is not available"
        });

        Assert.Multiple(() =>
        {
            Assert.That(summary.TotalErrors, Is.EqualTo(1));
            Assert.That(summary.Errors, Has.Count.EqualTo(1));
            Assert.That(summary.Errors[0].VideoId, Is.EqualTo("Knt8RVzBiuU"));
            Assert.That(summary.Errors[0].Message,
                Is.EqualTo("Video unavailable. This video is not available"));
        });
    }

    [Test]
    public void Parse_extracts_message_without_video_id()
    {
        var summary = YtDlpErrorSummary.Parse(new[]
        {
            "ERROR: RequestBlocked: bot check"
        });

        Assert.Multiple(() =>
        {
            Assert.That(summary.TotalErrors, Is.EqualTo(1));
            Assert.That(summary.Errors[0].VideoId, Is.Null);
            Assert.That(summary.Errors[0].Message, Is.EqualTo("RequestBlocked: bot check"));
        });
    }

    [Test]
    public void Parse_keeps_colons_inside_the_message()
    {
        var summary = YtDlpErrorSummary.Parse(new[]
        {
            "ERROR: [youtube] abc123: something failed: retrying: later"
        });

        Assert.Multiple(() =>
        {
            Assert.That(summary.Errors[0].VideoId, Is.EqualTo("abc123"));
            Assert.That(summary.Errors[0].Message, Is.EqualTo("something failed: retrying: later"));
        });
    }

    [Test]
    public void Parse_ignores_non_error_lines_and_error_not_at_start()
    {
        var summary = YtDlpErrorSummary.Parse(new[]
        {
            "[download] 100.0% of 10.00KiB",
            "WARNING: [youtube] ERROR: this is not a real error marker",
            "some text ERROR: [youtube] abc: embedded error",
            ""
        });

        Assert.That(summary.TotalErrors, Is.Zero, "only lines starting with ERROR: count");
        Assert.That(summary.Errors, Has.Count.Zero);
    }

    [Test]
    public void Parse_deduplicates_identical_pairs_keeps_first_seen_order()
    {
        var summary = YtDlpErrorSummary.Parse(new[]
        {
            "ERROR: [youtube] aaa: boom",
            "ERROR: [youtube] aaa: boom", // identical -> ignored
            "ERROR: [youtube] bbb: boom", // same message, different id -> distinct
            "ERROR: RequestBlocked: bot check"
        });

        Assert.Multiple(() =>
        {
            Assert.That(summary.TotalErrors, Is.EqualTo(3));
            Assert.That(summary.Errors, Has.Count.EqualTo(3));
            Assert.That(summary.Errors[0].VideoId, Is.EqualTo("aaa"));
            Assert.That(summary.Errors[1].VideoId, Is.EqualTo("bbb"));
            Assert.That(summary.Errors[2].VideoId, Is.Null);
        });
    }

    [Test]
    public void Parse_caps_listed_errors_at_maxListed_while_counting_all()
    {
        var lines = new[]
        {
            "ERROR: [youtube] a1: one",
            "ERROR: [youtube] a2: two",
            "ERROR: [youtube] a3: three",
            "ERROR: [youtube] a4: four",
            "ERROR: [youtube] a5: five"
        };

        var summary = YtDlpErrorSummary.Parse(lines, maxListed: 3);

        Assert.Multiple(() =>
        {
            Assert.That(summary.TotalErrors, Is.EqualTo(5), "TotalErrors counts every distinct error");
            Assert.That(summary.Errors, Has.Count.EqualTo(3), "only maxListed are listed");
            Assert.That(summary.Errors[0].VideoId, Is.EqualTo("a1"));
            Assert.That(summary.Errors[2].VideoId, Is.EqualTo("a3"));
        });
    }

    [Test]
    public void Parse_empty_input_yields_zero_errors()
    {
        var summary = YtDlpErrorSummary.Parse(Array.Empty<string>());

        Assert.Multiple(() =>
        {
            Assert.That(summary.TotalErrors, Is.Zero);
            Assert.That(summary.Errors, Has.Count.Zero);
        });
    }

    [Test]
    public void Parse_null_lines_throws()
    {
        Assert.Throws<ArgumentNullException>(() => YtDlpErrorSummary.Parse(null!));
    }

    [Test]
    public void Parse_skips_null_lines_without_throwing()
    {
        var summary = YtDlpErrorSummary.Parse(new string?[]
        {
            null,
            "ERROR: [youtube] aaa: boom",
            null
        }!);

        Assert.Multiple(() =>
        {
            Assert.That(summary.TotalErrors, Is.EqualTo(1));
            Assert.That(summary.Errors[0].VideoId, Is.EqualTo("aaa"));
        });
    }

    [Test]
    public void Parse_negative_maxListed_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => YtDlpErrorSummary.Parse(Array.Empty<string>(), -1));
    }
}
