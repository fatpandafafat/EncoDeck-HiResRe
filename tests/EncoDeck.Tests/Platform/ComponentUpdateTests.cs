// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Text.Json.Nodes;
using EncoDeck.Platform.Components;
using EncoDeck.Platform.Updates;

namespace EncoDeck.Tests.Platform;

public sealed class ComponentUpdateTests
{
    private const string Commit = "3a094af733c830a2f4f5bd7418e6c7ba1db94749";
    private static readonly string Sha = new('a', 64);

    private static JsonObject Description(string version, int contract = 1, string? minApp = "1.11.0", long size = 1000, string? sha = null) => new()
    {
        ["schema"] = 1,
        ["id"] = "a2dpwb",
        ["version"] = version,
        ["tag"] = "a2dpwb-v" + version,
        ["interface"] = contract,
        ["minApp"] = minApp,
        ["source"] = new JsonObject { ["repository"] = "https://github.com/SeiyaFunaokaJP/A2DP-Windows-Bridge", ["commit"] = Commit, ["release"] = "v" + version[..5] },
        ["packages"] = new JsonArray(new JsonObject
        {
            ["architecture"] = "x64", ["file"] = $"A2DPWB-{version[..5]}-win64-portable.zip", ["size"] = size, ["sha256"] = sha ?? Sha, ["root"] = $"A2DPWB-{version[..5]}",
        }),
    };

    /// <summary>A release as the GitHub API lists it; <paramref name="assetSha"/> is the digest GitHub reports for the package.</summary>
    private static JsonObject Release(string version, bool prerelease = false, string? assetSha = null, long assetSize = 1000, bool manifest = true)
    {
        var assets = new JsonArray(new JsonObject
        {
            ["name"] = $"A2DPWB-{version[..5]}-win64-portable.zip", ["size"] = assetSize, ["digest"] = "sha256:" + (assetSha ?? Sha),
            ["browser_download_url"] = $"https://github.com/TIANLI0/EncoDeck/releases/download/a2dpwb-v{version}/A2DPWB-{version[..5]}-win64-portable.zip",
        });
        if (manifest)
            assets.Add(new JsonObject
            {
                ["name"] = "component.json", ["size"] = 900, ["digest"] = "sha256:" + new string('b', 64),
                ["browser_download_url"] = $"https://github.com/TIANLI0/EncoDeck/releases/download/a2dpwb-v{version}/component.json",
            });
        return new JsonObject { ["tag_name"] = "a2dpwb-v" + version, ["draft"] = false, ["prerelease"] = prerelease, ["assets"] = assets };
    }

    private static ComponentUpdateClient Client(JsonArray releases, Dictionary<string, JsonObject> descriptions, string app = "1.11.0", HttpStatusCode list = HttpStatusCode.OK) =>
        new(new HttpClient(new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.github.com") return new HttpResponseMessage(list) { Content = new StringContent(releases.ToJsonString()) };
            string tag = uri.Segments[^2].TrimEnd('/');
            return descriptions.TryGetValue(tag, out var d)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(d.ToJsonString()) }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        })), SemanticVersion.Parse(app));

    [Fact]
    public async Task OffersTheNewestConsistentReleaseAboveTheFloor()
    {
        var releases = new JsonArray(Release("2.0.2-r1"), Release("2.0.1-r2"), Release("2.0.1-r1"),
            new JsonObject { ["tag_name"] = "v1.11.0", ["draft"] = false, ["prerelease"] = false, ["assets"] = new JsonArray() },
            new JsonObject { ["tag_name"] = "lhdc-v0.9.0-r1", ["draft"] = false, ["prerelease"] = false, ["assets"] = new JsonArray() });
        var client = Client(releases, new()
        {
            ["a2dpwb-v2.0.2-r1"] = Description("2.0.2-r1"),
            ["a2dpwb-v2.0.1-r2"] = Description("2.0.1-r2"),
        });
        var result = await client.CheckAsync("a2dpwb", 1, "2.0.1-r1", includePrerelease: false, TestContext.Current.CancellationToken);
        Assert.Equal(ComponentUpdateOutcome.Available, result.Outcome);
        Assert.Equal("2.0.2-r1", result.Release?.Version);
        Assert.Equal(1000, result.Release?.Packages[0].Size);

        var none = await client.CheckAsync("a2dpwb", 1, "2.0.2-r1", includePrerelease: false, TestContext.Current.CancellationToken);
        Assert.Equal(ComponentUpdateOutcome.UpToDate, none.Outcome);
    }

    [Theory]
    [InlineData("interface")]   // another backend / pipe contract
    [InlineData("minApp")]      // needs a newer app
    [InlineData("digest")]      // description disagrees with GitHub's digest of the uploaded file
    [InlineData("size")]
    [InlineData("tag")]         // description of another version
    [InlineData("missing")]     // no component.json at all
    public async Task SkipsReleasesThatDoNotFitThisApp(string flaw)
    {
        var release = flaw switch
        {
            "digest" => Release("2.0.2-r1", assetSha: new string('c', 64)),
            "size" => Release("2.0.2-r1", assetSize: 999),
            "missing" => Release("2.0.2-r1", manifest: false),
            _ => Release("2.0.2-r1"),
        };
        var description = flaw switch
        {
            "interface" => Description("2.0.2-r1", contract: 2),
            "minApp" => Description("2.0.2-r1", minApp: "1.12.0"),
            "tag" => Description("2.0.3-r1"),
            _ => Description("2.0.2-r1"),
        };
        var client = Client(new JsonArray(release), new() { ["a2dpwb-v2.0.2-r1"] = description });
        var result = await client.CheckAsync("a2dpwb", 1, "2.0.1-r1", includePrerelease: false, TestContext.Current.CancellationToken);
        Assert.Equal(ComponentUpdateOutcome.UpToDate, result.Outcome);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task PrereleasesOnlyWhenAsked()
    {
        var client = Client(new JsonArray(Release("2.0.2-r1", prerelease: true)), new() { ["a2dpwb-v2.0.2-r1"] = Description("2.0.2-r1") });
        Assert.Equal(ComponentUpdateOutcome.UpToDate, (await client.CheckAsync("a2dpwb", 1, "2.0.1-r1", false, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ComponentUpdateOutcome.Available, (await client.CheckAsync("a2dpwb", 1, "2.0.1-r1", true, TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public async Task ReportsTheRateLimit()
    {
        var client = Client([], [], list: HttpStatusCode.TooManyRequests);
        Assert.Equal(ComponentUpdateOutcome.RateLimited, (await client.CheckAsync("a2dpwb", 1, null, false, TestContext.Current.CancellationToken)).Outcome);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
