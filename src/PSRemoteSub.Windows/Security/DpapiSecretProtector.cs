using System.Security.Cryptography;
using System.Text;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.Windows.Security;

/// <summary>
/// Protects secrets with DPAPI for the current Windows user: UTF-8 text in, base64 out, with a fixed entropy so
/// other programs running as the same user cannot decrypt the blob by accident. Thread-safe (stateless).
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PSRemoteSub.v1");

    public string Protect(string plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    /// <summary>Null on any failure: not base64, another user or machine, corrupt or empty.</summary>
    public string? Unprotect(string protectedText)
    {
        try
        {
            byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception)
        {
            return null; // The caller asks for the key again (AiKeyNeeded).
        }
    }
}
