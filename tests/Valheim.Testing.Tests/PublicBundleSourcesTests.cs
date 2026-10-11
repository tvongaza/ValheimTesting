using System.Net;
using Valheim.Testing.Bundles;
using Xunit;

public sealed class PublicBundleSourcesTests
{
    [Fact]
    public void A_304_is_followed_by_one_unconditional_fresh_get()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.NotModified, HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var versions = new PublicBundleSources(http).PackageVersions("Valheim.Testing.Game");

        Assert.Equal(["0.1.0-preview.16"], versions);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://api.nuget.org/v3-flatcontainer/valheim.testing.game/index.json", handler.Requests[0].Url);
        Assert.False(handler.Requests[0].NoCache);
        Assert.True(handler.Requests[1].NoCache);
        Assert.Equal("no-cache", handler.Requests[1].Pragma);
        Assert.All(handler.Requests, request => Assert.False(request.Conditional));
    }

    [Fact]
    public void Repeated_304_fails_with_the_package_name_and_a_bounded_attempt_count()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.NotModified, HttpStatusCode.NotModified);
        using var http = new HttpClient(handler);

        var error = Assert.Throws<HttpRequestException>(() => new PublicBundleSources(http).PackageVersions("Valheim.Testing.Game"));
        Assert.Contains("304 Not Modified twice", error.Message);
        Assert.Contains("Valheim.Testing.Game", error.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void A_404_is_absence_without_a_retry()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.NotFound);
        using var http = new HttpClient(handler);

        Assert.Null(new PublicBundleSources(http).PackageVersions("Missing.Package"));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void A_304_then_server_error_fails_with_the_final_status()
    {
        using var handler = new ScriptedHandler(HttpStatusCode.NotModified, HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler);

        var error = Assert.Throws<HttpRequestException>(() => new PublicBundleSources(http).PackageVersions("Valheim.Testing.Game"));
        Assert.Contains("503", error.Message);
        Assert.Contains("Valheim.Testing.Game", error.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    private sealed class ScriptedHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _statuses = new(statuses);
        public List<(string Url, bool NoCache, string Pragma, bool Conditional)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri,
                request.Headers.CacheControl?.NoCache == true,
                string.Join(",", request.Headers.Pragma.Select(value => value.Name)),
                request.Headers.IfModifiedSince != null || request.Headers.IfNoneMatch.Count != 0));
            var status = _statuses.Dequeue();
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
                response.Content = new StringContent("{\"versions\":[\"0.1.0-preview.16\"]}");
            return Task.FromResult(response);
        }
    }
}
