using PSRemoteSub.Core.Capture;

namespace PSRemoteSub.Core.Tests.Capture;

public class CaptureColorTests
{
    [Theory]
    [InlineData(1u, 2u, "matrix BT.709, range limited 16-235")]
    [InlineData(2u, 1u, "matrix BT.601, range full 0-255")]
    [InlineData(4u, 3u, "matrix BT.2020, range 48-208")]
    [InlineData(0u, 0u, "matrix unknown, range unknown")]
    [InlineData(99u, 99u, "matrix 99, range 99")]
    public void Describe_NamesTheValues(uint matrix, uint range, string expected) =>
        Assert.Equal(expected, CaptureColor.Describe(matrix, range));

    [Fact]
    public void Describe_Unset_SaysSo() =>
        // Unset means the reader's video processor picks its default (BT.601 or BT.709 by size, limited range).
        Assert.Equal("matrix unset, range unset", CaptureColor.Describe(null, null));
}
