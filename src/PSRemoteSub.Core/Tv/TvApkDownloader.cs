using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PSRemoteSub.Core.Diagnostics;

namespace PSRemoteSub.Core.Tv;

/// <summary>A TV app APK on disk and its versionCode.</summary>
public sealed record TvApk(string Path, int Version);

/// <summary>
/// Gets the TV app from the latest GitHub release of the <c>psrs-tv-overlay</c> repository: the release asset
/// <c>psrs-tv-overlay-&lt;versionCode&gt;.apk</c>, downloaded once into <paramref name="cacheDir"/> and checked against
/// the asset's size and SHA-256 digest. Signature trust stays with Android: <c>adb install -r</c> refuses an update
/// signed with another key.
/// </summary>
public sealed partial class TvApkDownloader(HttpClient http, string repositoryUrl, string cacheDir)
{
    /// <summary>Name of the TV app repository; it lives under the same owner as the PC app's repository.</summary>
    public const string RepositoryName = "psrs-tv-overlay";

    private const string FilePrefix = "psrs-tv-overlay-";
    private const long MaxApkBytes = 200L * 1024 * 1024;

    [GeneratedRegex(@"^https://github\.com/([A-Za-z0-9_.\-]+)/([A-Za-z0-9_.\-]+?)(?:\.git)?/?$")]
    private static partial Regex RepoRx();

    [GeneratedRegex(@"^psrs-tv-overlay-(\d{1,9})\.apk$")]
    private static partial Regex AssetRx();

    /// <summary>
    /// The TV app repository next to <paramref name="appRepositoryUrl"/> (same owner), or null when that is empty or
    /// not a GitHub https URL: local builds have no repository URL and never contact GitHub.
    /// </summary>
    public static string? RepositoryFor(string? appRepositoryUrl)
    {
        var m = RepoRx().Match((appRepositoryUrl ?? "").Trim());
        return m.Success ? $"https://github.com/{m.Groups[1].Value}/{RepositoryName}" : null;
    }

    /// <summary>
    /// An APK newer than <paramref name="newerThan"/> (the bundled versionCode, or 0 without one): the latest
    /// release's, downloaded when not already cached, else (offline, GitHub down, a bad download) the newest cached
    /// one. Null when nothing newer is available. Never throws except on cancellation.
    /// </summary>
    public async Task<TvApk?> GetNewerAsync(int newerThan, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (await FindLatestAsync(ct).ConfigureAwait(false) is { } asset && asset.Version > newerThan
                && await EnsureCachedAsync(asset, ct).ConfigureAwait(false) is { } fresh)
                return fresh;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("TV app download failed", ex);
        }
        return NewestCached(newerThan);
    }

    private sealed record Asset(int Version, string Url, long Size, string? Sha256);

    private async Task<Asset?> FindLatestAsync(CancellationToken ct)
    {
        var m = RepoRx().Match((repositoryUrl ?? "").Trim());
        if (!m.Success)
            return null;

        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{m.Groups[1].Value}/{m.Groups[2].Value}/releases/latest");
        req.Headers.UserAgent.ParseAdd("PSRemoteSub");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warn($"TV app download: latest release returned HTTP {(int)resp.StatusCode}");
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        Asset? best = null;
        foreach (var a in assets.EnumerateArray())
        {
            if (Str(a, "name") is not { } name || Str(a, "browser_download_url") is not { } url)
                continue;
            var am = AssetRx().Match(name);
            if (!am.Success || !int.TryParse(am.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                Log.Warn($"TV app download: {name} has a non-https download URL");
                continue;
            }
            long size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : -1;
            string? sha = Str(a, "digest") is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? d["sha256:".Length..].Trim().ToLowerInvariant()
                : null;
            if (best is null || v > best.Version)
                best = new Asset(v, url, size, sha);
        }
        return best;
    }

    /// <summary>The cached file for <paramref name="asset"/>, downloading it when missing or not matching; null when the download is bad.</summary>
    private async Task<TvApk?> EnsureCachedAsync(Asset asset, CancellationToken ct)
    {
        var path = CachePath(asset.Version);
        if (File.Exists(path) && Matches(path, asset))
            return new TvApk(path, asset.Version);

        Directory.CreateDirectory(cacheDir);
        var tmp = path + ".tmp";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, asset.Url);
            req.Headers.UserAgent.ParseAdd("PSRemoteSub");
            req.Headers.Accept.ParseAdd("application/octet-stream");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"TV app download: HTTP {(int)resp.StatusCode}");
                return null;
            }
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxApkBytes)
                    {
                        Log.Warn("TV app download: file is too large");
                        return null;
                    }
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
            if (!Matches(tmp, asset))
            {
                Log.Warn($"TV app download: psrs-tv-overlay-{asset.Version}.apk does not match the release's size or digest");
                return null;
            }
            File.Move(tmp, path, true);
            Log.Info($"TV app download: got versionCode {asset.Version}");
            RemoveOlderThan(asset.Version);
            return new TvApk(path, asset.Version);
        }
        finally
        {
            try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Size (when known) and SHA-256 digest (when GitHub gives one) both match.</summary>
    private static bool Matches(string path, Asset asset)
    {
        var info = new FileInfo(path);
        if (asset.Size >= 0 && info.Length != asset.Size)
            return false;
        if (asset.Sha256 is null)
            return asset.Size >= 0; // nothing to check against: never trust an unknown file
        using var fs = info.OpenRead();
        return Convert.ToHexString(SHA256.HashData(fs)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private string CachePath(int version) => Path.Combine(cacheDir, $"{FilePrefix}{version.ToString(CultureInfo.InvariantCulture)}.apk");

    private IEnumerable<TvApk> Cached()
    {
        if (!Directory.Exists(cacheDir))
            yield break;
        foreach (var f in Directory.EnumerateFiles(cacheDir, FilePrefix + "*.apk"))
        {
            var m = AssetRx().Match(Path.GetFileName(f));
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                yield return new TvApk(f, v);
        }
    }

    /// <summary>Files are only ever moved in after a verified download, so a cached one is trusted.</summary>
    private TvApk? NewestCached(int newerThan)
    {
        try
        {
            return Cached().Where(a => a.Version > newerThan).MaxBy(a => a.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RemoveOlderThan(int version)
    {
        foreach (var old in Cached().Where(a => a.Version < version).ToList())
            try { File.Delete(old.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;
}
