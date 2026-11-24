using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PSRemoteSub.Core.Library;

namespace PSRemoteSub.Core.Tests.Library;

/// <summary>
/// Golden set: about 3,600 lookups against the real library/gta6, recorded with the matcher before the scale refactor.
/// Every query must keep exactly the same result (hit or miss, Thai and score): a wrong Thai on screen is worse than a
/// miss. The fixture is pinned to the sha256 of subtitles.jsonl; set PSRS_GOLDEN_WRITE=1 to re-record it (only after a
/// library update, and only with a matcher you trust).
/// </summary>
public sealed class LibraryGoldenTests
{
    internal const string FixtureName = "golden.jsonl";

    internal sealed record GoldenQuery(string Mode, string Query, string? Speaker, string? Thai, int Score);

    internal sealed record Golden(string Sha, IReadOnlyList<LibraryLine> Learned, IReadOnlyList<GoldenQuery> Queries);

    internal static string Root()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "PSRemoteSub.sln")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        return root;
    }

    internal static string LibraryDir => Path.Combine(Root(), "library", "gta6");

    internal static string FixturePath => Path.Combine(Root(), "tests", "PSRemoteSub.Core.Tests", "Library", FixtureName);

    internal static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    internal static Golden ReadFixture()
    {
        string? sha = null;
        var learned = new List<LibraryLine>();
        var queries = new List<GoldenQuery>();
        foreach (var raw in File.ReadLines(FixturePath))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            if (r.TryGetProperty("sha", out var s)) sha = s.GetString();
            else if (r.TryGetProperty("learn", out var l))
                learned.Add(new LibraryLine(l.GetProperty("en").GetString()!, l.GetProperty("th").GetString()!, "ai",
                    null, null, Str(l, "sp"), learned.Count));
            else
                queries.Add(new GoldenQuery(r.GetProperty("m").GetString()!, r.GetProperty("q").GetString()!, Str(r, "sp"),
                    Str(r, "th"), r.TryGetProperty("s", out var sc) ? sc.GetInt32() : 0));
        }
        Assert.NotNull(sha);
        return new Golden(sha, learned, queries);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>Runs every golden query against the matcher for its mode; returns the mismatches (empty when all agree).</summary>
    internal static List<string> Diff(Golden golden, Func<string, LibraryMatcher> matcherFor)
    {
        var bad = new List<string>();
        var matchers = new Dictionary<string, LibraryMatcher>();
        for (int i = 0; i < golden.Queries.Count; i++)
        {
            var q = golden.Queries[i];
            if (!matchers.TryGetValue(q.Mode, out var m)) matchers[q.Mode] = m = matcherFor(q.Mode);
            var hit = m.Lookup(q.Query, q.Speaker);
            if (hit?.Thai != q.Thai || (hit?.Score ?? 0) != q.Score)
                bad.Add($"#{i} [{q.Mode}] expected {(q.Thai is null ? "miss" : q.Score.ToString())}, got {(hit is null ? "miss" : hit.Score.ToString())}"
                    + (hit is not null && q.Thai is not null && hit.Thai != q.Thai ? " (different Thai)" : ""));
        }
        return bad;
    }

    internal static (LibraryData Data, Golden Golden) Load()
    {
        var data = LibraryLoader.Load(LibraryDir, "gta6");
        Assert.NotNull(data);
        var golden = ReadFixture();
        Assert.True(golden.Sha == Sha(Path.Combine(LibraryDir, "subtitles.jsonl")),
            "library/gta6/subtitles.jsonl changed since the golden set was recorded: re-record it (PSRS_GOLDEN_WRITE=1) with a trusted matcher.");
        return (data, golden);
    }

    [Fact]
    public void Golden_EveryQueryKeepsItsRecordedResult()
    {
        if (Environment.GetEnvironmentVariable("PSRS_GOLDEN_WRITE") == "1") Write();
        var (data, golden) = Load();
        var characters = new CharacterIndex(data.Info);
        var bad = Diff(golden, mode => new LibraryMatcher(data.Lines, mode == "l" ? golden.Learned : [], characters));
        Assert.True(bad.Count == 0, $"{bad.Count} of {golden.Queries.Count} golden results changed:\n" + string.Join("\n", bad.Take(30)));
    }

    // ---- recording ----

    private static readonly string[] Common =
        "man baby yeah okay listen look money car cops boss deal right now later tonight home street gun cash bro the you and to it".Split(' ');

    /// <summary>Records the fixture with the current matcher (fixed seed).</summary>
    private static void Write()
    {
        var data = LibraryLoader.Load(LibraryDir, "gta6")!;
        var lines = data.Lines;
        var rnd = new Random(20261121);
        var speakers = data.Info.Characters.SelectMany(c => c.Aliases.Prepend(c.En)).ToList();
        string? Speaker() => rnd.Next(5) == 0 && speakers.Count > 0 ? speakers[rnd.Next(speakers.Count)] : null;
        LibraryLine Any() => lines[rnd.Next(lines.Count)];

        string OneChar(string s)
        {
            var c = s.ToCharArray();
            for (int tries = 0; tries < 20; tries++)
            {
                int p = rnd.Next(c.Length);
                if (!char.IsLetter(c[p])) continue;
                char r;
                do r = (char)('a' + rnd.Next(26)); while (r == char.ToLowerInvariant(c[p]));
                c[p] = r;
                break;
            }
            return new string(c);
        }

        // Consecutive lines of one scene (the scene library has no Video; the matcher may or may not join them).
        bool Joinable(int i, int len)
        {
            if (i + len > lines.Count || lines[i].Scene is null) return false;
            for (int k = 1; k < len; k++)
                if (lines[i + k].Video != lines[i].Video || lines[i + k].Scene != lines[i].Scene) return false;
            return true;
        }

        var starts = new Dictionary<int, List<int>>();
        string Run(int len)
        {
            if (!starts.TryGetValue(len, out var s))
                starts[len] = s = Enumerable.Range(0, lines.Count).Where(i => Joinable(i, len)).ToList();
            Assert.NotEmpty(s);
            int i = s[rnd.Next(s.Count)];
            return string.Join(' ', Enumerable.Range(i, len).Select(k => lines[k].En));
        }

        string Negate(string s)
        {
            var swaps = new[] { (" is ", " isn't "), (" can ", " can't "), (" will ", " won't "), (" do ", " don't "),
                (" was ", " wasn't "), (" are ", " aren't "), (" don't ", " do "), (" can't ", " can "), ("n't ", " "), (" not ", " ") };
            foreach (var (a, b) in swaps.OrderBy(_ => rnd.Next()))
            {
                int p = s.IndexOf(a, StringComparison.Ordinal);
                if (p >= 0) return s[..p] + b + s[(p + a.Length)..];
            }
            int sp = s.IndexOf(' ');
            return sp < 0 ? "not " + s : s[..sp] + " not" + s[sp..];
        }

        var withDigits = lines.Where(l => l.En.Any(char.IsAsciiDigit)).ToList();
        var shorts = lines.Where(l => LibraryText.Normalize(l.En).Length is > 0 and < 14).ToList();
        var words = lines.SelectMany(l => LibraryText.Normalize(l.En).Split(' ')).Distinct().ToList();

        var queries = new List<(string Mode, string Q, string? Sp, string Cat)>();
        string cat = "";
        void Add(string q, string mode = "r") => queries.Add((mode, q, Speaker(), cat));

        cat = "exact";
        for (int i = 0; i < 350; i++) Add(Any().En);
        cat = "one-char";
        for (int i = 0; i < 600; i++) Add(OneChar(Any().En));
        cat = "run2";
        for (int i = 0; i < 300; i++) Add(Run(2));
        cat = "run3";
        for (int i = 0; i < 100; i++) Add(Run(3));
        cat = "run-fuzzy";
        for (int i = 0; i < 100; i++) Add(OneChar(Run(rnd.Next(2, 4))));
        cat = "two-places";
        for (int i = 0; i < 300; i++)
        {
            var a = Any();
            LibraryLine b;
            do b = Any(); while (b.Scene == a.Scene);
            Add(a.En.TrimEnd('.', '!', '?', ',', ' ') + ". " + b.En);
        }
        cat = "number-changed";
        for (int i = 0; i < 200; i++)
        {
            var s = withDigits[rnd.Next(withDigits.Count)].En.ToCharArray();
            var digits = Enumerable.Range(0, s.Length).Where(k => char.IsAsciiDigit(s[k])).ToList();
            int p = digits[rnd.Next(digits.Count)];
            s[p] = (char)('0' + (s[p] - '0' + 1 + rnd.Next(9)) % 10);
            Add(new string(s));
        }
        cat = "number-added";
        for (int i = 0; i < 50; i++)
        {
            var s = Any().En;
            int sp = s.IndexOf(' ');
            Add(sp < 0 ? "2 " + s : s[..sp] + " " + rnd.Next(2, 100) + s[sp..]);
        }
        cat = "negation";
        for (int i = 0; i < 200; i++) Add(Negate(Any().En));
        cat = "short";
        for (int i = 0; i < 150; i++) Add(shorts[rnd.Next(shorts.Count)].En);
        cat = "short-fuzzy";
        for (int i = 0; i < 150; i++) Add(OneChar(shorts[rnd.Next(shorts.Count)].En));
        cat = "extra-words";
        for (int i = 0; i < 150; i++)
            Add(Common[rnd.Next(Common.Length)] + " " + Any().En + " " + Common[rnd.Next(Common.Length)]);
        cat = "noise";
        for (int i = 0; i < 150; i++)
        {
            var c = new char[3 + rnd.Next(70)];
            for (int k = 0; k < c.Length; k++) c[k] = rnd.Next(6) == 0 ? ' ' : (char)('a' + rnd.Next(26));
            Add(new string(c));
        }
        cat = "word-salad";
        for (int i = 0; i < 150; i++)
            Add(string.Join(' ', Enumerable.Range(0, 2 + rnd.Next(14)).Select(_ => words[rnd.Next(words.Count)])));

        // Learned mode: learned lines that duplicate, nearly duplicate or extend released lines, plus new ones.
        var learned = new List<(string En, string Th, string? Sp)>();
        for (int i = 0; i < 100; i++) learned.Add((Any().En, $"ล{i}", Speaker()));
        for (int i = 0; i < 100; i++) learned.Add((OneChar(Any().En), $"ล{100 + i}", Speaker()));
        for (int i = 0; i < 100; i++)
            learned.Add((string.Join(' ', Enumerable.Range(0, 4 + rnd.Next(10)).Select(_ => words[rnd.Next(words.Count)])), $"ล{200 + i}", Speaker()));
        cat = "l-exact";
        for (int i = 0; i < 200; i++) Add(learned[rnd.Next(learned.Count)].En, "l");
        cat = "l-fuzzy";
        for (int i = 0; i < 200; i++) Add(OneChar(learned[rnd.Next(learned.Count)].En), "l");
        cat = "l-released-fuzzy";
        for (int i = 0; i < 100; i++) Add(OneChar(Any().En), "l");
        cat = "l-joined";
        for (int i = 0; i < 100; i++) Add(learned[rnd.Next(learned.Count)].En + " " + Any().En, "l");

        var released = new LibraryMatcher(lines, [], new CharacterIndex(data.Info));
        var learnedLines = learned.Select((l, k) => new LibraryLine(l.En, l.Th, "ai", null, null, l.Sp, k)).ToList();
        var withLearned = new LibraryMatcher(lines, learnedLines, new CharacterIndex(data.Info));

        var opts = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var sb = new StringBuilder();
        void Line(Action<Utf8JsonWriter> body)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, opts)) { w.WriteStartObject(); body(w); w.WriteEndObject(); }
            sb.Append(Encoding.UTF8.GetString(ms.ToArray())).Append('\n');
        }
        Line(w => w.WriteString("sha", Sha(Path.Combine(LibraryDir, "subtitles.jsonl"))));
        foreach (var l in learned)
            Line(w =>
            {
                w.WriteStartObject("learn");
                w.WriteString("en", l.En);
                w.WriteString("th", l.Th);
                if (l.Sp is not null) w.WriteString("sp", l.Sp);
                w.WriteEndObject();
            });
        foreach (var (mode, q, sp, c) in queries)
        {
            var hit = (mode == "l" ? withLearned : released).Lookup(q, sp);
            Line(w =>
            {
                w.WriteString("m", mode);
                w.WriteString("c", c);
                w.WriteString("q", q);
                if (sp is not null) w.WriteString("sp", sp);
                if (hit is not null)
                {
                    w.WriteString("th", hit.Thai);
                    w.WriteNumber("s", hit.Score);
                }
            });
        }
        File.WriteAllText(FixturePath, sb.ToString(), new UTF8Encoding(false));
    }
}
