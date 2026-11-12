using Lidarr.Plugin.YtDlp.Indexing;

namespace Lidarr.Plugin.YtDlp.Tests.Indexing;

public class SearchLineParserTests
{
    [Test]
    public void Parses_full_entry()
    {
        const string line =
            """{"id":"abc123","url":"https://www.youtube.com/watch?v=abc123","title":"Song","channel":"Artist","duration":213.5,"type":"video"}""";

        Assert.That(SearchLineParser.TryParseLine(line, out var entry), Is.True);
        Assert.That(entry.Id, Is.EqualTo("abc123"));
        Assert.That(entry.Url, Is.EqualTo("https://www.youtube.com/watch?v=abc123"));
        Assert.That(entry.Title, Is.EqualTo("Song"));
        Assert.That(entry.Channel, Is.EqualTo("Artist"));
        Assert.That(entry.Duration, Is.EqualTo(213.5).Within(0.0001));
        Assert.That(entry.Type, Is.EqualTo("video"));
    }

    [Test]
    public void Handles_pipes_quotes_newlines_and_unicode_in_title()
    {
        const string line =
            """{"id":"v1","url":"https://www.youtube.com/watch?v=v1","title":"Song | Live: \"Acoustic\" — ünïcode\nsecond line","channel":"A | B","duration":10,"type":"video"}""";

        Assert.That(SearchLineParser.TryParseLine(line, out var entry), Is.True);
        Assert.That(entry.Title, Is.EqualTo("Song | Live: \"Acoustic\" — ünïcode\nsecond line"));
        Assert.That(entry.Channel, Is.EqualTo("A | B"));
    }

    [Test]
    public void Missing_or_null_fields_are_absent_not_thrown()
    {
        const string line = """{"id":"v2","url":"https://www.youtube.com/watch?v=v2","title":"T","channel":null,"duration":null}""";

        Assert.That(SearchLineParser.TryParseLine(line, out var entry), Is.True);
        Assert.That(entry.Channel, Is.EqualTo(string.Empty));
        Assert.That(entry.Duration, Is.Null);
        Assert.That(entry.Type, Is.Null);
    }

    [Test]
    public void Integer_duration_is_accepted()
    {
        const string line = """{"id":"v3","url":"u","title":"T","duration":95}""";

        Assert.That(SearchLineParser.TryParseLine(line, out var entry), Is.True);
        Assert.That(entry.Duration, Is.EqualTo(95.0));
    }

    [TestCase("WARNING: [youtube] n entries found")]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("{not json")]
    [TestCase("{\"title\":\"no id\"}")]
    [TestCase("{\"id\":\"\",\"url\":\"u\",\"title\":\"empty id\"}")]
    [TestCase("not a json line at all")]
    public void Non_entry_lines_are_skipped(string line)
    {
        Assert.That(SearchLineParser.TryParseLine(line, out _), Is.False);
    }

    [Test]
    public void ParseAll_keeps_only_valid_entries_in_order()
    {
        var output = string.Join('\n',
            "WARNING: hello",
            """{"id":"a","url":"ua","title":"A","duration":1,"type":"video"}""",
            "",
            """{"id":"b","url":"ub","title":"B"}""",
            "garbage{",
            """{"id":"a","url":"ua","title":"A2"}"""); // duplicate id — parser keeps it; dedup is the indexer's job

        var entries = SearchLineParser.ParseAll(output);

        Assert.That(entries, Has.Count.EqualTo(3));
        Assert.That(entries.Select(e => e.Id), Is.EqualTo(new[] { "a", "b", "a" }));
    }

    [Test]
    public void ParseAll_empty_output_gives_empty_list()
    {
        Assert.That(SearchLineParser.ParseAll(string.Empty), Is.Empty);
        Assert.That(SearchLineParser.ParseAll(null!), Is.Empty);
    }

    // ----- entry inspection (flat re-fetch of browse URLs) -----

    [Test]
    public void InspectLine_strips_album_page_prefix()
    {
        // The music browse page titles albums "Album - <name>" (observed shape).
        Assert.That(SearchLineParser.TryParseInspectLine("{\"playlist_title\":\"Album - 21\",\"playlist_channel\":null}", out var resolution), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(resolution, Is.Not.Null);
            Assert.That(resolution!.Title, Is.EqualTo("21"));
            Assert.That(resolution.Channel, Is.Null);
        });
    }

    [Test]
    public void InspectLine_without_prefix_is_used_verbatim()
    {
        Assert.That(SearchLineParser.TryParseInspectLine("{\"playlist_title\":\"30 \"}", out var resolution), Is.True);
        Assert.That(resolution!.Title, Is.EqualTo("30"), "trimmed");
    }

    [Test]
    public void InspectLine_carries_channel_when_present()
    {
        Assert.That(SearchLineParser.TryParseInspectLine("{\"playlist_title\":\"Mix 1\",\"playlist_channel\":\"Adele\"}", out var resolution), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(resolution!.Title, Is.EqualTo("Mix 1"));
            Assert.That(resolution.Channel, Is.EqualTo("Adele"));
        });
    }

    [Test]
    public void InspectLine_carries_playlist_id_when_present()
    {
        // Observed live: music album browse pages expose the embedded album
        // playlist (OLAK...) which the indexer turns into the canonical
        // playlist download URL.
        Assert.That(SearchLineParser.TryParseInspectLine("{\"playlist_title\":\"Album - 21\",\"playlist_channel\":null,\"playlist_id\":\"OLAK5uy_kNiuS8m9gEwAoTTt62VFPFwPFMdqDC8Oo\"}", out var resolution), Is.True);
        Assert.That(resolution!.PlaylistId, Is.EqualTo("OLAK5uy_kNiuS8m9gEwAoTTt62VFPFwPFMdqDC8Oo"));
    }

    [Test]
    public void InspectLine_without_playlist_id_yields_null_playlist_id()
    {
        Assert.That(SearchLineParser.TryParseInspectLine("{\"playlist_title\":\"Album - 21\"}", out var resolution), Is.True);
        Assert.That(resolution!.PlaylistId, Is.Null);
    }

    [TestCase(null!)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("WARNING: something yt-dlp grumbled")]
    [TestCase("{not json}")]
    [TestCase("{\"title\":\"a plain video line without playlist_title\"}")]
    [TestCase("{\"playlist_title\":\"   \"}")]
    public void InspectLine_rejects_non_inspect_lines(string line)
    {
        Assert.That(SearchLineParser.TryParseInspectLine(line, out var resolution), Is.False);
        Assert.That(resolution, Is.Null);
    }
}
