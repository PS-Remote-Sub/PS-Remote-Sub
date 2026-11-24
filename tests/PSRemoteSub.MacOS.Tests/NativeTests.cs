namespace PSRemoteSub.MacOS.Tests;

public class NativeTests
{
    [MacFact]
    public void Version_IsOne() => Assert.Equal(1, Native.psrs_native_version());
}
