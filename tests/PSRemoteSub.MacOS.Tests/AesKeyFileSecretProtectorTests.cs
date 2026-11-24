using System.Runtime.Versioning;
using PSRemoteSub.MacOS.Security;

namespace PSRemoteSub.MacOS.Tests;

public sealed class AesKeyFileSecretProtectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "psrs-aes-" + Guid.NewGuid().ToString("N"));
    private string KeyPath => Path.Combine(_dir, "secret.key");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public void RoundTrip_ReturnsPlain()
    {
        var p = new AesKeyFileSecretProtector(KeyPath);
        var text = p.Protect("sk-abc");
        Assert.StartsWith("aesgcm1:", text);
        Assert.Equal("sk-abc", p.Unprotect(text));
    }

    [MacFact]
    [UnsupportedOSPlatform("windows")]
    public void Protect_CreatesKeyFile_Mode0600()
    {
        new AesKeyFileSecretProtector(KeyPath).Protect("x");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyPath));
    }

    [Fact]
    public void SecondInstance_SameKeyFile_Decrypts()
    {
        var text = new AesKeyFileSecretProtector(KeyPath).Protect("sk-abc");
        Assert.Equal("sk-abc", new AesKeyFileSecretProtector(KeyPath).Unprotect(text));
    }

    [Fact]
    public void ForeignText_ReturnsNull()
    {
        var p = new AesKeyFileSecretProtector(KeyPath);
        Assert.Null(p.Unprotect(Convert.ToBase64String(new byte[80])));
        Assert.Null(p.Unprotect("aesgcm1:!!!notbase64"));

        var good = p.Protect("sk-abc");
        var raw = Convert.FromBase64String(good["aesgcm1:".Length..]);
        raw[^1] ^= 0xFF;
        Assert.Null(p.Unprotect("aesgcm1:" + Convert.ToBase64String(raw)));
    }

    [Fact]
    public void MissingKeyFile_ProtectCreatesIt_UnprotectOfOldTextReturnsNull()
    {
        var old = new AesKeyFileSecretProtector(KeyPath).Protect("sk-abc");
        File.Delete(KeyPath);

        var p = new AesKeyFileSecretProtector(KeyPath);
        Assert.Null(p.Unprotect(old));
        Assert.False(File.Exists(KeyPath));
        Assert.Equal("n", p.Unprotect(p.Protect("n")));
        Assert.True(File.Exists(KeyPath));
    }

    [Fact]
    public void WrongSizedKeyFile_UnprotectNull_ProtectReplacesKey()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(KeyPath, new byte[5]);
        var p = new AesKeyFileSecretProtector(KeyPath);
        Assert.Null(p.Unprotect("aesgcm1:" + Convert.ToBase64String(new byte[40])));
        Assert.Equal("v", p.Unprotect(p.Protect("v")));
        Assert.Equal(32, new FileInfo(KeyPath).Length);
    }
}
