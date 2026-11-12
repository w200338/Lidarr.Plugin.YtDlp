using Lidarr.Plugin.YtDlp.Download;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests.Download;

[TestFixture]
public class YtDlpAlbumIdTests
{
    [TestCase("yt:dQw4w9WgXcQ", YtDlpAlbumKind.SingleTrack, "dQw4w9WgXcQ")]
    [TestCase("playlist:PL1234567890abcdef", YtDlpAlbumKind.Playlist, "PL1234567890abcdef")]
    [TestCase("playlist:PL1234567890abcdef#2", YtDlpAlbumKind.Playlist, "PL1234567890abcdef")]
    [TestCase("browse:MPREb_IRSjexVmMMl", YtDlpAlbumKind.Browse, "MPREb_IRSjexVmMMl")]
    [TestCase(" yt:dQw4w9WgXcQ ", YtDlpAlbumKind.SingleTrack, "dQw4w9WgXcQ")]
    [TestCase("yt:abc-def_012", YtDlpAlbumKind.SingleTrack, "abc-def_012")]
    public void TryParse_valid_tokens(string token, YtDlpAlbumKind kind, string id)
    {
        Assert.That(YtDlpAlbumId.TryParse(token, out var result), Is.True);
        Assert.That(result.Kind, Is.EqualTo(kind));
        Assert.That(result.Id, Is.EqualTo(id));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("yt:")]
    [TestCase("playlist:")]
    [TestCase("yt:has space")]
    [TestCase("yt:bad/id")]
    [TestCase("playlist:bad/id")]
    [TestCase("playlist:abc#")]
    [TestCase("playlist:abc#xy")]
    [TestCase("browse:")]
    [TestCase("browse:bad/id")]
    [TestCase("watch?v=dQw4w9WgXcQ")]
    [TestCase("https://www.youtube.com/watch?v=abc")]
    [TestCase("yt:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void TryParse_invalid_tokens(string? token)
    {
        Assert.That(YtDlpAlbumId.TryParse(token, out _), Is.False);
    }

    [Test]
    public void RoundTrip_token_to_url_and_back()
    {
        var single = new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, "dQw4w9WgXcQ");
        var playlist = new YtDlpAlbumRef(YtDlpAlbumKind.Playlist, "PL1234567890abcdef");
        var browse = new YtDlpAlbumRef(YtDlpAlbumKind.Browse, "MPREb_IRSjexVmMMl");

        var singleUrl = YtDlpAlbumId.ToUrl(single);
        var playlistUrl = YtDlpAlbumId.ToUrl(playlist);
        var browseUrl = YtDlpAlbumId.ToUrl(browse);

        Assert.Multiple(() =>
        {
            Assert.That(singleUrl, Is.EqualTo("https://www.youtube.com/watch?v=dQw4w9WgXcQ"));
            Assert.That(playlistUrl, Is.EqualTo("https://www.youtube.com/playlist?list=PL1234567890abcdef"));
            Assert.That(browseUrl, Is.EqualTo("https://music.youtube.com/browse/MPREb_IRSjexVmMMl"));
            Assert.That(YtDlpAlbumId.TryFromUrl(singleUrl, out var s2), Is.True);
            Assert.That(s2, Is.EqualTo(single));
            Assert.That(YtDlpAlbumId.TryFromUrl(playlistUrl, out var p2), Is.True);
            Assert.That(p2, Is.EqualTo(playlist));
            Assert.That(YtDlpAlbumId.TryFromUrl(browseUrl, out var b2), Is.True);
            Assert.That(b2, Is.EqualTo(browse));
        });
    }

    [Test]
    public void TryFromUrl_handles_short_link_and_query_stripping()
    {
        Assert.Multiple(() =>
        {
            Assert.That(YtDlpAlbumId.TryFromUrl("https://youtu.be/dQw4w9WgXcQ", out var short1), Is.True);
            Assert.That(short1.Id, Is.EqualTo("dQw4w9WgXcQ"));
            Assert.That(YtDlpAlbumId.TryFromUrl("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42s", out var withQuery), Is.True);
            Assert.That(withQuery.Id, Is.EqualTo("dQw4w9WgXcQ"));
            Assert.That(YtDlpAlbumId.TryFromUrl("https://www.youtube.com/playlist?list=PL1234567890abcdef&hl=en", out var playlistQuery), Is.True);
            Assert.That(playlistQuery.Id, Is.EqualTo("PL1234567890abcdef"));
            Assert.That(YtDlpAlbumId.TryFromUrl("https://example.com/x", out _), Is.False);
        });
    }

    [Test]
    public void TryFromUrl_accepts_music_browse_album_urls()
    {
        // The indexer's album search emits these when the flat re-fetch could
        // not resolve the canonical playlist id; yt-dlp downloads the album
        // playlist behind a browse page directly, so the client must accept it.
        Assert.Multiple(() =>
        {
            Assert.That(YtDlpAlbumId.TryFromUrl("https://music.youtube.com/browse/MPREb_IRSjexVmMMl", out var browse), Is.True);
            Assert.That(browse.Kind, Is.EqualTo(YtDlpAlbumKind.Browse));
            Assert.That(browse.Id, Is.EqualTo("MPREb_IRSjexVmMMl"));
            // Query strings on browse links are stripped.
            Assert.That(YtDlpAlbumId.TryFromUrl("https://music.youtube.com/browse/MPREb_IRSjexVmMMl?foo=bar", out var withQuery), Is.True);
            Assert.That(withQuery.Id, Is.EqualTo("MPREb_IRSjexVmMMl"));
        });
    }

    [Test]
    public void ToString_renders_canonical_token()
    {
        Assert.That(new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, "abc").ToString(), Is.EqualTo("yt:abc"));
        Assert.That(new YtDlpAlbumRef(YtDlpAlbumKind.Playlist, "PL1").ToString(), Is.EqualTo("playlist:PL1"));
        Assert.That(new YtDlpAlbumRef(YtDlpAlbumKind.Browse, "MPREb_1").ToString(), Is.EqualTo("browse:MPREb_1"));
    }
}
