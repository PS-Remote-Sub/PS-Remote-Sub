using PSRemoteSub.Core.Library.Build;

namespace PSRemoteSub.Core.Tests.Library.Build;

public class DraftCheckTests
{
    private static Chunk C() => new(1, [
        new Segment("s000001", "Hold ~INPUT_A~ now.", "m@1", "Main story (story000)", null),
        new Segment("s000002", "Ahck!", "m@1", "Main story (story000)", null),
        new Segment("s000003", "Let's go now.", "m@1", "Main story (story000)", null)]);

    [Fact]
    public void Check_accepts_bom_crlf_and_blank_lines()
    {
        var r = DraftCheck.Check(C(), "﻿s000001\tกด ~INPUT_A~ เลย\r\n\r\ns000002\tAhck!\r\ns000003\tไปกัน\r\n");
        Assert.True(r.Ok, string.Join("\n", r.Problems));
        Assert.Equal(new[] { "s000001", "s000002", "s000003" }, r.Rows.Select(x => x.Id));
        Assert.Equal("ไปกัน", r.Rows[2].Th);
    }

    [Fact]
    public void Check_reports_every_problem_by_id_or_line()
    {
        var r = DraftCheck.Check(C(), "```\ns000001\tกดเลย\ns000003\tไป\tกัน\ns000003\tไป\ns000009\tเกิน\n");
        Assert.False(r.Ok);
        Assert.Contains(r.Problems, p => p.Contains("line 1"));
        Assert.Contains(r.Problems, p => p.Contains("s000001") && p.Contains("~INPUT_A~"));
        Assert.Contains(r.Problems, p => p.Contains("s000002") && p.Contains("missing"));
        Assert.Contains(r.Problems, p => p.Contains("s000003") && p.Contains("TAB"));
        Assert.Contains(r.Problems, p => p.Contains("s000003") && p.Contains("duplicate"));
        Assert.Contains(r.Problems, p => p.Contains("s000009") && p.Contains("unknown"));
    }

    [Fact]
    public void Check_rejects_a_broken_character()
    {
        var r = DraftCheck.Check(C(), "s000001\tกด ~INPUT_A~\ns000002\tAhck�\ns000003\tไปกัน\n");
        Assert.Contains(r.Problems, p => p.Contains("s000002") && p.Contains("broken"));
    }

    private static Chunk Lang() => new(1, [
        new Segment("s000001", "In Spanish: The eight, my lucky number.", "m@1", "Main story (story000)", null),
        new Segment("s000002", "Unknown Language: Get out of here now.", "m@1", "Main story (story000)", null),
        new Segment("s000003", "Let's go now.", "m@1", "Main story (story000)", null)]);

    [Fact]
    public void Check_requires_the_language_prefix_where_the_english_has_one()
    {
        Assert.True(DraftCheck.Check(Lang(), "s000001\t(ภาษาสเปน) เลขแปด\ns000002\t(ภาษาอื่น) ออกไป\ns000003\tไปกัน\n").Ok);
        var r = DraftCheck.Check(Lang(), "s000001\tเลขแปด\ns000002\t(ภาษาสเปน) ออกไป\ns000003\t(ภาษาอื่น) ไปกัน\n");
        Assert.Contains(r.Problems, p => p.Contains("s000001") && p.Contains("(ภาษาสเปน)"));
        Assert.Contains(r.Problems, p => p.Contains("s000002") && p.Contains("(ภาษาอื่น)"));
        Assert.Contains(r.Problems, p => p.Contains("s000003") && p.Contains("prefix"));
    }

    [Fact]
    public void Check_flags_untranslated_empty_and_overlong_lines()
    {
        var r = DraftCheck.Check(C(), "s000001\tกด ~INPUT_A~\ns000002\t \ns000003\tLet's go now.\n");
        Assert.Contains(r.Problems, p => p.Contains("s000003") && p.Contains("Thai"));
        Assert.Contains(r.Problems, p => p.Contains("s000002") && p.Contains("empty"));
        // limit for "Let's go now." (13 chars) is Math.Max(4 * 13, 40) = 52
        r = DraftCheck.Check(C(), "s000001\tกด ~INPUT_A~\ns000002\tอ๊าก\ns000003\t" + new string('ก', 53) + "\n");
        Assert.Contains(r.Problems, p => p.Contains("s000003") && p.Contains("long"));
        r = DraftCheck.Check(C(), "s000001\tกด ~INPUT_A~\ns000002\tอ๊าก\ns000003\t" + new string('ก', 52) + "\n");
        Assert.True(r.Ok, string.Join("\n", r.Problems));
    }
}
