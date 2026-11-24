namespace PSRemoteSub.Core.Platform;

/// <summary>Protects secrets (API keys) at rest, bound to the current OS user.</summary>
public interface ISecretProtector
{
    string Protect(string plain);

    /// <summary>Returns null when the text cannot be decrypted (other user or machine, corrupt).</summary>
    string? Unprotect(string protectedText);
}
