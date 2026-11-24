using PSRemoteSub.Core.Library;

namespace PSRemoteSub.Core.Tests.Library;

public sealed class LibraryMatcherTests
{
    private const string Long = "and try to get a few grand stacked away.";
    private const string LongTh = "แล้วจะลองเก็บเงินสักหลายพันไว้";

    private static LibraryLine L(string en, string th, string? speaker = null, int order = 0) =>
        new(en, th, "claude", null, null, speaker, order);

    private static LibraryMatcher M(IReadOnlyList<LibraryLine> released, IReadOnlyList<LibraryLine>? learned = null) =>
        new(released, learned ?? [], CharacterIndexTests.Sample());

    private static LibraryMatcher Basic() => M([L(Long, LongTh)]);

    [Fact]
    public void Lookup_Exact()
    {
        var hit = Basic().Lookup(Long, null);
        Assert.NotNull(hit);
        Assert.Equal(LongTh, hit!.Thai);
        Assert.Equal(100, hit.Score);
    }

    [Fact]
    public void Lookup_IgnoresCaseAndPunctuationAndCurlyApostrophe()
    {
        Assert.NotNull(Basic().Lookup("AND TRY to get a few grand, stacked away", null));
        var m = M([L("I can\u2019t go", "ฉันไปไม่ได้")]);
        Assert.NotNull(m.Lookup("I can't go", null));
    }

    [Fact]
    public void Lookup_FuzzyOcrSlip()
    {
        var hit = Basic().Lookup("and try to get a few grond stacked away", null);
        Assert.NotNull(hit);
        Assert.Equal(LongTh, hit!.Thai);
        Assert.True(hit.Score >= 85 && hit.Score < 100);
    }

    [Fact]
    public void Lookup_RejectsBelowFloor() =>
        Assert.Null(Basic().Lookup("and try to buy a new car today", null));

    [Fact]
    public void Lookup_ShortNeedsExact()
    {
        var m = M([L("Go.", "ไป")]);
        Assert.Null(m.Lookup("Ga.", null));
        Assert.NotNull(m.Lookup("go", null));
    }

    [Fact]
    public void Lookup_RejectsLengthRatio() =>
        Assert.Null(Basic().Lookup("a few grand stacked away", null));

    [Fact]
    public void Lookup_RejectsNegationFlip() =>
        Assert.Null(M([L("I can do it tonight", "ฉันทำได้คืนนี้")]).Lookup("I can't do it tonight", null));

    [Fact]
    public void Lookup_RejectsDigitMismatch() =>
        Assert.Null(M([L("I need 50 grand by tonight", "ฉันต้องการห้าหมื่นภายในคืนนี้")]).Lookup("I need 60 grand by tonight", null));

    [Fact]
    public void Lookup_SameDigits_FuzzyStillHits() =>
        Assert.Equal("ฉันต้องการห้าหมื่นภายในคืนนี้",
            M([L("I need 50 grand by tonight", "ฉันต้องการห้าหมื่นภายในคืนนี้")]).Lookup("I need 50 grand by tonihgt", null)?.Thai);

    [Fact]
    public void Lookup_EmptyMisses() => Assert.Null(Basic().Lookup("  ...  ", null));

    [Fact]
    public void Lookup_PrefersSpeaker()
    {
        var m = M([L("Come on", "ค่ะ", "Lucia", 0), L("Come on", "ครับ", "Jason Duval", 1)]);
        Assert.Equal("ครับ", m.Lookup("Come on", "Jay")!.Thai);
        Assert.Equal("ค่ะ", m.Lookup("Come on", "Lu")!.Thai);
        Assert.Equal("ค่ะ", m.Lookup("Come on", null)!.Thai); // lowest Order
    }

    [Fact]
    public void Lookup_ReleasedBeatsLearned()
    {
        var m = M([L("Come on", "released", null, 5)], [L("Come on", "learned", null, 0)]);
        Assert.Equal("released", m.Lookup("Come on", null)!.Thai);
    }

    [Fact]
    public void Lookup_LearnedIsFuzzyToo()
    {
        var m = M([], [L(Long, LongTh)]);
        Assert.Equal(LongTh, m.Lookup("and try to get a few grond stacked away", null)!.Thai);
    }

    [Fact]
    public void Lookup_ShortTargetSurvivesLongerLinesCrowdingTheShortlist()
    {
        var lines = new List<LibraryLine>();
        for (int k = 0; k < 100; k++)
            lines.Add(L($"stop that now and then go home number {k}", "x" + k, null, k));
        lines.Add(L("stop that now", "หยุดเดี๋ยวนี้", null, 1000)); // last: loses shortlist ties by index
        var hit = M(lines).Lookup("stop thxt now", null);
        Assert.NotNull(hit);
        Assert.Equal("หยุดเดี๋ยวนี้", hit!.Thai);
    }

    [Fact]
    public void Lookup_FloorIsCheckedExactlyNotOnTheRoundedScore()
    {
        // 13 chars each, LCS 11: raw 84.6 rounds to 85 but must not match.
        Assert.Equal(85, LibraryText.IndelRatio("abcdefghijkxy", "abcdefghijklm"));
        Assert.Null(M([L("abcdefghijklm", "x")]).Lookup("abcdefghijkxy", null));
    }

    [Fact]
    public void AddLearned_IsFoundNext()
    {
        var m = Basic();
        Assert.Null(m.Lookup("Where is the money", null));
        m.AddLearned(L("Where is the money", "เงินอยู่ไหน"));
        Assert.Equal("เงินอยู่ไหน", m.Lookup("where is the money?", null)!.Thai);
    }

    [Fact]
    public void AddLearned_IsFoundFuzzily()
    {
        var m = Basic();
        m.AddLearned(L("Where is the money you owe me", "เงินที่ติดฉันอยู่ไหน"));
        Assert.Equal("เงินที่ติดฉันอยู่ไหน", m.Lookup("where is the monay you owe me", null)?.Thai);
    }

    [Fact]
    public void AddLearned_SameTextTwice_FirstStillWins()
    {
        var m = M([]);
        m.AddLearned(L("Where is the money you owe me", "first", null, 0));
        m.AddLearned(L("Where is the money you owe me", "second", null, 1));
        Assert.Equal("first", m.Lookup("where is the monay you owe me", null)?.Thai);
    }

    private static List<LibraryLine> ManyLearned(int count, int seed)
    {
        var rnd = new Random(seed);
        var words = Enumerable.Range(0, 400)
            .Select(_ => new string(Enumerable.Range(0, 3 + rnd.Next(6)).Select(_ => (char)('a' + rnd.Next(26))).ToArray()))
            .ToArray();
        var lines = new List<LibraryLine>(count);
        for (int i = 0; i < count; i++)
            lines.Add(L(string.Join(' ', Enumerable.Range(0, 6 + rnd.Next(6)).Select(_ => words[rnd.Next(words.Length)])), "t" + i, null, i));
        return lines;
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Lookup_ManyLearned_FastAndRight()
    {
        var learned = ManyLearned(20000, 5);
        var m = M([L(Long, LongTh)], learned);
        var rnd = new Random(6);
        var picks = Enumerable.Range(0, 100).Select(_ => rnd.Next(learned.Count)).ToList();
        m.Lookup("warm up the matcher once", null);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var i in picks)
        {
            var chars = learned[i].En.ToCharArray();
            int p = rnd.Next(chars.Length);
            chars[p] = chars[p] == 'q' ? 'z' : 'q';
            Assert.Equal("t" + i, m.Lookup(new string(chars), null)?.Thai);
        }
        sw.Stop();
        var avgMs = sw.Elapsed.TotalMilliseconds / picks.Count;
        Assert.True(avgMs < 5, $"avg {avgMs:F2} ms");
    }

    private static LibraryLine V(string en, string th, string? scene, int order, string? video = "v1") =>
        new(en, th, "claude", scene, video, null, order);

    private const string RunA = "We owe that bitch of a landlady her rent, so I'm gonna stay out here a while,";
    private const string RunB = "and try to get a few grand stacked away.";

    [Fact]
    public void Lookup_JoinsTwoConsecutiveLines()
    {
        var m = M([V(RunA, "ไทย1", "s", 0), V(RunB, "ไทย2", "s", 1)]);
        var hit = m.Lookup("We owe that bitch of a landlady her rent, so I'm gonna stay out here a while, and try to get a few grand stacked away.", null);
        Assert.NotNull(hit);
        Assert.Equal("ไทย1 ไทย2", hit!.Thai);
    }

    [Fact]
    public void Lookup_DoesNotJoinAcrossScenes()
    {
        var m = M([V(RunA, "ไทย1", "s1", 0), V(RunB, "ไทย2", "s2", 1)]);
        Assert.Null(m.Lookup(RunA + " " + RunB, null));
    }

    [Fact]
    public void Lookup_DoesNotJoinWithoutVideo()
    {
        var m = M([V(RunA, "ไทย1", null, 0, null), V(RunB, "ไทย2", null, 1, null)]);
        Assert.Null(m.Lookup(RunA + " " + RunB, null));
    }

    [Fact]
    public void Lookup_SingleBeatsRunAtEqualScore()
    {
        var m2 = M([V("alpha beta gamma delta", "a", "s", 0), V("epsilon zeta", "b", "s", 1),
                    V("alpha beta gamma delta epsilon zeta", "single", "s", 9)]);
        Assert.Equal("single", m2.Lookup("alpha beta gamma delta epsilon zeta", null)!.Thai);
    }

    [Fact]
    public void Lookup_SplitsSentences()
    {
        var m = M([L("Get in.", "ขึ้นรถ", null, 0), L("We're leaving now.", "เราไปแล้วนะ", null, 5)]);
        var hit = m.Lookup("Get in. We're leaving now.", null);
        Assert.NotNull(hit);
        Assert.Equal("ขึ้นรถ เราไปแล้วนะ", hit!.Thai);
    }

    [Fact]
    public void Lookup_SplitNeedsEverySentence()
    {
        var m = M([L("Get in.", "ขึ้นรถ", null, 0), L("We're leaving now.", "เราไปแล้วนะ", null, 5)]);
        Assert.Null(m.Lookup("Get in. Something totally new here.", null));
    }

    [Fact]
    public void Lookup_SplitScoreIsMinimum()
    {
        var m = M([L("We're leaving now, all of us.", "a", null, 0), L("Get in the car quickly.", "b", null, 5)]);
        var hit = m.Lookup("We're leaving now, all of us. Get in the cxr quickly.", null);
        Assert.NotNull(hit);
        Assert.True(hit!.Score < 100 && hit.Score >= 85);
    }

    [Fact]
    public void Lookup_CacheIsClearedByAddLearned()
    {
        var m = Basic();
        Assert.Null(m.Lookup("Where is the money", null));
        Assert.Null(m.Lookup("Where is the money", null)); // cached miss
        m.AddLearned(L("Where is the money", "เงินอยู่ไหน"));
        Assert.NotNull(m.Lookup("Where is the money", null));
    }

    [Fact]
    public void Lookup_ConcurrentWithAddLearned_DoesNotThrow()
    {
        var m = Basic();
        var thread = new Thread(() =>
        {
            for (int i = 0; i < 300; i++) m.AddLearned(L($"learned line number {i} here", "x", null, i));
        }); thread.Start();
        for (int i = 0; i < 300; i++) m.Lookup($"and try to get a few grond stacked away {i % 7}", null);
        thread.Join();
        Assert.NotNull(m.Lookup("learned line number 5 here", null));
    }

    // A screen line longer than 255 chars (more distinct trigrams than a byte holds) against 64+ near-identical
    // lines of up to 255 chars: the shortlist bar must not wrap around, or the best line is dropped and a worse
    // learned line wins. The old matcher answered BASE at 91.
    [Fact]
    public void Lookup_LongQuery_ManyNearDuplicates_KeepsTheBest()
    {
        static string Words(Random r, int len)
        {
            var sb = new System.Text.StringBuilder();
            while (sb.Length < len)
            {
                int w = r.Next(3, 9);
                for (int i = 0; i < w; i++) sb.Append((char)('a' + r.Next(26)));
                sb.Append(' ');
            }
            return sb.ToString(0, len).Trim();
        }

        var r = new Random(7);
        string baseLine = Words(r, 250);
        var released = new List<LibraryLine> { L(baseLine, "BASE", null, 0) };
        for (int i = 1; i < 80; i++)
        {
            var c = baseLine.ToCharArray();
            int p;
            do p = r.Next(c.Length); while (c[p] == ' ');
            c[p] = c[p] == 'z' ? 'y' : (char)(c[p] + 1);
            released.Add(L(new string(c), "VAR" + i, null, i));
        }
        for (int i = 0; i < 200; i++) released.Add(L(Words(r, 60), "OTHER" + i, null, 100 + i));
        string query = baseLine + " " + Words(r, 50);
        var worse = baseLine.ToCharArray();
        worse[3] = worse[3] == 'a' ? 'b' : 'a';
        worse[40] = ' ';
        worse[90] = worse[90] == 'q' ? 'r' : 'q';

        var hit = M(released, [L(new string(worse), "LEARNED-WORSE", null, 0)]).Lookup(query, null);

        Assert.NotNull(hit);
        Assert.Equal("BASE", hit!.Thai);
        Assert.Equal(91, hit.Score);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Lookup_Real25k_Fast()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "PSRemoteSub.sln")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var data = LibraryLoader.Load(Path.Combine(root, "library", "gta6"), "gta6");
        Assert.NotNull(data);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var m = new LibraryMatcher(data.Lines, [], CharacterIndexTests.Sample());
        sw.Stop();
        var buildMs = sw.Elapsed.TotalMilliseconds;

        var rnd = new Random(12345);
        var inputs = new List<string>();
        for (int i = 0; i < 100; i++)
        {
            var chars = data.Lines[rnd.Next(data.Lines.Count)].En.ToCharArray();
            chars[rnd.Next(chars.Length)] = (char)('a' + rnd.Next(26));
            inputs.Add(new string(chars));
        }
        for (int i = 0; i < 100; i++)
        {
            var chars = new char[10 + rnd.Next(60)];
            for (int k = 0; k < chars.Length; k++) chars[k] = rnd.Next(7) == 0 ? ' ' : (char)('a' + rnd.Next(26));
            inputs.Add(new string(chars));
        }

        sw.Restart();
        for (int i = 0; i < 200; i++) m.Lookup(inputs[i], null);
        sw.Stop();
        var avgMs = sw.Elapsed.TotalMilliseconds / 200;
        Console.WriteLine($"PERF build={buildMs:F0}ms avg={avgMs:F3}ms");

        Assert.True(buildMs < 3000, $"build {buildMs:F0} ms");
        Assert.True(avgMs < 5, $"avg {avgMs:F3} ms");
    }
}
