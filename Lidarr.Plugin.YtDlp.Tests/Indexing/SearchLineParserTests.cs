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
}
