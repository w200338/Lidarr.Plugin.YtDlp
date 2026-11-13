using Lidarr.Plugin.YtDlp.Download;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests.Download;

[TestFixture]
public class YtDlpProgressParserTests
{
    private static void Feed(YtDlpProgressParser parser, params string[] lines)
    {
        foreach (var line in lines)
        {
            parser.Feed(line);
        }
    }

    [Test]
    public void Single_track_progress_reaches_100()
    {
        var parser = new YtDlpProgressParser();
        Feed(parser,
            "[download] Destination: /data/Get Lucky.webm",
            "[download]   5.0% of ~ 2.00MiB at 1.00MiB/s ETA 00:02",
            "[download]  50.0% of ~ 2.00MiB at 1.00MiB/s ETA 00:01");

        Assert.Multiple(() =>
        {
            Assert.That(parser.AlbumPercent(1), Is.EqualTo(50.0).Within(0.01));
            Assert.That(parser.CurrentTrack, Is.EqualTo("Get Lucky"));
            Assert.That(parser.FilesStarted, Is.EqualTo(1));
            Assert.That(parser.FilesCompleted, Is.EqualTo(0));
        });

        Feed(parser, "[download] 100% of  1.80MiB in 00:01 at 1.80MiB/s");

        Assert.Multiple(() =>
        {
            Assert.That(parser.AlbumPercent(1), Is.EqualTo(100).Within(0.01));
            Assert.That(parser.FilesCompleted, Is.EqualTo(1));
        });
    }

    [Test]
    public void Multi_track_progress_scales_by_total()
    {
        var parser = new YtDlpProgressParser();
        Feed(parser,
            "[download] Destination: /data/01 - First.webm",
            "[download]  10.0% of ~ 2.00MiB at 1.00MiB/s",
            "[download] 100% of  1.80MiB in 00:01",
            "[ExtractAudio] Destination: /data/01 - First.mp3",
            "[ExtractAudio] Converting audio to mp3",
            "[download] Destination: /data/02 - Second.webm",
            "[download]  50.0% of ~ 3.00MiB at 1.00MiB/s");

        Assert.Multiple(() =>
        {
            Assert.That(parser.FilesStarted, Is.EqualTo(2));
            Assert.That(parser.FilesCompleted, Is.EqualTo(1));
            Assert.That(parser.CurrentTrack, Is.EqualTo("02 - Second"));
            // (1 completed + 0.5 current) / 3 total
            Assert.That(parser.AlbumPercent(3), Is.EqualTo(50.0).Within(0.01));
        });
    }

    [Test]
    public void Without_total_falls_back_to_current_track_percent()
    {
        var parser = new YtDlpProgressParser();
        Feed(parser,
            "[download] Destination: /data/Song.webm",
            "[download]  25.0% of ~ 4.00MiB at 1.00MiB/s");

        Assert.That(parser.AlbumPercent(null), Is.EqualTo(25.0).Within(0.01));
    }

    [Test]
    public void Non_progress_lines_are_ignored()
    {
        var parser = new YtDlpProgressParser();
        Feed(parser,
            "WARNING: You have a very old version of yt-dlp",
            "[youtube] abc123: Downloading webpage",
            "[download] Downloading 1 of 2 format(s)",
            "Extracting audio: warning",
            "");

        Assert.Multiple(() =>
        {
            Assert.That(parser.FilesStarted, Is.EqualTo(0));
            Assert.That(parser.AlbumPercent(3), Is.EqualTo(0));
            Assert.That(parser.CurrentTrack, Is.Null);
        });
    }

    [Test]
    public void Merger_destination_does_not_start_new_track()
    {
        var parser = new YtDlpProgressParser();
        Feed(parser,
            "[download] Destination: /data/Song.webm",
            "[download] 100% of  1.80MiB in 00:01",
            "[Merger] Merging formats into \"/data/Song.webm\"",
            "[ExtractAudio] Destination: \"/data/Song.mp3\"",
            "[ExtractAudio] Converting audio to mp3");

        Assert.Multiple(() =>
        {
            Assert.That(parser.FilesStarted, Is.EqualTo(1));
            Assert.That(parser.FilesCompleted, Is.EqualTo(1));
            Assert.That(parser.CurrentTrack, Is.EqualTo("Song"));
        });
    }

    [Test]
    public void Feed_returns_false_when_nothing_changed()
    {
        var parser = new YtDlpProgressParser();

        Assert.Multiple(() =>
        {
            Assert.That(parser.Feed("WARNING: whatever"), Is.False);
            Assert.That(parser.Feed(""), Is.False);
            Assert.That(parser.Feed("[download] Destination: /data/Song.webm"), Is.True);
            Assert.That(parser.Feed("[download] Destination: /data/Song.webm"), Is.False, "same stem twice = no change");
        });
    }

    [Test]
    public void Progress_line_yields_current_file_size_and_eta()
    {
        var parser = new YtDlpProgressParser();
        Feed(parser,
            "[download] Destination: /data/Song.webm",
            "[download]   5.0% of ~ 2.00MiB at 1.00MiB/s ETA 00:05");

        Assert.Multiple(() =>
        {
            Assert.That(parser.CurrentFileTotalBytes, Is.EqualTo(2L * 1024 * 1024), "2.00MiB");
            Assert.That(parser.CurrentFileRemainingBytes, Is.EqualTo((long)(2L * 1024 * 1024 * 0.95)).Within(2L * 1024), "5% done ≈ 95% left");
            Assert.That(parser.CurrentTrackEta, Is.EqualTo(TimeSpan.FromSeconds(5)));
        });

        Feed(parser, "[download]  50.0% of ~ 2.00MiB at 1.00MiB/s ETA 00:03");

        Assert.Multiple(() =>
        {
            Assert.That(parser.CurrentFileRemainingBytes, Is.EqualTo(2L * 1024 * 1024 / 2).Within(2L * 1024), "50% done ≈ 50% left");
            Assert.That(parser.CurrentTrackEta, Is.EqualTo(TimeSpan.FromSeconds(3)));
        });
    }

    [Test]
    public void File_completion_clears_eta_and_new_file_resets_size()
    {
        var parser = new YtDlpProgressParser();
        Feed(parser,
            "[download] Destination: /data/01 - First.webm",
            "[download]  10.0% of ~ 2.00MiB at 1.00MiB/s ETA 00:04",
            "[download] 100% of  1.80MiB in 00:01 at 1.80MiB/s");

        Assert.That(parser.CurrentTrackEta, Is.Null, "file finished → no eta");

        Feed(parser,
            "[download] Destination: /data/02 - Second.webm",
            "[download]   5.0% of ~ 3.00MiB at 1.00MiB/s ETA 00:02");

        Assert.Multiple(() =>
        {
            Assert.That(parser.CurrentFileTotalBytes, Is.EqualTo(3L * 1024 * 1024), "second file's size replaces the first");
            Assert.That(parser.CurrentTrackEta, Is.EqualTo(TimeSpan.FromSeconds(2)));
        });
    }

    [Test]
    public void Size_units_are_parsed_binary_and_decimal()
    {
        var parser = new YtDlpProgressParser();

        Feed(parser, "[download] Destination: /data/Song.webm");
        Feed(parser, "[download]  10.0% of ~ 1.50GiB at 1.00MiB/s");
        Assert.That(parser.CurrentFileTotalBytes, Is.EqualTo((long)(1.5 * 1024d * 1024 * 1024)), "GiB");

        var parser2 = new YtDlpProgressParser();
        Feed(parser2, "[download] Destination: /data/Song.webm");
        Feed(parser2, "[download]  10.0% of  500KB at 1.00MiB/s");
        Assert.That(parser2.CurrentFileTotalBytes, Is.EqualTo(500_000L), "KB is decimal");
    }
}
