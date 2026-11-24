using PSRemoteSub.Core.Text;

namespace PSRemoteSub.Core.Tests.Text;

public class SimilarityTests
{
    [Fact] public void Ratio_KittenSitting() => Assert.Equal(0.571, Similarity.Ratio("kitten", "sitting"), 0.001);
    [Fact] public void Ratio_CaseInsensitive() => Assert.Equal(1.0, Similarity.Ratio("ABC", "abc"));
    [Fact] public void Ratio_BothEmpty() => Assert.Equal(1.0, Similarity.Ratio("", ""));
    [Fact] public void Ratio_OneEmpty() => Assert.Equal(0.0, Similarity.Ratio("a", ""));
}
