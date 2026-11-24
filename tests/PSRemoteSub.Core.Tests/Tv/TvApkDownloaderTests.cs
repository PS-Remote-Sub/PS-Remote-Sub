using System.Net;
using System.Security.Cryptography;
using System.Text;
using PSRemoteSub.Core.Tests.Translation;
using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

public sealed class TvApkDownloaderTests : IDisposable
{
    private const string Repo = "https://github.com/o/psrs-tv-overlay";
    private static readonly byte[] Apk5 = Encoding.ASCII.GetBytes("apk version five");

    private readonly string _dir = Directory.CreateTempSubdirectory("tvapk").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Latest(string name, byte[] bytes, string? digest = "auto", long? size = null, string url = "https://dl.test/a.apk")
    {
        var d = digest == "auto" ? Digest(bytes) : digest;
        var digestJson = d is null ? "" : $",\"digest\":\"{d}\"";
        return $"{{\"tag_name\":\"v1.0\",\"assets\":[{{\"name\":\"{name}\",\"size\":{size ?? bytes.Length},\"browser_download_url\":\"{url}\"{digestJson}}}]}}";
    }

    private TvApkDownloader Downloader(string latestJson, byte[] apk, out StubHttpHandler handler, HttpStatusCode apiStatus = HttpStatusCode.OK)
    {
        handler = new StubHttpHandler(req => req.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(apiStatus) { Content = new StringContent(latestJson) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(apk) });
        return new TvApkDownloader(new HttpClient(handler), Repo, _dir);
    }

    private string Cached(int version) => Path.Combine(_dir, $"psrs-tv-overlay-{version}.apk");

    [Theory]
    [InlineData("https://github.com/PS-Remote-Sub/PS-Remote-Sub", "https://github.com/PS-Remote-Sub/psrs-tv-overlay")]
    [InlineData("https://github.com/owner/app.git", "https://github.com/owner/psrs-tv-overlay")]
    [InlineData("https://github.com/owner/app/", "https://github.com/owner/psrs-tv-overlay")]
    public void RepositoryFor_SameOwner(string app, string expected) => Assert.Equal(expected, TvApkDownloader.RepositoryFor(app));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://gitlab.com/o/r")]
    [InlineData("http://github.com/o/r")]
    public void RepositoryFor_NotGitHub_Null(string app) => Assert.Null(TvApkDownloader.RepositoryFor(app));

    [Fact]
    public async Task NewerRelease_DownloadsAndVerifies()
    {
        var d = Downloader(Latest("psrs-tv-overlay-5.apk", Apk5), Apk5, out var h);

        var apk = await d.GetNewerAsync(2, CancellationToken.None);

        Assert.NotNull(apk);
        Assert.Equal(5, apk.Version);
        Assert.Equal(Cached(5), apk.Path);
        Assert.Equal(Apk5, File.ReadAllBytes(apk.Path));
        Assert.Equal("https://api.github.com/repos/o/psrs-tv-overlay/releases/latest", h.Requests[0].ToString());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task ReleaseNotNewer_NoDownload()
    {
        var d = Downloader(Latest("psrs-tv-overlay-2.apk", Apk5), Apk5, out var h);

        Assert.Null(await d.GetNewerAsync(2, CancellationToken.None));
        Assert.Single(h.Requests); // only the release lookup
    }

    [Fact]
    public async Task AlreadyCached_NoSecondDownload()
    {
        File.WriteAllBytes(Cached(5), Apk5);
        var d = Downloader(Latest("psrs-tv-overlay-5.apk", Apk5), Apk5, out var h);

        var apk = await d.GetNewerAsync(2, CancellationToken.None);

        Assert.Equal(Cached(5), apk?.Path);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task CorruptCache_Redownloads()
    {
        File.WriteAllBytes(Cached(5), Encoding.ASCII.GetBytes("broken"));
        var d = Downloader(Latest("psrs-tv-overlay-5.apk", Apk5), Apk5, out var h);

        var apk = await d.GetNewerAsync(2, CancellationToken.None);

        Assert.Equal(Apk5, File.ReadAllBytes(apk!.Path));
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task DigestMismatch_Rejected()
    {
        var d = Downloader(Latest("psrs-tv-overlay-5.apk", Apk5, digest: "sha256:" + new string('0', 64)), Apk5, out _);

        Assert.Null(await d.GetNewerAsync(2, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task SizeMismatch_Rejected()
    {
        var d = Downloader(Latest("psrs-tv-overlay-5.apk", Apk5, digest: null, size: 999), Apk5, out _);

        Assert.Null(await d.GetNewerAsync(2, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task NoDigest_SizeMatches_Accepted()
    {
        var d = Downloader(Latest("psrs-tv-overlay-5.apk", Apk5, digest: null), Apk5, out _);

        Assert.Equal(5, (await d.GetNewerAsync(2, CancellationToken.None))?.Version);
    }

    [Fact]
    public async Task HttpDownloadUrl_Rejected()
    {
        var d = Downloader(Latest("psrs-tv-overlay-5.apk", Apk5, url: "http://dl.test/a.apk"), Apk5, out var h);

        Assert.Null(await d.GetNewerAsync(2, CancellationToken.None));
        Assert.Single(h.Requests);
    }

    [Theory]
    [InlineData("psrs-tv-overlay.apk")]
    [InlineData("psrs-tv-overlay-5.zip")]
    [InlineData("other-5.apk")]
    [InlineData("psrs-tv-overlay-x.apk")]
    public async Task UnknownAssetName_Ignored(string name)
    {
        var d = Downloader(Latest(name, Apk5), Apk5, out _);

        Assert.Null(await d.GetNewerAsync(2, CancellationToken.None));
    }

    [Fact]
    public async Task Offline_FallsBackToNewestCachedNewerThanBundle()
    {
        File.WriteAllBytes(Cached(4), Apk5);
        File.WriteAllBytes(Cached(3), Apk5);
        var d = new TvApkDownloader(new HttpClient(new StubHttpHandler(_ => throw new HttpRequestException("offline"))), Repo, _dir);

        var apk = await d.GetNewerAsync(2, CancellationToken.None);

        Assert.Equal(Cached(4), apk?.Path);
        Assert.Equal(4, apk?.Version);
    }

    [Fact]
    public async Task ApiError_CachedNotNewer_Null()
    {
        File.WriteAllBytes(Cached(2), Apk5);
        var d = Downloader("{}", Apk5, out _, HttpStatusCode.NotFound);

        Assert.Null(await d.GetNewerAsync(2, CancellationToken.None));
    }

    [Fact]
    public async Task Download_RemovesOlderCachedApks()
    {
        File.WriteAllBytes(Cached(3), Apk5);
        var d = Downloader(Latest("psrs-tv-overlay-5.apk", Apk5), Apk5, out _);

        await d.GetNewerAsync(2, CancellationToken.None);

        Assert.Equal([Cached(5)], Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var d = Downloader(Latest("psrs-tv-overlay-5.apk", Apk5), Apk5, out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d.GetNewerAsync(2, cts.Token));
    }
}
