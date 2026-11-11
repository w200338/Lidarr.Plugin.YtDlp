using Lidarr.Plugin.YtDlp.Download;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests.Download;

[TestFixture]
public class YtDlpAlbumIdTests
{
    [TestCase("yt:dQw4w9WgXcQ", YtDlpAlbumKind.SingleTrack, "dQw4w9WgXcQ")]
    [TestCase("playlist:PL1234567890abcdef", YtDlpAlbumKind.Playlist, "PL1234567890abcdef")]
    [TestCase("playlist:PL1234567890abcdef#2", YtDlpAlbumKind.Playlist, "PL1234567890abcdef")]
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

        var singleUrl = YtDlpAlbumId.ToUrl(single);
        var playlistUrl = YtDlpAlbumId.ToUrl(playlist);

        Assert.Multiple(() =>
        {
            Assert.That(singleUrl, Is.EqualTo("https://www.youtube.com/watch?v=dQw4w9WgXcQ"));
            Assert.That(playlistUrl, Is.EqualTo("https://www.youtube.com/playlist?list=PL1234567890abcdef"));
            Assert.That(YtDlpAlbumId.TryFromUrl(singleUrl, out var s2), Is.True);
            Assert.That(s2, Is.EqualTo(single));
            Assert.That(YtDlpAlbumId.TryFromUrl(playlistUrl, out var p2), Is.True);
            Assert.That(p2, Is.EqualTo(playlist));
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
    public void ToString_renders_canonical_token()
    {
        Assert.That(new YtDlpAlbumRef(YtDlpAlbumKind.SingleTrack, "abc").ToString(), Is.EqualTo("yt:abc"));
        Assert.That(new YtDlpAlbumRef(YtDlpAlbumKind.Playlist, "PL1").ToString(), Is.EqualTo("playlist:PL1"));
    }
}
