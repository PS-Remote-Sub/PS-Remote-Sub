using PSRemoteSub.Windows.Security;

namespace PSRemoteSub.Windows.Tests;

public class DpapiSecretProtectorTests
{
    [Fact]
    public void Dpapi_RoundTrip()
    {
        var protector = new DpapiSecretProtector();
        const string plain = "sk-ทดสอบ-123";

        string protectedText = protector.Protect(plain);

        Assert.NotEqual(plain, protectedText);
        Assert.DoesNotContain("sk-", protectedText, StringComparison.Ordinal);
        _ = Convert.FromBase64String(protectedText); // base64 output
        Assert.Equal(plain, protector.Unprotect(protectedText));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")]
    public void Dpapi_GarbageReturnsNull(string garbage)
    {
        Assert.Null(new DpapiSecretProtector().Unprotect(garbage));
    }
}
