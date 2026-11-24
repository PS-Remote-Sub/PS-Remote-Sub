using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PSRemoteSub.Core.Translation;

namespace PSRemoteSub.Core.Library;

public sealed record LibraryHit(string Thai, int Score);

/// <summary>
/// Finds an already-translated library line for OCR'd text. A wrong match is worse than a miss,
/// so every non-exact match must pass the floor, the length ratio, the negation check and the digit check (the same
/// numbers on both sides).
/// </summary>
/// <remarks>
/// Candidates are single released lines and runs of 2-3 consecutive released lines (same non-null Video and Scene).
/// Only singles are indexed; a run's shared count is bounded by its parts' counts and taken exactly from its text only
/// when that bound can reach the shortlist. The fuzzy shortlist is the best <see cref="Shortlist"/> entries by shared
/// distinct trigrams (ties: the lower index in the singles-then-runs order). Entries that cannot share enough trigrams
/// to pass the floor (see <see cref="MinShared"/>) are never gathered: they rank below every entry that can, and would
/// fail the floor anyway, so the result is the same.
/// </remarks>
public sealed class LibraryMatcher
{
    public const int Floor = 85;
    private const int MinFuzzyLength = LibraryIndex.MinFuzzyLength;
    private const int Shortlist = 64;
    private const double MinLengthRatio = 0.6;
    private const int CacheCapacity = 512;

    /// <summary>A learned line's payload.</summary>
    private sealed record Entry(string Thai, string? Speaker, int Order);

    /// <summary>A candidate payload: a released single (line position), a released run (run id) or a learned line.</summary>
    private readonly record struct Ent(byte Kind, int Ref, Entry? Learned)
    {
        public const byte Single = 0, Run = 1, Learn = 2;
        public bool IsRun => Kind == Run;
        public bool Released => Kind != Learn;
    }

    /// <summary>A shortlist candidate: shared trigram count, index in the old combined order, payload.</summary>
    private readonly record struct Cand(int Count, int Vindex, bool IsRun, int Ref);

    private readonly IReadOnlyList<LibraryLine> _released;
    private readonly LibraryIndex _index;
    private readonly CharacterIndex _characters;

    // Lookup runs on the tick thread, AddLearned on the AI lane: the cache is guarded by _cacheLock,
    // never held around the search. _cacheVersion stops a search that raced AddLearned from caching.
    private readonly object _cacheLock = new();
    private readonly LruCache<(string, string?), LibraryHit?> _cache = new(CacheCapacity);
    private int _cacheVersion;

    // Learned lines, guarded by _learnedLock (AddLearned on the AI lane, Lookup on the tick): entries by text, the
    // distinct texts by learned id (first-added order) and their trigram postings (texts of MinFuzzyLength or more).
    private readonly object _learnedLock = new();
    private readonly Dictionary<string, List<Entry>> _learned = new(StringComparer.Ordinal);
    private readonly List<string> _learnedTexts = [];
    private int _learnedCount;
    private readonly Dictionary<int, List<int>> _learnedPostings = [];

    public LibraryMatcher(IReadOnlyList<LibraryLine> released, IReadOnlyList<LibraryLine> learned, CharacterIndex characters)
        : this(released, learned, characters, LibraryIndex.Build(released))
    {
    }

    internal LibraryMatcher(IReadOnlyList<LibraryLine> released, IReadOnlyList<LibraryLine> learned, CharacterIndex characters,
        LibraryIndex index)
    {
        if (index.LineCount != released.Count) throw new ArgumentException("Index does not match the released lines.", nameof(index));
        _released = released;
        _index = index;
        _characters = characters;
        foreach (var line in learned) AddLearned(line);
    }

    /// <summary>Lines taught with <see cref="AddLearned"/> (the AI's answers this session; never saved).</summary>
    public int LearnedCount
    {
        get { lock (_learnedLock) return _learnedCount; }
    }

    public void AddLearned(LibraryLine line)
    {
        var n = LibraryText.Normalize(line.En);
        if (n.Length == 0) return;
        lock (_learnedLock)
        {
            if (!_learned.TryGetValue(n, out var list))
            {
                _learned[n] = list = [];
                int id = _learnedTexts.Count;
                _learnedTexts.Add(n);
                if (n.Length >= MinFuzzyLength)
                    foreach (var key in Trigrams(n))
                    {
                        if (!_learnedPostings.TryGetValue(key, out var posting)) _learnedPostings[key] = posting = [];
                        posting.Add(id);
                    }
            }
            list.Add(new Entry(line.Th, line.Speaker, line.Order));
            _learnedCount++;
        }
        lock (_cacheLock)
        {
            _cacheVersion++;
            _cache.Clear();
        }
    }

    public LibraryHit? Lookup(string text, string? speakerLabel)
    {
        var norm = LibraryText.Normalize(text);
        if (norm.Length == 0) return null;
        var speaker = _characters.Resolve(speakerLabel)?.En;
        var key = (norm, speaker);

        int version;
        lock (_cacheLock)
        {
            if (_cache.TryGet(key, out var cached)) return cached;
            version = _cacheVersion;
        }

        var hit = LookupWhole(text, speaker) ?? LookupSentences(text, speaker);

        lock (_cacheLock)
            if (version == _cacheVersion) _cache.Set(key, hit);
        return hit;
    }

    /// <summary>Every sentence must hit; the Thai is joined with a space and the score is the minimum.</summary>
    private LibraryHit? LookupSentences(string text, string? speaker)
    {
        var sentences = LibraryText.SplitSentences(text);
        if (sentences.Count < 2) return null;
        var thai = new List<string>(sentences.Count);
        int min = 100;
        foreach (var sentence in sentences)
        {
            var h = LookupWhole(sentence, speaker);
            if (h is null) return null;
            thai.Add(h.Thai);
            min = Math.Min(min, h.Score);
        }
        return new LibraryHit(string.Join(' ', thai), min);
    }

    /// <summary>Whole-line search of one text over singles and runs; speaker is already resolved.</summary>
    private LibraryHit? LookupWhole(string text, string? speaker)
    {
        var norm = LibraryText.Normalize(text);
        if (norm.Length == 0) return null;

        var exact = Collect(norm);
        if (exact.Count > 0) return new LibraryHit(Thai(Choose(exact, speaker)), 100);

        if (norm.Length < MinFuzzyLength) return null;

        var query = FuzzyQuery.Of(norm);
        if (query is null) return null; // no entry of a length in the window can pass the floor
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in ShortlistMain(query))
        {
            var s = c.IsRun ? _index.RunString(c.Ref) : _index.NormString(c.Ref);
            if (seen.Add(s)) candidates.Add(s);
        }
        foreach (var n in LearnedCandidates(query))
            if (seen.Add(n)) candidates.Add(n);

        bool negated = LibraryText.IsNegated(norm);
        Ent best = default;
        bool any = false;
        int bestScore = 0;
        foreach (var cand in candidates)
        {
            int shorter = Math.Min(cand.Length, norm.Length), longer = Math.Max(cand.Length, norm.Length);
            if (shorter < MinLengthRatio * longer) continue;
            int lcs = LibraryText.Lcs(norm, cand), total = norm.Length + cand.Length;
            if (!LibraryText.MeetsFloor(lcs, total, Floor)) continue;
            int score = LibraryText.IndelRatio(lcs, total);
            if (LibraryText.IsNegated(cand) != negated) continue;
            if (!LibraryText.SameDigitTokens(norm, cand)) continue; // "50 grand" is not "60 grand"
            var entries = Collect(cand);
            if (entries.Count == 0) continue;
            var chosen = Choose(entries, speaker);
            // Higher score wins; at equal score a single line beats a run, then the lower Order.
            if (!any || score > bestScore
                || (score == bestScore && (chosen.IsRun, Order(chosen)).CompareTo((best.IsRun, Order(best))) < 0))
                (best, bestScore, any) = (chosen, score, true);
        }
        return any ? new LibraryHit(Thai(best), bestScore) : null;
    }

    /// <summary>Every entry whose normalized text is <paramref name="norm"/>: singles, then runs, then learned lines.</summary>
    private List<Ent> Collect(string norm)
    {
        var all = new List<Ent>();
        ulong hash = LibraryIndex.Hash(norm);
        int id = _index.FindSingle(norm, hash);
        if (id >= 0)
            for (int k = _index.NormLineStart[id]; k < _index.NormLineStart[id + 1]; k++)
                all.Add(new Ent(Ent.Single, _index.NormLines[k], null));
        var runs = new List<int>();
        _index.FindRuns(norm, hash, runs);
        foreach (var r in runs) all.Add(new Ent(Ent.Run, r, null));
        lock (_learnedLock)
            if (_learned.TryGetValue(norm, out var l))
                foreach (var e in l) all.Add(new Ent(Ent.Learn, 0, e));
        return all;
    }

    private LibraryLine FirstLine(Ent e) => _released[e.Kind == Ent.Single ? e.Ref : _index.Ordered[e.Ref >> 1]];

    private int Order(Ent e) => e.Kind == Ent.Learn ? e.Learned!.Order : FirstLine(e).Order;

    private string? Speaker(Ent e) => e.Kind == Ent.Learn ? e.Learned!.Speaker : FirstLine(e).Speaker;

    private string Thai(Ent e)
    {
        if (e.Kind == Ent.Learn) return e.Learned!.Thai;
        if (e.Kind == Ent.Single) return _released[e.Ref].Th;
        int i = e.Ref >> 1, len = (e.Ref & 1) + 2;
        var parts = new string[len];
        for (int j = 0; j < len; j++) parts[j] = _released[_index.Ordered[i + j]].Th;
        return string.Join(' ', parts);
    }

    /// <summary>A single before a run, then the speaker's line, then released before learned, then the lower Order; first wins ties.</summary>
    private Ent Choose(List<Ent> entries, string? speaker)
    {
        (int, int, int, int) Rank(Ent e) => (
            e.IsRun ? 1 : 0,
            speaker is not null && string.Equals(Speaker(e), speaker, StringComparison.OrdinalIgnoreCase) ? 0 : 1,
            e.Released ? 0 : 1,
            Order(e));
        var best = entries[0];
        var bestRank = Rank(best);
        for (int k = 1; k < entries.Count; k++)
        {
            var r = Rank(entries[k]);
            if (r.CompareTo(bestRank) < 0) (best, bestRank) = (entries[k], r);
        }
        return best;
    }

    /// <summary>
    /// A fuzzy search: the query's distinct trigrams, the length window of entries that can pass the 0.6 ratio (and are
    /// long enough to be fuzzy-matched), and the least shared trigrams an entry in it needs to pass the floor.
    /// </summary>
    private sealed record FuzzyQuery(List<int> Keys, int Wlo, int Hi, int Need)
    {
        /// <summary>Null when no entry of a length in the window can pass the floor.</summary>
        public static FuzzyQuery? Of(string norm)
        {
            int n = norm.Length;
            // Lengths passing the 0.6 ratio form one interval [lo, hi].
            static bool Ok(int len, int n) => Math.Min(len, n) >= MinLengthRatio * Math.Max(len, n);
            int lo = (int)(MinLengthRatio * n);
            while (!Ok(lo, n)) lo++;
            int hi = (int)(n / MinLengthRatio) + 1;
            while (!Ok(hi, n)) hi--;
            var keys = Trigrams(norm);
            int wlo = Math.Max(lo, MinFuzzyLength);
            int need = MinShared(n, keys.Count, wlo, hi);
            return need > keys.Count ? null : new FuzzyQuery(keys, wlo, hi, Math.Max(need, 1));
        }
    }

    /// <summary>
    /// Learned texts that can pass the floor (enough shared trigrams, length in the window), in the order they were
    /// first learned: the old scan's order, so ties resolve the same way.
    /// </summary>
    private List<string> LearnedCandidates(FuzzyQuery q)
    {
        var result = new List<string>();
        lock (_learnedLock)
        {
            if (_learnedTexts.Count == 0) return result;
            var counts = t_learnedCounts is { } pooled && pooled.Length >= _learnedTexts.Count
                ? pooled
                : t_learnedCounts = new int[Math.Max(_learnedTexts.Count * 2, 256)];
            var touched = new List<int>();
            foreach (var key in q.Keys)
                if (_learnedPostings.TryGetValue(key, out var posting))
                    foreach (var id in posting)
                        if (counts[id]++ == 0) touched.Add(id);
            var ids = new List<int>();
            foreach (var id in touched)
            {
                int len = _learnedTexts[id].Length;
                if (counts[id] >= q.Need && len >= q.Wlo && len <= q.Hi) ids.Add(id);
                counts[id] = 0;
            }
            ids.Sort();
            foreach (var id in ids) result.Add(_learnedTexts[id]);
        }
        return result;
    }

    [ThreadStatic] private static int[]? t_learnedCounts;

    /// <summary>
    /// Lower bound on the distinct trigrams a text of length n (k distinct trigrams) shares with any entry of a length
    /// in [mLo, hi] that passes the floor; int.MaxValue when no such length can pass. An LCS of L leaves n - L query
    /// characters unmatched (each breaks at most 3 trigram positions) and m - L entry characters (each gap breaks at
    /// most 2), and repeated trigrams lose at most n - k more.
    /// </summary>
    internal static int MinShared(int n, int k, int mLo, int hi)
    {
        int min = int.MaxValue;
        for (int m = mLo; m <= hi; m++)
        {
            int l = (Floor * (n + m) + 199) / 200; // least LCS with 200 * LCS >= Floor * (n + m)
            if (l > Math.Min(n, m)) continue;
            min = Math.Min(min, k - 3 * (n - l) - 2 * (m - l));
        }
        return min;
    }

    /// <summary>
    /// The shortlist in evaluation order (ascending count, then descending index), as the old heap gave it.
    /// </summary>
    /// <remarks>
    /// <para>Every single's exact shared count is computed: common trigrams (kept as bitsets) are added 64 singles at
    /// a time with bit-sliced counters, so they cost a sequential read of the bitset instead of a scattered walk of a
    /// long posting list; only the rarer trigrams are walked.</para>
    /// <para>A run shares at most the sum of its parts' counts plus its junction trigrams; only the few runs whose sum
    /// reaches the bar have their exact count taken from their text.</para>
    /// </remarks>
    private List<Cand> ShortlistMain(FuzzyQuery q) =>
        _index.MaxNormLength <= byte.MaxValue ? ShortlistOf(q, ref t_counts8) : ShortlistOf(q, ref t_counts16);

    /// <summary><see cref="ShortlistMain"/> with counters of type T (a byte when no single is longer than 255).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private List<Cand> ShortlistOf<T>(FuzzyQuery q, ref T[]? pool) where T : unmanaged, IBinaryInteger<T>
    {
        var index = _index;
        var query = q.Keys;
        int k = query.Count, need = q.Need, wlo = q.Wlo, hi = q.Hi;

        // Bitset trigrams (at most 255, the counters' width) are counted; the others are walked.
        var dense = new List<int>();
        var walkedSlots = new List<int>();
        foreach (var key in query)
        {
            int s = Array.BinarySearch(index.Keys, key);
            if (s < 0) continue;
            if (index.DenseOf[s] >= 0 && dense.Count < 255) dense.Add(index.DenseOf[s]);
            else walkedSlots.Add(s);
        }

        // counts[id]: shared distinct trigrams of single id (exact).
        int normCount = index.NormCount;
        int blocks = index.DenseWords;
        var counts = pool is { } pc && pc.Length >= blocks * 64 ? pc : pool = new T[Math.Max(blocks * 64, 64)];
        if (dense.Count > 0) CountDense(index, dense, counts);
        else Array.Clear(counts, 0, blocks * 64);
        foreach (var s in walkedSlots)
        {
            int d = index.DenseOf[s];
            if (d < 0)
            {
                foreach (var id in index.Post.AsSpan(index.PostStart[s], index.PostStart[s + 1] - index.PostStart[s]))
                    counts[id]++;
                continue;
            }
            var bits = index.Dense.AsSpan((int)((long)d * blocks), blocks); // a bitset beyond the counters' width
            for (int w = 0; w < blocks; w++)
                for (ulong word = bits[w]; word != 0; word &= word - 1)
                    counts[(w << 6) + BitOperations.TrailingZeroCount(word)]++;
        }

        var top = new Top(need);

        // Singles: the Shortlist-th best count in the window first, then only those at or above it, in id order.
        bool simd = typeof(T) == typeof(byte) && Avx2.IsSupported && hi < ushort.MaxValue;
        int bar = simd
            ? SinglesAvx2((byte[])(object)counts, index, wlo, hi, need, k, top)
            : Singles(counts, index, wlo, hi, need, k, top);

        // Runs: the sum of the parts' counts plus the junctions bounds a run's count; only runs reaching the bar in
        // the window are counted exactly.
        var scanner = t_scanner ??= new Scanner();
        scanner.Reset(query);
        var orderedNorm = index.OrderedNorm;
        var runMax = index.RunMax;
        int lineCount = index.LineCount;
        var starts = t_starts ??= [];
        starts.Clear();
        if (simd && bar <= byte.MaxValue)
            RunStartsAvx2((byte[])(object)counts, index, bar, wlo, hi, starts);
        else
            for (int i = 0; i < lineCount; i++)
                if (runMax[i] >= 2) starts.Add(i);
        foreach (var i in starts)
        {
            int c0 = CountAt(counts, orderedNorm, i), c1 = CountAt(counts, orderedNorm, i + 1);
            int c2 = runMax[i] == 3 ? CountAt(counts, orderedNorm, i + 2) : 0;
            if (c0 + c1 + c2 + 2 < bar) continue;
            for (int rlen = 2; rlen <= runMax[i]; rlen++)
            {
                if ((rlen == 2 ? c0 + c1 + 1 : c0 + c1 + c2 + 2) < bar) continue;
                int runId = 2 * i + rlen - 2;
                int v = index.RunVindex[runId];
                if (v < 0) continue;
                int m = (rlen == 2 ? index.Run2Length : index.Run3Length)[i];
                if (m == ushort.MaxValue) m = index.RunTextLength(runId);
                if (m < wlo || m > hi) continue;
                int c = scanner.CountRun(index, runId);
                if (c < bar) continue;
                top.Offer(new Cand(c, v, true, runId));
                bar = top.Bar;
            }
        }
        scanner.Done();
        return top.InOrder();
    }

    /// <summary>Singles, one by one: a histogram of the counts in the window gives the bar, then the offers in id order.</summary>
    private static int Singles<T>(T[] counts, LibraryIndex index, int wlo, int hi, int need, int k, Top top)
        where T : unmanaged, IBinaryInteger<T>
    {
        var histogram = new int[k + 1];
        for (int id = 0; id < index.NormCount; id++)
        {
            int len = index.NormTextLength(id);
            if (len >= wlo && len <= hi) histogram[int.CreateTruncating(counts[id])]++;
        }
        for (int c = k, above = 0; c >= need; c--)
            if ((above += histogram[c]) >= Shortlist)
            {
                top.Bar = Math.Max(top.Bar, c);
                break;
            }
        int bar = top.Bar;
        for (int id = 0; id < index.NormCount; id++)
        {
            int c = int.CreateTruncating(counts[id]);
            if (c < bar) continue;
            int len = index.NormTextLength(id);
            if (len < wlo || len > hi) continue;
            top.Offer(new Cand(c, id, false, id));
            bar = top.Bar;
        }
        return bar;
    }

    private static int CountAt<T>(T[] counts, int[] orderedNorm, int j) where T : unmanaged, IBinaryInteger<T>
    {
        int id = j < orderedNorm.Length ? orderedNorm[j] : -1;
        return id < 0 ? 0 : int.CreateTruncating(counts[id]);
    }

    /// <summary>Byte mask of the 32 lengths at <paramref name="at"/> within [wlo, wlo + span] (0xFF in, 0 out).</summary>
    private static Vector256<byte> InWindow(ushort[] lengths, int at, Vector256<ushort> wlo, Vector256<ushort> span)
    {
        var a = Vector256.LessThanOrEqual(Vector256.Create(lengths.AsSpan(at, 16)) - wlo, span);
        var b = Vector256.LessThanOrEqual(Vector256.Create(lengths.AsSpan(at + 16, 16)) - wlo, span);
        var packed = Avx2.PackSignedSaturate(a.AsInt16(), b.AsInt16());
        return Avx2.Permute4x64(packed.AsInt64(), 0b11_01_10_00).AsByte();
    }

    /// <summary>
    /// Singles with byte counts, 32 at a time: the counts of the singles in the window (others 0), then the bar (the Shortlist-th best count, at least <paramref name="need"/>) by a
    /// binary search over "how many reach c", then the offers in id order.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int SinglesAvx2(byte[] counts, LibraryIndex index, int wlo, int hi, int need, int k, Top top)
    {
        int normCount = index.NormCount;
        var lengths = index.NormLength;
        var windowed = t_windowed is { } pooled && pooled.Length >= normCount + 32 ? pooled : t_windowed = new byte[normCount + 32];
        var lo16 = Vector256.Create((ushort)wlo);
        var span16 = Vector256.Create((ushort)(hi - wlo));
        int i0 = 0;
        for (; i0 + 32 <= normCount; i0 += 32)
            (Vector256.Create(counts.AsSpan(i0, 32)) & InWindow(lengths, i0, lo16, span16)).CopyTo(windowed.AsSpan(i0, 32));
        for (int id = i0; id < normCount; id++)
            windowed[id] = (uint)(lengths[id] - wlo) <= (uint)(hi - wlo) ? counts[id] : (byte)0;
        windowed.AsSpan(normCount, 32).Clear();
        int blocks = (normCount + 31) >> 5;

        int Reaching(int c)
        {
            var at = Vector256.Create((byte)c);
            int total = 0;
            for (int b = 0; b < blocks; b++)
                total += BitOperations.PopCount(Vector256.GreaterThanOrEqual(Vector256.Create(windowed.AsSpan(b << 5, 32)), at).ExtractMostSignificantBits());
            return total;
        }

        int bar = need;
        if (Reaching(need) >= Shortlist)
        {
            // Counts here are bytes, so the bar stays ≤ 255: a query can have more trigrams than that, and Reaching
            // compares against (byte)c, which would wrap.
            int lowC = need, highC = Math.Min(k, byte.MaxValue) + 1; // Reaching(lowC) >= Shortlist > Reaching(highC)
            while (highC - lowC > 1)
            {
                int mid = (lowC + highC) >>> 1;
                if (Reaching(mid) >= Shortlist) lowC = mid;
                else highC = mid;
            }
            bar = lowC;
        }
        top.Bar = Math.Max(top.Bar, bar);
        bar = top.Bar;
        for (int b = 0; b < blocks; b++)
        {
            var v = Vector256.Create(windowed.AsSpan(b << 5, 32));
            for (uint bits = Vector256.GreaterThanOrEqual(v, Vector256.Create((byte)bar)).ExtractMostSignificantBits(); bits != 0; bits &= bits - 1)
            {
                int id = (b << 5) + BitOperations.TrailingZeroCount(bits);
                int c = windowed[id];
                if (c < bar) continue;
                top.Offer(new Cand(c, id, false, id));
                bar = top.Bar;
            }
        }
        return bar;
    }

    /// <summary>
    /// Ordered indexes that start a run in the length window whose parts' counts plus junctions can reach
    /// <paramref name="bar"/> (at most 255): saturating byte sums of 3 neighbours, 32 at a time. A superset; the
    /// caller checks each run exactly.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void RunStartsAvx2(byte[] counts, LibraryIndex index, int bar, int wlo, int hi, List<int> into)
    {
        int lineCount = index.LineCount;
        byte[] pos;
        if (index.OrderedIsIdentity) pos = counts;
        else
        {
            pos = t_pos is { } pooled && pooled.Length >= lineCount + 2 ? pooled : t_pos = new byte[lineCount + 2];
            var orderedNorm = index.OrderedNorm;
            for (int i = 0; i < lineCount; i++)
            {
                int id = orderedNorm[i];
                pos[i] = id < 0 ? (byte)0 : counts[id];
            }
        }
        var len2 = index.Run2Length;
        var len3 = index.Run3Length;
        var t2 = Vector256.Create((byte)(bar - 1));
        var t3 = Vector256.Create((byte)Math.Max(bar - 2, 0));
        var lo16 = Vector256.Create((ushort)wlo);
        var span16 = Vector256.Create((ushort)(hi - wlo));
        int i0 = 0;
        // The loads read pos[i0 .. i0 + 33]; the rest is checked one by one.
        for (; i0 + 34 <= pos.Length && i0 + 32 <= lineCount; i0 += 32)
        {
            var a = Vector256.Create(pos.AsSpan(i0, 32));
            var b = Vector256.Create(pos.AsSpan(i0 + 1, 32));
            var c = Vector256.Create(pos.AsSpan(i0 + 2, 32));
            var s2 = Avx2.AddSaturate(a, b);
            var s3 = Avx2.AddSaturate(s2, c);
            var ok = (Vector256.GreaterThanOrEqual(s2, t2) & InWindow(len2, i0, lo16, span16))
                | (Vector256.GreaterThanOrEqual(s3, t3) & InWindow(len3, i0, lo16, span16));
            for (uint bits = ok.ExtractMostSignificantBits(); bits != 0; bits &= bits - 1)
                into.Add(i0 + BitOperations.TrailingZeroCount(bits));
        }
        for (int i = i0; i < lineCount; i++)
            if (index.RunMax[i] >= 2) into.Add(i);
    }

    /// <summary>The best Shortlist candidates by (count, -index) in a min-heap: the old heap's order and ties.</summary>
    private sealed class Top(int need)
    {
        private readonly PriorityQueue<Cand, (int Count, int Neg)> _heap = new();
        private readonly HashSet<int> _taken = [];

        /// <summary>Least count worth offering: the floor bound, then the full heap's worst.</summary>
        public int Bar = need;

        public void Offer(Cand c)
        {
            if (_heap.Count == Shortlist)
            {
                _heap.TryPeek(out _, out var worst);
                if ((c.Count, -c.Vindex).CompareTo(worst) <= 0) return;
            }
            if (!_taken.Add(c.Vindex)) return;
            if (_heap.Count == Shortlist) _heap.Dequeue();
            _heap.Enqueue(c, (c.Count, -c.Vindex));
            if (_heap.Count == Shortlist)
            {
                _heap.TryPeek(out _, out var worst);
                Bar = Math.Max(Bar, worst.Count);
            }
        }

        /// <summary>Ascending (count, -index), as the old heap dequeued them.</summary>
        public List<Cand> InOrder()
        {
            var result = new List<Cand>(_heap.Count);
            while (_heap.TryDequeue(out var c, out _)) result.Add(c);
            return result;
        }
    }

    /// <summary>
    /// For every norm id, how many of the <paramref name="dense"/> bitsets hold it (at most 255): bit-sliced counters
    /// per block of 64 ids (tiles of blocks, each bitset read sequentially), then spread into one lane per id.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void CountDense<T>(LibraryIndex index, List<int> dense, T[] into) where T : unmanaged, IBinaryInteger<T>
    {
        const int Tile = 32;
        int blocks = index.DenseWords;
        var bitsets = index.Dense;
        var rows = new long[dense.Count];
        for (int r = 0; r < rows.Length; r++) rows[r] = (long)dense[r] * blocks;
        var lanes = System.Runtime.InteropServices.MemoryMarshal.Cast<T, ulong>(into.AsSpan());
        bool bytes = System.Runtime.CompilerServices.Unsafe.SizeOf<T>() == 1;
        Span<ulong> slices = stackalloc ulong[Tile * 8];
        int top = 32 - BitOperations.LeadingZeroCount((uint)dense.Count); // bits needed
        for (int t0 = 0; t0 < blocks; t0 += Tile)
        {
            int tn = Math.Min(Tile, blocks - t0);
            slices.Clear();
            foreach (var row in rows)
            {
                var src = bitsets.AsSpan((int)(row + t0), tn);
                for (int t = 0; t < tn; t++)
                {
                    ulong x = src[t];
                    for (int b = t << 3; x != 0; b++)
                    {
                        ulong carry = slices[b] & x;
                        slices[b] ^= x;
                        x = carry;
                    }
                }
            }
            for (int t = 0; t < tn; t++)
            {
                var sl = slices.Slice(t << 3, 8);
                int w = t0 + t;
                if (bytes)
                    for (int chunk = 0; chunk < 8; chunk++)
                    {
                        ulong acc = 0;
                        for (int b = 0; b < top; b++) acc |= Spread8[(int)(sl[b] >> (chunk << 3)) & 0xFF] << b;
                        lanes[(w << 3) + chunk] = acc;
                    }
                else
                    for (int chunk = 0; chunk < 16; chunk++)
                    {
                        ulong acc = 0;
                        for (int b = 0; b < top; b++) acc |= Spread16[(int)(sl[b] >> (chunk << 2)) & 0xF] << b;
                        lanes[(w << 4) + chunk] = acc;
                    }
            }
        }
    }

    /// <summary>Spread8[v]: byte j holds bit j of v. Spread16[v]: 16-bit lane j holds bit j of the nibble v.</summary>
    private static readonly ulong[] Spread8 = Enumerable.Range(0, 256)
        .Select(v => Enumerable.Range(0, 8).Aggregate(0UL, (acc, j) => acc | ((ulong)((v >> j) & 1) << (8 * j))))
        .ToArray();
    private static readonly ulong[] Spread16 = Enumerable.Range(0, 16)
        .Select(v => Enumerable.Range(0, 4).Aggregate(0UL, (acc, j) => acc | ((ulong)((v >> j) & 1) << (16 * j))))
        .ToArray();

    // Pooled per-thread counters and run scanner.
    [ThreadStatic] private static byte[]? t_pos;
    [ThreadStatic] private static byte[]? t_windowed;
    [ThreadStatic] private static List<int>? t_starts;
    [ThreadStatic] private static byte[]? t_counts8;
    [ThreadStatic] private static ushort[]? t_counts16;
    [ThreadStatic] private static Scanner? t_scanner;

    /// <summary>Distinct packed trigrams of " " + norm + " " in first-seen order. Normalized text is ASCII, 7 bits per char.</summary>
    private static List<int> Trigrams(string norm)
    {
        var s = " " + norm + " ";
        var seen = new HashSet<int>();
        var list = new List<int>(s.Length);
        for (int i = 0; i + 3 <= s.Length; i++)
        {
            int key = LibraryIndex.Key(s[i], s[i + 1], s[i + 2]);
            if (seen.Add(key)) list.Add(key);
        }
        return list;
    }

    /// <summary>Counts the distinct query trigrams in a run's padded text.</summary>
    private sealed class Scanner
    {
        private readonly ulong[] _filter = new ulong[(1 << 21) / 64]; // query keys (bit per packed trigram)
        private int[] _keys = new int[64];  // open addressing: key + 1, 0 = empty
        private int[] _slots = new int[64]; // query trigram index
        private int[] _seen = new int[64];  // stamp per query trigram
        private List<int> _query = [];
        private int _mask, _stamp;
        private int _a, _b, _fed, _count;

        public void Reset(List<int> query)
        {
            _query = query;
            int size = 64;
            while (size < query.Count * 4) size <<= 1;
            if (_keys.Length < size)
            {
                _keys = new int[size];
                _slots = new int[size];
            }
            else Array.Clear(_keys);
            if (_seen.Length < query.Count)
            {
                _seen = new int[size];
                _stamp = 0;
            }
            _mask = _keys.Length - 1;
            for (int q = 0; q < query.Count; q++)
            {
                int key = query[q];
                _filter[key >> 6] |= 1UL << key;
                int h = Hash(key);
                while (_keys[h] != 0) h = (h + 1) & _mask;
                _keys[h] = key + 1;
                _slots[h] = q;
            }
        }

        /// <summary>Clears the filter for the next query.</summary>
        public void Done()
        {
            foreach (var key in _query) _filter[key >> 6] = 0;
        }

        private int Hash(int key) => (int)((uint)(key * -1640531535) >> 8) & _mask;

        private int Find(int key)
        {
            if ((_filter[key >> 6] & (1UL << key)) == 0) return -1;
            for (int h = Hash(key); ; h = (h + 1) & _mask)
            {
                int k = _keys[h];
                if (k == 0) return -1;
                if (k == key + 1) return _slots[h];
            }
        }

        private void Feed(int c)
        {
            if (_fed >= 2)
            {
                int s = Find(LibraryIndex.Key(_a, _b, c));
                if (s >= 0 && _seen[s] != _stamp)
                {
                    _seen[s] = _stamp;
                    _count++;
                }
            }
            (_a, _b) = (_b, c);
            _fed++;
        }

        public int CountRun(LibraryIndex index, int runId)
        {
            if (++_stamp == int.MaxValue)
            {
                Array.Clear(_seen);
                _stamp = 1;
            }
            _fed = 0;
            _count = 0;
            Feed(' ');
            int i = runId >> 1, len = (runId & 1) + 2;
            bool any = false;
            for (int j = i; j < i + len; j++)
            {
                int id = index.OrderedNorm[j];
                if (id < 0) continue;
                if (any) Feed(' ');
                foreach (var b in index.NormBytes(id)) Feed(b);
                any = true;
            }
            Feed(' ');
            return _count;
        }
    }
}
