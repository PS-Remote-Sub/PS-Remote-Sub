using PSRemoteSub.Core.Library.Build;

namespace PSRemoteSub.Core.Tests.Library.Build;

public class ChunkPlanTests
{
    private static int _n;

    private static IEnumerable<Segment> Seg(string scene, int count, string part) =>
        Enumerable.Range(0, count).Select(_ => { _n++; return new Segment($"s{_n:D6}", $"line {_n}", scene, part, null); }).ToList();

    [Fact]
    public void Plan_splits_big_scenes_evenly_and_merges_small_ones_within_a_part()
    {
        var segs = Seg("A", 450, "Main story").Concat(Seg("B", 10, "Main story")).Concat(Seg("C", 30, "Main story"))
            .Concat(Seg("D", 5, "Open world")).ToList();
        var chunks = ChunkPlan.Plan(segs);
        Assert.Equal(new[] { 150, 150, 150, 40, 5 }, chunks.Select(c => c.Lines.Count));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, chunks.Select(c => c.Number));
        Assert.Equal("0001", chunks[0].Name);
    }

    [Fact]
    public void Plan_packs_whole_scenes_that_fit_into_one_chunk_by_default()
    {
        var segs = Seg("A", 120, "M").Concat(Seg("B", 70, "M")).Concat(Seg("C", 150, "M")).ToList();
        Assert.Equal(new[] { 190, 150 }, ChunkPlan.Plan(segs).Select(c => c.Lines.Count));
    }

    [Fact]
    public void Plan_keeps_a_normal_scene_whole_and_stops_merging_at_the_cap()
    {
        var segs = Seg("A", 39, "M").Concat(Seg("B", 39, "M")).Concat(Seg("C", 120, "M")).Concat(Seg("D", 39, "M")).ToList();
        var chunks = ChunkPlan.Plan(segs, max: 100);
        Assert.Equal(new[] { 78, 60, 60, 39 }, chunks.Select(c => c.Lines.Count));
    }
}
