using PSRemoteSub.Core.Translation;

namespace PSRemoteSub.Core.Tests.Translation;

public class LruCacheTests
{
    [Fact]
    public void Lru_EvictsLeastRecentlyUsed()
    {
        var c = new LruCache<string, int>(2);
        c.Set("a", 1);
        c.Set("b", 2);
        Assert.True(c.TryGet("a", out _));
        c.Set("c", 3);
        Assert.False(c.TryGet("b", out _));
        Assert.True(c.TryGet("a", out var a));
        Assert.Equal(1, a);
        Assert.True(c.TryGet("c", out _));
        Assert.Equal(2, c.Count);
    }

    [Fact]
    public void Lru_SetExistingKey_UpdatesAndRefreshes()
    {
        var c = new LruCache<string, int>(2);
        c.Set("a", 1);
        c.Set("b", 2);
        c.Set("a", 10);
        c.Set("c", 3);
        Assert.False(c.TryGet("b", out _));
        Assert.True(c.TryGet("a", out var a));
        Assert.Equal(10, a);
    }
}
