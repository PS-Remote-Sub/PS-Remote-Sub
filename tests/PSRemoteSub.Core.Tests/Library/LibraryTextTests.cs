using PSRemoteSub.Core.Library;

namespace PSRemoteSub.Core.Tests.Library;

public class LibraryTextTests
{
    [Fact]
    public void Normalize_MatchesLeonida()
    {
        Assert.Equal("hey it's me lucia", LibraryText.Normalize("Hey—it’s ME, Lucia!!"));
        Assert.Equal("yo", LibraryText.Normalize("'Yo'"));
        Assert.Equal("", LibraryText.Normalize("ลูเซีย"));
    }

    [Fact]
    public void SplitSentences_SplitsOnTerminators()
    {
        Assert.Equal(new[] { "Wait.", "Go now!", "Why?" }, LibraryText.SplitSentences("- Wait. Go now! Why?"));
        Assert.Equal(new[] { "Mr.Smith" }, LibraryText.SplitSentences("Mr.Smith"));
    }

    [Fact]
    public void IsNegated_Cases()
    {
        Assert.True(LibraryText.IsNegated("i can't go"));
        Assert.True(LibraryText.IsNegated("i cant go"));
        Assert.True(LibraryText.IsNegated("never again"));
        Assert.False(LibraryText.IsNegated("i can go"));
        Assert.False(LibraryText.IsNegated("no way"));
    }

    [Fact]
    public void SameDigitTokens_Cases()
    {
        Assert.True(LibraryText.SameDigitTokens("i need 50 grand", "i need 50 grond"));
        Assert.True(LibraryText.SameDigitTokens("no digits here", "none here either"));
        Assert.True(LibraryText.SameDigitTokens("2 cars and 10 bikes", "10 cars and 2 bikes")); // a multiset
        Assert.False(LibraryText.SameDigitTokens("i need 50 grand", "i need 60 grand"));
        Assert.False(LibraryText.SameDigitTokens("room 4b", "room 4"));
        Assert.False(LibraryText.SameDigitTokens("5 and 5", "5 and five"));
    }

    [Fact]
    public void IndelRatio_Known()
    {
        Assert.Equal(62, LibraryText.IndelRatio("kitten", "sitting"));
        Assert.Equal(100, LibraryText.IndelRatio("abc", "abc"));
        Assert.Equal(100, LibraryText.IndelRatio("", ""));
    }

    private static int LcsByTable(string a, string b)
    {
        var t = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                t[i, j] = a[i - 1] == b[j - 1] ? t[i - 1, j - 1] + 1 : Math.Max(t[i - 1, j], t[i, j - 1]);
        return t[a.Length, b.Length];
    }

    [Fact]
    public void Lcs_MatchesTheTable_AnyLengthAndAlphabet()
    {
        var rnd = new Random(3);
        const string small = "ab ";
        const string wide = "abcdefghijklmnopqrstuvwxyz0123456789' ";
        for (int round = 0; round < 3000; round++)
        {
            var alphabet = round % 3 == 0 ? small : wide;
            string Make(int len) => new(Enumerable.Range(0, len).Select(_ => round % 50 == 0 ? (char)rnd.Next(1, 0x3000) : alphabet[rnd.Next(alphabet.Length)]).ToArray());
            var a = Make(rnd.Next(0, round % 10 == 0 ? 300 : 90));
            var b = Make(rnd.Next(0, round % 10 == 0 ? 300 : 90));
            Assert.Equal(LcsByTable(a, b), LibraryText.Lcs(a, b));
        }
        Assert.Equal(0, LibraryText.Lcs("", "abc"));
        Assert.Equal(64, LibraryText.Lcs(new string('a', 64), new string('a', 64)));
        Assert.Equal(65, LibraryText.Lcs(new string('a', 65), new string('a', 130)));
    }
}
