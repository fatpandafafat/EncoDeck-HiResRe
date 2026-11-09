// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EncoDeck.Core.Configuration;
using EncoDeck.Platform.Settings;
using EncoDeck.Platform.Updates;
using Microsoft.Extensions.Logging.Abstractions;

namespace EncoDeck.Tests.Platform;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.10.0", "0.9.0")]
    [InlineData("1.0.0", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0-beta.11")]
    [InlineData("1.0.0-beta.11", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha")]
    [InlineData("0.5.0", "0.5.0-ci.123")]
    [InlineData("0.5.1-beta.1", "0.5.0")]
    [InlineData("1.0.0-x.99999999999999999999", "1.0.0-x.9")]
    public void PrecedenceFollowsSemVer(string higher, string lower)
    {
        var a = SemanticVersion.Parse(higher);
        var b = SemanticVersion.Parse(lower);
        Assert.True(a > b);
        Assert.True(b < a);
        Assert.False(a.Equals(b));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3+abcdef", "1.2.3")]
    [InlineData("0.5.0-beta.1+8c1f2e3", "0.5.0-beta.1")]
    [InlineData("1.2", "1.2.0")]
    [InlineData(" V2 ", "2.0.0")]
    public void ParsesTagsAndStripsBuildMetadata(string text, string expected)
    {
        Assert.Equal(expected, SemanticVersion.Parse(text).ToString());
        Assert.Equal(SemanticVersion.Parse(expected), SemanticVersion.Parse(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2.3.4")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.x")]
    public void RejectsMalformedVersions(string text) => Assert.False(SemanticVersion.TryParse(text, out _));

    [Fact]
    public void BuildMetadataDoesNotAffectPrecedence() =>
        Assert.Equal(0, SemanticVersion.Parse("1.0.0+a").CompareTo(SemanticVersion.Parse("1.0.0+b")));
}

public sealed class GitHubUpdateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("encodeck-update-test").FullName;
    private static readonly Uri Api = new("https://api.github.test/repos/fatpandafafat/EncoDeck-HiResRe/releases");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static string Url(string tag, string name) => $"https://github.com/fatpandafafat/EncoDeck-HiResRe/releases/download/{tag}/{name}";

    private static object Asset(string tag, string name, long size, string? digest = null) =>
        new { name, size, browser_download_url = Url(tag, name), digest };

    private static object Release(string tag, bool draft = false, bool prerelease = false, params object[] assets) => new
    {
        tag_name = tag, name = $"EncoDeck {tag}", draft, prerelease, html_url = $"https://github.com/fatpandafafat/EncoDeck-HiResRe/releases/tag/{tag}",
        published_at = "2026-10-20T00:00:00Z", assets,
    };

    private static object Installer(string tag, long size = 1000, string? digest = null) => Asset(tag, $"EncoDeck-Setup-{tag.TrimStart('v')}-win-x64.exe", size, digest);

    private static Handler Json(params object[] releases) => new(_ => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(releases), Encoding.UTF8, "application/json"),
    });

    private GitHubUpdateClient Client(HttpMessageHandler handler, string current = "0.5.0") =>
        new(new HttpClient(handler), _root, SemanticVersion.Parse(current), Api);

    [Fact]
    public async Task NewestStableReleaseWithInstallerIsOffered_AndRequestCarriesUserAgent()
    {
        var handler = Json(
            Release("v0.6.0", false, false, Installer("v0.6.0")),
            Release("v0.7.0-beta.1", false, true, Installer("v0.7.0-beta.1")),
            Release("v0.8.0", true, false, Installer("v0.8.0")),
            Release("v0.5.1", false, false, Installer("v0.5.1")));
        using var client = Client(handler);
        var result = await client.CheckAsync(includePrerelease: false, Ct);
        Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
        Assert.Equal("0.6.0", result.Release!.Version.ToString());
        Assert.Equal("EncoDeck-Setup-0.6.0-win-x64.exe", result.Release.Installer.Name);
        Assert.Equal("https://github.com/fatpandafafat/EncoDeck-HiResRe/releases/tag/v0.6.0", result.Release.NotesUrl!.AbsoluteUri);
        Assert.Contains("EncoDeck/0.5.0", handler.Requests[0].Headers.UserAgent.ToString());

        var withPreview = await client.CheckAsync(includePrerelease: true, Ct);
        Assert.Equal("0.7.0-beta.1", withPreview.Release!.Version.ToString());
        Assert.True(withPreview.Release.IsPrerelease);
    }

    [Fact]
    public async Task SuffixedTagCountsAsPrereleaseEvenWithoutTheGitHubFlag()
    {
        using var client = Client(Json(Release("v0.6.0-rc.1", false, false, Installer("v0.6.0-rc.1"))));
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await client.CheckAsync(false, Ct)).Outcome);
        Assert.Equal(UpdateCheckOutcome.Available, (await client.CheckAsync(true, Ct)).Outcome);
    }

    [Fact]
    public async Task ReleaseWithoutMatchingInstallerIsSkipped()
    {
        using var client = Client(Json(
            Release("v0.7.0", false, false, Asset("v0.7.0", "EncoDeck-win-x64.zip", 10)),
            Release("v0.6.5", false, false, Asset("v0.6.5", "EncoDeck-Setup-0.6.4-win-x64.exe", 10)),
            Release("v0.6.0", false, false, Installer("v0.6.0"))));
        var result = await client.CheckAsync(false, Ct);
        Assert.Equal("0.6.0", result.Release!.Version.ToString());
    }

    [Fact]
    public async Task SameOrOlderVersionIsUpToDate_AndCiBuildIsOfferedTheRelease()
    {
        var releases = Json(Release("v0.5.0", false, false, Installer("v0.5.0")));
        using (var client = Client(releases))
            Assert.Equal(UpdateCheckOutcome.UpToDate, (await client.CheckAsync(false, Ct)).Outcome);
        using (var ci = Client(Json(Release("v0.5.0", false, false, Installer("v0.5.0"))), current: "0.5.0-ci.42"))
            Assert.Equal(UpdateCheckOutcome.Available, (await ci.CheckAsync(false, Ct)).Outcome);
        using (var none = Client(Json()))
            Assert.Equal(UpdateCheckOutcome.UpToDate, (await none.CheckAsync(false, Ct)).Outcome);
    }

    [Fact]
    public async Task ApiFailuresAreReportedQuietly()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
        limited.Headers.Add("X-RateLimit-Remaining", "0");
        using (var client = Client(new Handler(_ => limited)))
            Assert.Equal(UpdateCheckOutcome.RateLimited, (await client.CheckAsync(false, Ct)).Outcome);
        using (var client = Client(new Handler(_ => new(HttpStatusCode.TooManyRequests))))
            Assert.Equal(UpdateCheckOutcome.RateLimited, (await client.CheckAsync(false, Ct)).Outcome);
        using (var client = Client(new Handler(_ => new(HttpStatusCode.NotFound))))
            Assert.Equal(UpdateCheckOutcome.NotFound, (await client.CheckAsync(false, Ct)).Outcome);
        using (var client = Client(new Handler(_ => throw new HttpRequestException("No such host is known."))))
            Assert.Equal(UpdateCheckOutcome.Offline, (await client.CheckAsync(false, Ct)).Outcome);
        using (var client = Client(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>") })))
            Assert.Equal(UpdateCheckOutcome.Failed, (await client.CheckAsync(false, Ct)).Outcome);
    }

    [Theory]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789  EncoDeck-Setup-0.6.0-win-x64.exe\r\n")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789 *EncoDeck-Setup-0.6.0-win-x64.exe")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("SHA256 (EncoDeck-Setup-0.6.0-win-x64.exe) = abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("1111111111111111111111111111111111111111111111111111111111111111  EncoDeck-win-x64.zip\nabcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  installer/EncoDeck-Setup-0.6.0-win-x64.exe\n")]
    public void ChecksumFilesInCommonFormatsAreRead(string text) =>
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", GitHubUpdateClient.FindChecksum(text, "EncoDeck-Setup-0.6.0-win-x64.exe"));

    [Fact]
    public void ChecksumFileWithoutTheInstallerYieldsNothing() =>
        Assert.Null(GitHubUpdateClient.FindChecksum("1111111111111111111111111111111111111111111111111111111111111111  other.exe\n", "EncoDeck-Setup-0.6.0-win-x64.exe"));

    private (Handler Handler, UpdateRelease Release) Downloadable(byte[] installer, string? sumsText, string? digest = null, long? size = null)
    {
        const string tag = "v0.6.0";
        string name = "EncoDeck-Setup-0.6.0-win-x64.exe";
        var assets = new List<object> { Installer(tag, size ?? installer.Length, digest) };
        if (sumsText is not null) assets.Add(Asset(tag, name + ".sha256", sumsText.Length));
        var release = GitHubUpdateClient.SelectRelease(
            JsonSerializer.Deserialize(JsonSerializer.Serialize(new[] { Release(tag, false, false, [.. assets]) }), GitHubJsonContext.Default.ListGitHubRelease)!, false)!;
        var handler = new Handler(request => new(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath.EndsWith(".sha256") ? new StringContent(sumsText!) : new ByteArrayContent(installer),
        });
        return (handler, release);
    }

    [Fact]
    public async Task DownloadIsVerifiedAgainstPublishedChecksum_ThenReused()
    {
        byte[] installer = RandomNumberGenerator.GetBytes(300_000);
        string sums = $"{Convert.ToHexString(SHA256.HashData(installer))}  EncoDeck-Setup-0.6.0-win-x64.exe\r\n";
        var (handler, release) = Downloadable(installer, sums);
        Assert.NotNull(release.Checksum);
        using var client = Client(handler);
        var reports = new List<UpdateDownloadProgress>();
        string path = await client.DownloadAsync(release, new SyncProgress(reports.Add), Ct);
        Assert.Equal(Path.Combine(_root, "EncoDeck-Setup-0.6.0-win-x64.exe"), path);
        Assert.Equal(installer, await File.ReadAllBytesAsync(path, Ct));
        Assert.Equal(installer.Length, reports[^1].Received);
        Assert.All(reports, r => Assert.Equal(installer.Length, r.Total));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.partial"));

        int requests = handler.Requests.Count;
        Assert.Equal(path, await client.DownloadAsync(release, null, Ct));
        Assert.Equal(requests + 1, handler.Requests.Count); // Only the checksum file again.
    }

    [Fact]
    public async Task ChecksumMismatchLeavesNoInstaller()
    {
        byte[] installer = RandomNumberGenerator.GetBytes(10_000);
        string sums = $"{new string('0', 64)}  EncoDeck-Setup-0.6.0-win-x64.exe";
        var (handler, release) = Downloadable(installer, sums);
        using var client = Client(handler);
        await Assert.ThrowsAsync<UpdateVerificationException>(() => client.DownloadAsync(release, null, Ct));
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task GitHubAssetDigestIsUsedWhenNoChecksumFileIsPublished()
    {
        byte[] installer = RandomNumberGenerator.GetBytes(10_000);
        var (good, release) = Downloadable(installer, null, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(installer)));
        Assert.Null(release.Checksum);
        using (var client = Client(good))
            Assert.True(File.Exists(await client.DownloadAsync(release, null, Ct)));
        File.Delete(Path.Combine(_root, release.Installer.Name));
        var (_, wrong) = Downloadable(installer, null, "sha256:" + new string('a', 64));
        using (var client = Client(good))
            await Assert.ThrowsAsync<UpdateVerificationException>(() => client.DownloadAsync(wrong, null, Ct));
    }

    [Fact]
    public async Task SizeIsAlwaysVerified()
    {
        byte[] installer = RandomNumberGenerator.GetBytes(10_000);
        var (handler, release) = Downloadable(installer, null, size: installer.Length + 1);
        using var client = Client(handler);
        await Assert.ThrowsAsync<UpdateVerificationException>(() => client.DownloadAsync(release, null, Ct));
        Assert.Empty(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task ChecksumFileThatOmitsTheInstallerFailsVerification()
    {
        byte[] installer = RandomNumberGenerator.GetBytes(1000);
        var (handler, release) = Downloadable(installer, $"{new string('1', 64)}  something-else.exe\n{new string('2', 64)}  another.zip");
        using var client = Client(handler);
        await Assert.ThrowsAsync<UpdateVerificationException>(() => client.DownloadAsync(release, null, Ct));
    }

    private sealed class SyncProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Current { get; private set; } = new();
        public event EventHandler<AppSettings>? Changed;
        public void Update(Func<AppSettings, AppSettings> mutate) { Current = mutate(Current).Normalize(); Changed?.Invoke(this, Current); }
    }

    [Fact]
    public async Task AutomaticCheckDownloadsInBackground_AnnouncesOnce_AndRecordsTheCheckTime()
    {
        byte[] installer = RandomNumberGenerator.GetBytes(5000);
        var handler = new Handler(request => new(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.Host == Api.Host
                ? new StringContent(JsonSerializer.Serialize(new[] { Release("v0.6.0", false, false, Installer("v0.6.0", installer.Length, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(installer)))) }))
                : new ByteArrayContent(installer),
        });
        string state = Path.Combine(_root, "last-check");
        await using var updates = new UpdateManager(Client(handler), new MemorySettings(), state, NullLogger<UpdateManager>.Instance);
        Assert.True(updates.IsDue(DateTimeOffset.UtcNow));
        int announced = 0;
        updates.UpdateReady += (_, _) => announced++;
        await updates.RunAutomaticCheckAsync();
        Assert.Equal(UpdateStage.Ready, updates.Status.Stage);
        Assert.True(File.Exists(updates.Status.InstallerPath));
        Assert.Equal(1, announced);
        Assert.False(updates.IsDue(DateTimeOffset.UtcNow));
        Assert.True(updates.IsDue(DateTimeOffset.UtcNow.AddHours(25)));

        await updates.RunAutomaticCheckAsync();
        Assert.Equal(UpdateStage.Ready, updates.Status.Stage);
        Assert.Equal(1, announced);

        // The next start-up remembers the check time.
        await using var restarted = new UpdateManager(Client(handler), new MemorySettings(), state, NullLogger<UpdateManager>.Instance);
        Assert.False(restarted.IsDue(DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task ManualCheckNeverDownloads_AndOfflineDoesNotCountAsACheck()
    {
        var handler = Json(Release("v0.6.0", false, false, Installer("v0.6.0")));
        await using (var updates = new UpdateManager(Client(handler), new MemorySettings(), Path.Combine(_root, "a"), NullLogger<UpdateManager>.Instance))
        {
            await updates.CheckNowAsync();
            Assert.Equal(UpdateStage.Available, updates.Status.Stage);
            Assert.Single(handler.Requests);
        }
        await using (var offline = new UpdateManager(Client(new Handler(_ => throw new HttpRequestException("offline"))), new MemorySettings(), Path.Combine(_root, "b"), NullLogger<UpdateManager>.Instance))
        {
            await offline.CheckNowAsync();
            Assert.Equal(UpdateStage.Failed, offline.Status.Stage);
            Assert.Equal(UpdateFailure.Offline, offline.Status.Failure);
            Assert.True(offline.IsDue(DateTimeOffset.UtcNow));
        }
    }

    [Fact]
    public void OlderSettingsFilesGetAutomaticUpdateChecksOn()
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("{}")! with { AutoCheckUpdates = null };
        Assert.True(legacy.Normalize().AutoCheckUpdates);
        Assert.False(new AppSettings().IncludePrereleaseUpdates);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

public sealed class StoreReleaseNotesTests
{
    [Fact]
    public void ReadsTheSkuReleaseNotesAsBullets()
    {
        // The shape of the catalog JSON behind StoreProduct.ExtendedJsonData.
        const string json = """
            {"LocalizedProperties":[{"ProductTitle":"EncoDeck"}],
             "DisplaySkuAvailabilities":[{"Sku":{"LocalizedProperties":[{"MinimumNotes":"","ReleaseNotes":"- 新增水月雨支持\r\n- 修复电量显示\r\n"}]}}]}
            """;
        Assert.Equal("• 新增水月雨支持\n• 修复电量显示", StoreUpdateService.FindReleaseNotes(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"Skus":[{"LocalizedProperties":[{"ReleaseNotes":"  "}]}]}""")]
    public void MissingOrEmptyNotesAreNull(string? json) => Assert.Null(StoreUpdateService.FindReleaseNotes(json));
}

public sealed class ReleaseNotesTests
{
    [Fact]
    public void GitHubMarkdownBecomesPlainText_WithoutTheDownloadSection()
    {
        const string body = "这个版本新增 **水月雨** 支持。\r\n\r\n## 本次更新\r\n\r\n- **AKG**：支持电量。\r\n- 详见 [教程](https://example.test/a.md)。\r\n\r\n## 下载\r\n\r\n- EncoDeck-Setup.exe\r\n";
        Assert.Equal("这个版本新增 水月雨 支持。\n\n本次更新\n\n• AKG：支持电量。\n• 详见 教程。", ReleaseNotes.ToPlainText(body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" \r\n ")]
    [InlineData("## 下载\n- a.exe")]
    public void NothingLeftIsNull(string? body) => Assert.Null(ReleaseNotes.ToPlainText(body));
}
