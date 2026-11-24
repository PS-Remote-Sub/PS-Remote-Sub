using System.Security.Cryptography;
using System.Text;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.MacOS.Security;

/// <summary>
/// Protects secrets with AES-GCM under a 256-bit key kept in a user-only (0600) file. Output is
/// <c>"aesgcm1:" + base64(nonce[12] + tag[16] + ciphertext)</c>.
/// </summary>
public sealed class AesKeyFileSecretProtector(string keyPath) : ISecretProtector
{
    private const string Prefix = "aesgcm1:";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public string Protect(string plain)
    {
        var key = LoadOrCreateKey();
        var data = Encoding.UTF8.GetBytes(plain);
        var buf = new byte[NonceSize + TagSize + data.Length];
        var nonce = buf.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, data, buf.AsSpan(NonceSize + TagSize), buf.AsSpan(NonceSize, TagSize));
        return Prefix + Convert.ToBase64String(buf);
    }

    public string? Unprotect(string protectedText)
    {
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        try
        {
            var key = ReadKey();
            if (key is null)
                return null;
            var buf = Convert.FromBase64String(protectedText[Prefix.Length..]);
            if (buf.Length < NonceSize + TagSize)
                return null;
            var plain = new byte[buf.Length - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(buf.AsSpan(0, NonceSize), buf.AsSpan(NonceSize + TagSize), buf.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private byte[]? ReadKey()
    {
        if (!File.Exists(keyPath))
            return null;
        var key = File.ReadAllBytes(keyPath);
        return key.Length == KeySize ? key : null;
    }

    private byte[] LoadOrCreateKey()
    {
        var existing = ReadKey();
        if (existing is not null)
            return existing;

        var dir = Path.GetDirectoryName(keyPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // A wrong-sized key file is unusable: replace it.
        if (File.Exists(keyPath))
            File.Delete(keyPath);

        var key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            // CreateNew so two quick starts cannot each write their own key; the loser reads the winner's.
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) // created 0600 from the start, never briefly readable by others
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var fs = new FileStream(keyPath, options);
            fs.Write(key);
            return key;
        }
        catch (IOException) when (File.Exists(keyPath))
        {
            for (var i = 0; i < 20; i++)
            {
                var other = ReadKey();
                if (other is not null)
                    return other;
                Thread.Sleep(10); // the winner may still be writing
            }
            throw;
        }
    }
}
