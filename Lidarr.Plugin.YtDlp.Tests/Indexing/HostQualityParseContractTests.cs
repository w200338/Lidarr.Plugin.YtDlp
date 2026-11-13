using NzbDrone.Core.Qualities;
using NUnit.Framework;

namespace Lidarr.Plugin.YtDlp.Tests.Indexing;

/// <summary>
/// Contract against the REAL host (v3.1.6.5079) Parser, which the test project
/// references directly. The host derives release quality from the release TITLE
/// (not from the plugin's ReleaseInfo), so the tokens the indexer appends for
/// re-encode delivery modes must parse to concrete host qualities — otherwise
/// the release shows "Unknown" and profiles that disallow Unknown reject it.
/// </summary>
public class HostQualityParseContractTests
{
    private static NzbDrone.Core.Parser.Model.ParsedAlbumInfo Parse(string title) =>
        NzbDrone.Core.Parser.Parser.ParseAlbumTitle(title)!;

    [Test]
    public void Mp3_reencode_token_maps_to_host_mp3_vbr_quality()
    {
        var parsed = Parse("Adele - 21 (MP3 VBR V0)");

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.Not.Null, "host failed to parse the title");
            Assert.That(parsed!.Quality.Quality.Id, Is.EqualTo(Quality.MP3_VBR.Id), "host MP3-VBR-V0 quality (allowed by lossy profiles)");
        });
    }

    [Test]
    public void Aac_reencode_token_maps_to_host_aac_vbr_quality()
    {
        var parsed = Parse("Adele - 21 (AAC VBR)");

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.Not.Null, "host failed to parse the title");
            Assert.That(parsed!.Quality.Quality.Id, Is.EqualTo(Quality.AAC_VBR.Id), "host AAC-VBR quality");
        });
    }

    [Test]
    public void Vorbis_reencode_token_maps_to_host_vorbis_q10_quality()
    {
        var parsed = Parse("Adele - 21 (VORBIS Q10)");

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.Not.Null, "host failed to parse the title");
            Assert.That(parsed!.Quality.Quality.Id, Is.EqualTo(Quality.VORBIS_Q10.Id), "host OGG Vorbis Q10 quality");
        });
    }

    [Test]
    public void Plain_title_parses_as_unknown_via_search_criteria_fallback()
    {
        // Baseline (what v0.2.x produced): a plain "Artist - Album" title fails
        // standalone parsing, so the host falls back to
        // ParseAlbumTitleWithSearchCriteria (DownloadDecisionMaker), which
        // yields Unknown quality.
        var parsed = NzbDrone.Core.Parser.Parser.ParseAlbumTitleWithSearchCriteria(
            "Adele - 21",
            new NzbDrone.Core.Music.Artist { Name = "Adele" },
            new List<NzbDrone.Core.Music.Album> { new() { Title = "21" } });

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.Not.Null);
            Assert.That(parsed!.Quality.Quality.Id, Is.EqualTo(Quality.Unknown.Id));
        });
    }

    [Test]
    public void Quality_suffix_survives_the_search_criteria_fallback_too()
    {
        // The decision engine only reaches the criteria fallback when the
        // standalone parse fails; when it succeeds (our suffixed titles), the
        // standalone quality must already be the right one (tested above).
        // Here: the fallback path also carries the token through.
        var parsed = NzbDrone.Core.Parser.Parser.ParseAlbumTitleWithSearchCriteria(
            "Adele - 21 (MP3 VBR V0)",
            new NzbDrone.Core.Music.Artist { Name = "Adele" },
            new List<NzbDrone.Core.Music.Album> { new() { Title = "21" } });

        Assert.Multiple(() =>
        {
            Assert.That(parsed, Is.Not.Null);
            Assert.That(parsed!.Quality.Quality.Id, Is.EqualTo(Quality.MP3_VBR.Id));
        });
    }
}
