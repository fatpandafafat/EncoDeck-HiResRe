// SPDX-License-Identifier: GPL-3.0-only
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using EncoDeck.Platform.Updates;

namespace EncoDeck.Platform.Components;

public enum ComponentUpdateOutcome
{
    UpToDate,
    Available,
    Offline,
    /// <summary>GitHub's anonymous API limit was reached.</summary>
    RateLimited,
    Failed,
}

public sealed record ComponentUpdateResult(ComponentUpdateOutcome Outcome, ComponentRelease? Release = null);

/// <summary>
/// Finds component releases newer than what this app pins or has installed, without an app update. A candidate
/// counts only when its <c>component.json</c> names the same component, tag and version, speaks this app's
/// interface, accepts this app version, and every package it lists is an asset of that release with the same
/// size and GitHub digest. The installer then holds the download to that size and digest, as for a pin.
/// </summary>
public sealed class ComponentUpdateClient(HttpClient http, SemanticVersion app, Uri? releasesApi = null)
{
    private const long MaxList = 4L * 1024 * 1024;
    private const long MaxManifest = 64 * 1024;
    private readonly Uri _api = releasesApi ?? GitHubUpdateClient.DefaultReleasesApi;

    public async Task<ComponentUpdateResult> CheckAsync(string id, int contract, string? newerThan, bool includePrerelease, CancellationToken ct = default)
    {
        JsonArray releases;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = Request(_api);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests
                || (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0"))
                return new(ComponentUpdateOutcome.RateLimited);
            if (!response.IsSuccessStatusCode) return new(ComponentUpdateOutcome.Failed);
            releases = JsonNode.Parse(await ReadLimitedAsync(response, MaxList, timeout.Token).ConfigureAwait(false)) as JsonArray ?? [];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(ComponentUpdateOutcome.Offline); }
        catch (HttpRequestException ex) when (ex.StatusCode is null) { return new(ComponentUpdateOutcome.Offline); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or IOException) { return new(ComponentUpdateOutcome.Failed); }

        string prefix = id + "-v";
        var candidates = releases.OfType<JsonObject>()
            .Select(r => (Release: r, Tag: Str(r["tag_name"])))
            .Where(x => x.Tag is not null && x.Tag.StartsWith(prefix, StringComparison.Ordinal) && !Bool(x.Release["draft"])
                && (includePrerelease || !Bool(x.Release["prerelease"])))
            .Select(x => (x.Release, x.Tag, Version: ComponentVersion.TryParse(x.Tag![prefix.Length..], out var v) ? v : null))
            .Where(x => x.Version is not null && ComponentVersion.IsNewer(x.Version.ToString(), newerThan))
            .OrderByDescending(x => x.Version)
            .Take(3);
        foreach (var (release, tag, _) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (await DescribeAsync(release, id, tag!, contract, ct).ConfigureAwait(false) is { } offer) return new(ComponentUpdateOutcome.Available, offer);
        }
        return new(ComponentUpdateOutcome.UpToDate);
    }

    /// <summary>The release's component.json, when it is consistent with the release and this app; null otherwise.</summary>
    private async Task<ComponentRelease?> DescribeAsync(JsonObject release, string id, string tag, int contract, CancellationToken ct)
    {
        var assets = (release["assets"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var manifest = assets.FirstOrDefault(a => Str(a["name"]) == ComponentCatalog.ManifestAsset);
        if (manifest is null || !Uri.TryCreate(Str(manifest["browser_download_url"]), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) return null;
        ComponentRelease offer;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = Request(url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            offer = ComponentCatalog.ParseRelease(System.Text.Encoding.UTF8.GetString(await ReadLimitedAsync(response, MaxManifest, timeout.Token).ConfigureAwait(false)).TrimStart((char)0xFEFF));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or IOException or InvalidOperationException or FormatException) { return null; }
        return Accepts(offer, id, tag, contract, assets) ? offer : null;
    }

    /// <summary>The checks a found release must pass.</summary>
    internal bool Accepts(ComponentRelease offer, string id, string tag, int contract, IReadOnlyList<JsonObject> assets)
    {
        if (offer.Id != id || offer.Tag != tag || offer.Interface != contract || offer.Packages.Count == 0) return false;
        if (offer.MinApp is { } min && SemanticVersion.TryParse(min, out var minimum) && app.CompareTo(minimum) < 0) return false;
        foreach (var package in offer.Packages)
        {
            if (!package.IsPublished) return false;
            var asset = assets.FirstOrDefault(a => Str(a["name"]) == package.File);
            if (asset is null || asset["size"]?.GetValue<long>() != package.Size) return false;
            // GitHub's own digest of the uploaded file must agree with the description, where GitHub reports one.
            if (Str(asset["digest"]) is { } digest && !string.Equals(digest, "sha256:" + package.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private HttpRequestMessage Request(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("EncoDeck", app.ToString()));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/TIANLI0/EncoDeck)"));
        return request;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Response exceeds size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > limit) throw new InvalidDataException("Response exceeds size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static bool Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) && b;
}
