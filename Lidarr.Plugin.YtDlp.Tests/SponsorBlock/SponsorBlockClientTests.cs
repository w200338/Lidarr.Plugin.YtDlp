using System.Net;
using System.Text;
using Lidarr.Plugin.YtDlp.SponsorBlock;

namespace Lidarr.Plugin.YtDlp.Tests.SponsorBlock;

/// <summary>Scriptable HttpMessageHandler capturing the request for assertions.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }

    /// <summary>(StatusCode, body) to return; body null → empty.</summary>
    public (HttpStatusCode Status, string? Body) Response { get; set; } = (HttpStatusCode.OK, "{}");

    /// <summary>Delay before responding (ms); null = respond immediately.</summary>
    public int? DelayMs { get; set; }

    /// <summary>Throw this instead of responding (e.g. DNS failure).</summary>
    public Exception? Throw { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;

        if (DelayMs is { } delay)
        {
            await Task.Delay(delay, cancellationToken);
        }

        if (Throw is { } ex)
        {
            throw ex;
        }

        return new HttpResponseMessage(Response.Status)
        {
            Content = new StringContent(Response.Body ?? string.Empty, Encoding.UTF8, "application/json")
        };
    }
}

public class SponsorBlockClientTests
{
    private static (SponsorBlockClient Client, FakeHttpHandler Handler) Build(int? delayMs = null)
    {
        var handler = new FakeHttpHandler { DelayMs = delayMs };
        var client = new SponsorBlockClient(new HttpClient(handler), TimeSpan.FromSeconds(10));
        return (client, handler);
    }

    [Test]
    public async Task CheckEndpoint_200_with_uptime_passes()
    {
        var (client, handler) = Build();
        handler.Response = (HttpStatusCode.OK, """{"uptime":1234,"commit":"abc","hostname":"x"}""");

        var result = await client.CheckEndpointAsync("https://sponsor.ajay.app", CancellationToken.None);

        Assert.That(result.Ok, Is.True);
        Assert.That(result.IsWarning, Is.False);
        Assert.That(handler.LastRequest!.RequestUri!.ToString(),
            Is.EqualTo("https://sponsor.ajay.app/api/status"));
    }

    [Test]
    public async Task CheckEndpoint_schemeless_endpoint_gets_https()
    {
        var (client, handler) = Build();

        _ = await client.CheckEndpointAsync("sponsor.ajay.app", CancellationToken.None);

        Assert.That(handler.LastRequest!.RequestUri!.Scheme, Is.EqualTo("https"));
        Assert.That(handler.LastRequest.RequestUri!.Authority, Is.EqualTo("sponsor.ajay.app"));
        Assert.That(handler.LastRequest.RequestUri!.PathAndQuery, Is.EqualTo("/api/status"));
    }

    [Test]
    public async Task CheckEndpoint_trailing_slash_is_stripped()
    {
        var (client, handler) = Build();

        _ = await client.CheckEndpointAsync("https://sb.example.com///", CancellationToken.None);

        Assert.That(handler.LastRequest!.RequestUri!.ToString(),
            Is.EqualTo("https://sb.example.com/api/status"));
    }

    [Test]
    public async Task CheckEndpoint_404_message()
    {
        var (client, handler) = Build();
        handler.Response = (HttpStatusCode.NotFound, null);

        var result = await client.CheckEndpointAsync("https://bad.example", CancellationToken.None);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("404"));
        Assert.That(result.Error, Does.Contain("does not look like a SponsorBlock server"));
    }

    [Test]
    public async Task CheckEndpoint_500_message()
    {
        var handler = new FakeHttpHandler { Response = (HttpStatusCode.InternalServerError, null) };
        using var client = new SponsorBlockClient(new HttpClient(handler));

        var result = await client.CheckEndpointAsync("https://bad.example", CancellationToken.None);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("500"));
    }

    [Test]
    public async Task CheckEndpoint_200_without_uptime_warns()
    {
        var (client, handler) = Build();
        handler.Response = (HttpStatusCode.OK, """{"foo":"bar"}""");

        var result = await client.CheckEndpointAsync("https://sponsor.ajay.app", CancellationToken.None);

        Assert.That(result.Ok, Is.True);
        Assert.That(result.IsWarning, Is.True);
        Assert.That(result.Error, Does.Contain("uptime"));
    }

    [Test]
    public async Task CheckEndpoint_invalid_json_fails()
    {
        var (client, handler) = Build();
        handler.Response = (HttpStatusCode.OK, "not-json");

        var result = await client.CheckEndpointAsync("https://sponsor.ajay.app", CancellationToken.None);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("invalid JSON"));
    }

    [Test]
    public async Task CheckEndpoint_unreachable_fails_with_message()
    {
        var handler = new FakeHttpHandler { Throw = new HttpRequestException("Name or service not known") };
        using var client = new SponsorBlockClient(new HttpClient(handler));

        var result = await client.CheckEndpointAsync("https://nope.example", CancellationToken.None);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("unreachable"));
        Assert.That(result.Error, Does.Contain("Name or service not known"));
    }

    [Test]
    public async Task CheckEndpoint_timeout_fails()
    {
        var handler = new FakeHttpHandler { DelayMs = 5_000 };
        using var fastClient = new SponsorBlockClient(new HttpClient(handler), TimeSpan.FromMilliseconds(50));

        var result = await fastClient.CheckEndpointAsync("https://slow.example", CancellationToken.None);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("timed out"));
    }

    [Test]
    public async Task CheckEndpoint_empty_endpoint_fails_without_request()
    {
        var (client, handler) = Build();

        var result = await client.CheckEndpointAsync("   ", CancellationToken.None);

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Error, Does.Contain("empty"));
        Assert.That(handler.LastRequest, Is.Null);
    }

    [Test]
    public async Task GetSegments_200_parses_segments()
    {
        var (client, handler) = Build();
        handler.Response = (HttpStatusCode.OK,
            """[{"category":"music_offtopic","actionType":"skip","segment":[0,4.013],"UUID":"abc","videoDuration":252.241,"locked":1,"votes":8,"description":""}]""");

        var segments = await client.GetSegmentsAsync(
            "https://sponsor.ajay.app", "9bZkp7q19f0",
            ["music_offtopic", "sponsor"], CancellationToken.None);

        Assert.That(segments, Has.Count.EqualTo(1));
        Assert.That(segments[0].Category, Is.EqualTo("music_offtopic"));
        Assert.That(segments[0].Start, Is.EqualTo(0).Within(0.0001));
        Assert.That(segments[0].End, Is.EqualTo(4.013).Within(0.0001));
        Assert.That(segments[0].Uuid, Is.EqualTo("abc"));

        // Query shape: videoID + JSON-encoded categories array.
        var query = handler.LastRequest!.RequestUri!.Query;
        Assert.That(query, Does.Contain("videoID=9bZkp7q19f0"));
        Assert.That(query, Does.Contain("categories="));
        Assert.That(query, Does.Contain("music_offtopic"));
        Assert.That(query, Does.Contain("sponsor"));
        // categories must be a JSON array (escaped)
        var decoded = Uri.UnescapeDataString(query.Split("categories=", 2)[1]);
        Assert.That(decoded, Does.StartWith("["));
    }

    [Test]
    public async Task GetSegments_404_means_no_segments_not_error()
    {
        var (client, handler) = Build();
        handler.Response = (HttpStatusCode.NotFound, null);

        var segments = await client.GetSegmentsAsync(
            "https://sponsor.ajay.app", "noSegments1", ["sponsor"], CancellationToken.None);

        Assert.That(segments, Is.Empty);
    }

    [Test]
    public async Task GetSegments_500_throws()
    {
        var (client, handler) = Build();
        handler.Response = (HttpStatusCode.InternalServerError, null);

        Assert.That(async () => await client.GetSegmentsAsync(
            "https://sponsor.ajay.app", "abc", ["sponsor"], CancellationToken.None),
            Throws.TypeOf<HttpRequestException>());
    }

    [TestCase("sponsor.ajay.app", "https://sponsor.ajay.app")]
    [TestCase("https://sponsor.ajay.app", "https://sponsor.ajay.app")]
    [TestCase("https://sponsor.ajay.app/", "https://sponsor.ajay.app")]
    [TestCase("  http://local:3000/  ", "http://local:3000")]
    public void NormalizeEndpoint_shapes_input(string input, string expected)
    {
        Assert.That(SponsorBlockClient.NormalizeEndpoint(input), Is.EqualTo(expected));
    }

    [Test]
    public void NormalizeEndpoint_empty_is_null()
    {
        Assert.That(SponsorBlockClient.NormalizeEndpoint("  "), Is.Null);
        Assert.That(SponsorBlockClient.NormalizeEndpoint(null), Is.Null);
    }
}
