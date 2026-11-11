using Lidarr.Plugin.YtDlp.Commands;
using Lidarr.Plugin.YtDlp.Settings;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests.Commands;

[TestFixture]
public class YtDlpCommandBuilderTests
{
    private readonly YtDlpCommandBuilder _builder = new();

    private static int Idx(IReadOnlyList<string> list, string token) => list.ToList().IndexOf(token);

    [Test]
    public void BuildVersionArgs_is_minimal()
    {
        Assert.That(_builder.BuildVersionArgs(), Is.EqualTo(new[] { "--version" }));
    }

    [Test]
    public void BuildSearchArgs_uses_ytsearch_prefix_and_print_template()
    {
        var args = _builder.BuildSearchArgs("Daft Punk - Get Lucky", 10);

        Assert.Multiple(() =>
        {
            Assert.That(args[0], Is.EqualTo("--ignore-config"));
            Assert.That(args, Does.Contain("--no-warnings"));
            Assert.That(args, Does.Contain("--no-color"));
            Assert.That(args, Does.Contain("--flat-playlist"));
            Assert.That(args, Does.Contain("--print"));
            Assert.That(args[Idx(args, "--print") + 1], Is.EqualTo(YtDlpCommandBuilder.PrintTemplate));
            Assert.That(args[^1], Is.EqualTo("ytsearch10:Daft Punk - Get Lucky"), "query stays a single token (CliWrap escapes it)");
        });
    }

    [TestCase(0, "ytsearch1:q")]
    [TestCase(1, "ytsearch1:q")]
    [TestCase(500, "ytsearch100:q")]
    public void BuildSearchArgs_clamps_limit(int limit, string expectedPrefix)
    {
        var args = _builder.BuildSearchArgs("q", limit);

        Assert.That(args[^1], Is.EqualTo(expectedPrefix));
    }

    [Test]
    public void BuildAlbumSearchArgs_targets_music_youtube_albums_section()
    {
        var args = _builder.BuildAlbumSearchArgs("Random Access Memories", 25);

        Assert.Multiple(() =>
        {
            Assert.That(args[^1], Is.EqualTo("https://music.youtube.com/search?q=Random%20Access%20Memories#albums"));
            Assert.That(args, Does.Contain("--flat-playlist"));
        });
    }

    [Test]
    public void BuildSingleDownloadArgs_disabled_reencode_keeps_source_audio()
    {
        var args = _builder.BuildSingleDownloadArgs(
            "https://www.youtube.com/watch?v=abc123",
            "/data/album/Get Lucky.%(ext)s",
            new YtDlpDownloadOptions()).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(args, Does.Contain("--ignore-config"));
            Assert.That(args, Does.Contain("--no-warnings"));
            Assert.That(args, Does.Contain("--no-color"));
            Assert.That(args, Does.Contain("--newline"));
            Assert.That(args, Does.Contain("-f"));
            Assert.That(args[Idx(args, "-f") + 1], Is.EqualTo("ba/b"));
            Assert.That(args, Does.Not.Contain("-x"), "no extraction when re-encode disabled");
            Assert.That(args, Does.Not.Contain("--audio-format"));
            Assert.That(args, Does.Contain("--embed-metadata"));
            Assert.That(args, Does.Contain("--no-playlist"));
            Assert.That(args, Does.Not.Contain("--sponsorblock-remove"));
            var oIndex = Idx(args, "-o");
            Assert.That(oIndex, Is.GreaterThan(0));
            Assert.That(args[oIndex + 1], Is.EqualTo("/data/album/Get Lucky.%(ext)s"));
            Assert.That(args, Does.Contain("--sleep-requests"));
            Assert.That(args[Idx(args, "--sleep-requests") + 1], Is.EqualTo("1"));
            Assert.That(args, Does.Contain("--retries"));
            Assert.That(args[Idx(args, "--retries") + 1], Is.EqualTo("10"));
            // URL is last, after the option terminator.
            Assert.That(args[^2], Is.EqualTo("--"));
            Assert.That(args[^1], Is.EqualTo("https://www.youtube.com/watch?v=abc123"));
        });
    }

    [TestCase(ReencodeFormat.Mp3, "mp3")]
    [TestCase(ReencodeFormat.Aac, "m4a")]
    [TestCase(ReencodeFormat.Opus, "opus")]
    [TestCase(ReencodeFormat.Vorbis, "vorbis")]
    public void BuildSingleDownloadArgs_reencode_emits_extraction_flags(ReencodeFormat format, string expectedYtDlpFormat)
    {
        var args = _builder.BuildSingleDownloadArgs(
            "https://www.youtube.com/watch?v=abc123",
            "/data/album/track.%(ext)s",
            new YtDlpDownloadOptions { Reencode = format }).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(args, Does.Contain("-x"));
            Assert.That(args, Does.Contain("--audio-format"));
            Assert.That(args[Idx(args, "--audio-format") + 1], Is.EqualTo(expectedYtDlpFormat));
            Assert.That(args, Does.Contain("--audio-quality"));
            Assert.That(args[Idx(args, "--audio-quality") + 1], Is.EqualTo("0"));
        });
    }

    [Test]
    public void BuildSingleDownloadArgs_sponsorblock_emits_category_set_and_endpoint()
    {
        var args = _builder.BuildSingleDownloadArgs(
            "https://www.youtube.com/watch?v=abc123",
            "/data/album/track.%(ext)s",
            new YtDlpDownloadOptions
            {
                UseSponsorBlock = true,
                SponsorBlockEndpoint = "https://sb.example.com"
            }).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(args, Does.Contain("--sponsorblock-remove"));
            Assert.That(args[Idx(args, "--sponsorblock-remove") + 1],
                Is.EqualTo("music_offtopic,sponsor,intro,outro,selfpromo,preview,interaction,hook"));
            Assert.That(args, Does.Contain("--sponsorblock-api"));
            Assert.That(args[Idx(args, "--sponsorblock-api") + 1], Is.EqualTo("https://sb.example.com"));
        });
    }

    [Test]
    public void BuildSingleDownloadArgs_extra_args_appended_after_defaults_but_before_url()
    {
        var args = _builder.BuildSingleDownloadArgs(
            "https://www.youtube.com/watch?v=abc123",
            "/data/track.%(ext)s",
            new YtDlpDownloadOptions
            {
                ExtraArgs = new[] { "--proxy", "https://host:3128", "--username", "me" }
            }).ToArray();

        Assert.Multiple(() =>
        {
            // User args come after our defaults...
            Assert.That(Idx(args, "--proxy"), Is.GreaterThan(Idx(args, "--sleep-requests")));
            // ...and before the -- url terminator, so they cannot clobber later options.
            Assert.That(Idx(args, "--proxy"), Is.LessThan(Idx(args, "--")));
            Assert.That(args[^3..], Is.EqualTo(new[] { "me", "--", "https://www.youtube.com/watch?v=abc123" }));
        });
    }

    [Test]
    public void BuildPlaylistDownloadArgs_has_archive_break_and_positioned_output()
    {
        var args = _builder.BuildPlaylistDownloadArgs(
            "https://www.youtube.com/playlist?list=PL123",
            "/data/album",
            "/state/PL123.archive",
            new YtDlpDownloadOptions { Reencode = ReencodeFormat.Mp3 }).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(args, Does.Not.Contain("--no-playlist"));
            Assert.That(args, Does.Contain("-o"));
            Assert.That(args[Idx(args, "-o") + 1], Is.EqualTo("/data/album/%(playlist_index)02d - %(title)s.%(ext)s"));
            Assert.That(args, Does.Contain("--download-archive"));
            Assert.That(args[Idx(args, "--download-archive") + 1], Is.EqualTo("/state/PL123.archive"));
            Assert.That(args, Does.Contain("--break-on-existing"));
            Assert.That(args, Does.Contain("--audio-format"));
            Assert.That(args[Idx(args, "--audio-format") + 1], Is.EqualTo("mp3"));
            Assert.That(args[^2], Is.EqualTo("--"));
            Assert.That(args[^1], Is.EqualTo("https://www.youtube.com/playlist?list=PL123"));
        });
    }

    [Test]
    public void BuildPlaylistDownloadArgs_output_dir_trailing_slash_normalized()
    {
        var args = _builder.BuildPlaylistDownloadArgs(
            "https://www.youtube.com/playlist?list=PL123",
            "/data/album/",
            "/state/PL123.archive",
            new YtDlpDownloadOptions()).ToArray();

        Assert.That(args[Idx(args, "-o") + 1], Is.EqualTo("/data/album/%(playlist_index)02d - %(title)s.%(ext)s"));
    }

    [Test]
    public void BuildProbeArgs_simulates_single_video_json()
    {
        var args = _builder.BuildProbeArgs("https://www.youtube.com/watch?v=abc123");

        Assert.That(args, Is.EqualTo(new[]
        {
            "--ignore-config", "--no-warnings", "--no-color", "--simulate", "-J", "--no-playlist",
            "--", "https://www.youtube.com/watch?v=abc123"
        }));
    }

    [Test]
    public void PrintTemplate_is_per_entry_json()
    {
        // One compact JSON object per line; '|' can't be a field separator in a
        // yt-dlp template (reserved) so titles containing '|' would corrupt it.
        Assert.That(YtDlpCommandBuilder.PrintTemplate,
            Is.EqualTo("%(.{id,url,title,channel,duration,type})j"));
    }
}
