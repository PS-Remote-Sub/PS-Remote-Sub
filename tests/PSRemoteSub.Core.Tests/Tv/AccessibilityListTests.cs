using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

public sealed class AccessibilityListTests
{
    private const string TalkBack = "com.google.android.marvin.talkback/.TalkBackService";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("null\r\n")]
    public void Add_ToNull(string? current) => Assert.Equal(AccessibilityList.Ours, AccessibilityList.Add(current));

    [Fact]
    public void Add_KeepsTalkBack() =>
        Assert.Equal($"{TalkBack}:{AccessibilityList.Ours}", AccessibilityList.Add(TalkBack));

    [Fact]
    public void Add_NoDuplicate()
    {
        var current = $"{TalkBack}:{AccessibilityList.Ours}";
        Assert.Equal(current, AccessibilityList.Add(current));
        Assert.Equal(AccessibilityList.Ours, AccessibilityList.Add(AccessibilityList.Ours.ToUpperInvariant()), ignoreCase: true);
    }

    [Fact]
    public void Contains_CaseInsensitive_AndExactEntry()
    {
        Assert.True(AccessibilityList.Contains($"{TalkBack}:{AccessibilityList.Ours.ToUpperInvariant()}"));
        Assert.False(AccessibilityList.Contains(TalkBack));
        Assert.False(AccessibilityList.Contains(null));
        Assert.False(AccessibilityList.Contains("null"));
        Assert.False(AccessibilityList.Contains(AccessibilityList.Ours + "X"));
    }
}
