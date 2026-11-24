using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PSRemoteSub.Core.Diagnostics;

namespace PSRemoteSub.Core.Library;

/// <summary>
/// Immutable index of the released lines for <see cref="LibraryMatcher"/>: the distinct normalized single lines (ASCII
/// bytes), their trigram postings, and the runs of 2-3 consecutive lines. Runs are not indexed by trigram: the matcher
/// bounds them by their parts. Run texts are kept only as a sorted hash table (for exact hits) and as their place in
/// the old combined singles-then-runs order (shortlist ties).
/// </summary>
internal sealed class LibraryIndex
{
    internal const int MinFuzzyLength = 8;

    // ---- stored ----

    /// <summary>Released lines indexed (the list the matcher is given).</summary>
    public int LineCount { get; }

    /// <summary>Distinct normalized singles by norm id (first-seen order): bytes [TextStart[id], TextStart[id + 1]).</summary>
    public byte[] Text { get; }
    public int[] TextStart { get; }

    /// <summary>Per released line: its norm id, or -1 when it normalizes to nothing.</summary>
    public int[] LineNorm { get; }

    /// <summary>Norm id to its released line positions (released order): NormLines[NormLineStart[id]..NormLineStart[id + 1]).</summary>
    public int[] NormLineStart { get; }
    public int[] NormLines { get; }

    /// <summary>Released positions stably sorted by Order; runs are consecutive in this order.</summary>
    public int[] Ordered { get; }

    /// <summary>Per ordered index: the longest run starting there (1 = none, 2 or 3).</summary>
    public byte[] RunMax { get; }

    /// <summary>
    /// Per run id (2 * ordered index + length - 2): the run's index in the old combined order (singles first, then new
    /// run texts), equal to a single's norm id when the run's text equals that single; -1 when not a run or empty.
    /// </summary>
    public int[] RunVindex { get; }

    /// <summary>Exact hit tables, sorted by hash: singles (norm id) and runs (run id).</summary>
    public ulong[] SingleHash { get; }
    public int[] SingleHashId { get; }
    public ulong[] RunHash { get; }
    public int[] RunHashId { get; }

    /// <summary>
    /// Trigram postings of every single (any length): the present trigram keys, sorted, and for the key at slot s the
    /// ascending norm ids Post[PostStart[s]..PostStart[s + 1]).
    /// </summary>
    public int[] Keys { get; }
    public int[] PostStart { get; }
    public int[] Post { get; }

    /// <summary>
    /// Per key slot: -1, or the number of its bitset in <see cref="Dense"/> (<see cref="DenseWords"/> ulongs, bit =
    /// norm id). Common trigrams are kept as bitsets instead of postings: smaller, and counted 64 singles at a time.
    /// </summary>
    public int[] DenseOf { get; }
    public ulong[] Dense { get; }

    // ---- derived ----

    public int NormCount => TextStart.Length - 1;

    public int DenseWords => (NormCount + 63) >> 6;

    /// <summary>Per ordered index: the norm id of that line, or -1.</summary>
    public int[] OrderedNorm { get; }

    /// <summary>True when ordered index i is line i with norm id i (no reordering, no duplicate or empty norms).</summary>
    public bool OrderedIsIdentity { get; }

    /// <summary>Per norm id: its text length, capped at 65535.</summary>
    public ushort[] NormLength { get; }

    /// <summary>The longest norm.</summary>
    public int MaxNormLength { get; }

    /// <summary>Per ordered index: the text length of the 2-line and 3-line run starting there, 0 when none (capped at 65535).</summary>
    public ushort[] Run2Length { get; }
    public ushort[] Run3Length { get; }

    private LibraryIndex(int lineCount, byte[] text, int[] textStart, int[] lineNorm, int[] normLineStart, int[] normLines,
        int[] ordered, byte[] runMax, int[] runVindex, ulong[] singleHash, int[] singleHashId, ulong[] runHash,
        int[] runHashId, int[] keys, int[] postStart, int[] post, int[] denseOf, ulong[] dense)
    {
        LineCount = lineCount;
        Text = text;
        TextStart = textStart;
        LineNorm = lineNorm;
        NormLineStart = normLineStart;
        NormLines = normLines;
        Ordered = ordered;
        RunMax = runMax;
        RunVindex = runVindex;
        SingleHash = singleHash;
        SingleHashId = singleHashId;
        RunHash = runHash;
        RunHashId = runHashId;
        Keys = keys;
        PostStart = postStart;
        Post = post;
        DenseOf = denseOf;
        Dense = dense;

        OrderedNorm = new int[lineCount];
        bool identity = NormCount == lineCount;
        for (int i = 0; i < lineCount; i++)
        {
            OrderedNorm[i] = lineNorm[ordered[i]];
            identity &= OrderedNorm[i] == i;
        }
        OrderedIsIdentity = identity;
        NormLength = new ushort[NormCount];
        for (int id = 0; id < NormCount; id++)
        {
            NormLength[id] = (ushort)Math.Min(TextLength(id), ushort.MaxValue);
            MaxNormLength = Math.Max(MaxNormLength, TextLength(id));
        }
        Run2Length = new ushort[lineCount];
        Run3Length = new ushort[lineCount];
        for (int runId = 0; runId < runVindex.Length; runId++)
            if (runVindex[runId] >= 0)
                (runId % 2 == 0 ? Run2Length : Run3Length)[runId >> 1] = (ushort)Math.Min(RunTextLength(runId), ushort.MaxValue);
    }

    public int TextLength(int id) => TextStart[id + 1] - TextStart[id];

    public ReadOnlySpan<byte> NormBytes(int id) => Text.AsSpan(TextStart[id], TextStart[id + 1] - TextStart[id]);

    public string NormString(int id) => System.Text.Encoding.ASCII.GetString(NormBytes(id));

    /// <summary>A single's text length (not capped).</summary>
    public int NormTextLength(int id) => NormLength[id] == ushort.MaxValue ? TextLength(id) : NormLength[id];

    /// <summary>A run's text length: its non-empty parts plus a space between each.</summary>
    public int RunTextLength(int runId)
    {
        int i = runId >> 1, len = (runId & 1) + 2, m = -1;
        for (int j = i; j < i + len; j++)
            if (OrderedNorm[j] >= 0) m += TextLength(OrderedNorm[j]) + 1;
        return m;
    }

    // ---- hashing (FNV-1a 64: stable across processes, it is stored in the cache file) ----

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static ulong Hash(string norm)
    {
        ulong h = FnvOffset;
        foreach (var c in norm) h = (h ^ (byte)c) * FnvPrime;
        return h;
    }

    private static ulong Hash(ulong h, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) h = (h ^ b) * FnvPrime;
        return h;
    }

    // ---- runs ----

    /// <summary>The run's text: its non-empty parts' norms joined with a space (empty when every part is empty).</summary>
    public string RunString(int runId)
    {
        var sb = new System.Text.StringBuilder();
        int i = runId >> 1, len = (runId & 1) + 2;
        for (int j = i; j < i + len; j++)
        {
            int id = OrderedNorm[j];
            if (id < 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            foreach (var b in NormBytes(id)) sb.Append((char)b);
        }
        return sb.ToString();
    }

    private ulong RunHashOf(int runId, out bool empty)
    {
        ulong h = FnvOffset;
        empty = true;
        int i = runId >> 1, len = (runId & 1) + 2;
        for (int j = i; j < i + len; j++)
        {
            int id = OrderedNorm[j];
            if (id < 0) continue;
            if (!empty) h = (h ^ (byte)' ') * FnvPrime;
            h = Hash(h, NormBytes(id));
            empty = false;
        }
        return h;
    }

    /// <summary>True when run <paramref name="runId"/>'s text equals <paramref name="norm"/>.</summary>
    public bool RunEquals(int runId, string norm)
    {
        int pos = 0;
        bool any = false;
        int i = runId >> 1, len = (runId & 1) + 2;
        for (int j = i; j < i + len; j++)
        {
            int id = OrderedNorm[j];
            if (id < 0) continue;
            if (any)
            {
                if (pos >= norm.Length || norm[pos] != ' ') return false;
                pos++;
            }
            var bytes = NormBytes(id);
            if (pos + bytes.Length > norm.Length) return false;
            for (int k = 0; k < bytes.Length; k++)
                if (norm[pos + k] != bytes[k]) return false;
            pos += bytes.Length;
            any = true;
        }
        return any && pos == norm.Length;
    }

    private bool NormEquals(int id, string norm)
    {
        var bytes = NormBytes(id);
        if (bytes.Length != norm.Length) return false;
        for (int k = 0; k < bytes.Length; k++)
            if (norm[k] != bytes[k]) return false;
        return true;
    }

    /// <summary>The norm id of the single equal to <paramref name="norm"/>, or -1.</summary>
    public int FindSingle(string norm, ulong hash)
    {
        int at = LowerBound(SingleHash, hash);
        for (; at < SingleHash.Length && SingleHash[at] == hash; at++)
            if (NormEquals(SingleHashId[at], norm)) return SingleHashId[at];
        return -1;
    }

    /// <summary>Adds the ids of the runs whose text equals <paramref name="norm"/>, ascending.</summary>
    public void FindRuns(string norm, ulong hash, List<int> into)
    {
        int start = into.Count;
        int at = LowerBound(RunHash, hash);
        for (; at < RunHash.Length && RunHash[at] == hash; at++)
            if (RunEquals(RunHashId[at], norm)) into.Add(RunHashId[at]);
        if (into.Count - start > 1) into.Sort(start, into.Count - start, Comparer<int>.Default);
    }

    private static int LowerBound(ulong[] a, ulong key)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (a[mid] < key) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    // ---- trigrams ----

    /// <summary>Packed trigram of three normalized (7-bit) characters.</summary>
    public static int Key(int a, int b, int c) => ((a & 0x7F) << 14) | ((b & 0x7F) << 7) | (c & 0x7F);

    /// <summary>Postings at least this long are stored as bitsets (where a bitset is no larger than the posting).</summary>
    private static int DenseMin(int normCount) => Math.Max(1024, normCount / 32);

    // ---- build ----

    public static LibraryIndex Build(IReadOnlyList<LibraryLine> released)
    {
        int count = released.Count;
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var norms = new List<string>();
        var lineNorm = new int[count];
        long textLength = 0;
        for (int p = 0; p < count; p++)
        {
            var n = LibraryText.Normalize(released[p].En);
            if (n.Length == 0)
            {
                lineNorm[p] = -1;
                continue;
            }
            if (!ids.TryGetValue(n, out var id))
            {
                ids[n] = id = norms.Count;
                norms.Add(n);
                textLength += n.Length;
            }
            lineNorm[p] = id;
        }
        ids.Clear();
        ids.TrimExcess();

        int normCount = norms.Count;
        var text = new byte[textLength];
        var textStart = new int[normCount + 1];
        for (int id = 0, at = 0; id < normCount; id++)
        {
            textStart[id] = at;
            foreach (var c in norms[id]) text[at++] = (byte)c;
            textStart[id + 1] = at;
        }

        // Norm id -> released positions (counting sort keeps released order).
        var normLineStart = new int[normCount + 1];
        foreach (var id in lineNorm)
            if (id >= 0) normLineStart[id + 1]++;
        for (int id = 0; id < normCount; id++) normLineStart[id + 1] += normLineStart[id];
        var normLines = new int[normLineStart[normCount]];
        var fill = (int[])normLineStart.Clone();
        for (int p = 0; p < count; p++)
            if (lineNorm[p] >= 0) normLines[fill[lineNorm[p]]++] = p;

        // Runs: 2 and 3 consecutive lines (by Order, stable) with the same non-null Video and the same Scene.
        var ordered = Enumerable.Range(0, count).OrderBy(p => released[p].Order).ToArray();
        var runMax = new byte[count];
        for (int i = 0; i < count; i++)
        {
            var first = released[ordered[i]];
            runMax[i] = 1;
            if (first.Video is null) continue;
            for (int len = 2; len <= 3 && i + len <= count; len++)
            {
                var last = released[ordered[i + len - 1]];
                if (last.Video != first.Video || last.Scene != first.Scene) break;
                runMax[i] = (byte)len;
            }
        }

        var singleHash = new ulong[normCount];
        var singleHashId = new int[normCount];
        for (int id = 0; id < normCount; id++)
        {
            singleHash[id] = Hash(norms[id]);
            singleHashId[id] = id;
        }
        norms.Clear();
        Array.Sort(singleHash, singleHashId);

        var (keys, postStart, post, denseOf, dense) = BuildPostings(text, textStart);
        var runVindex = new int[2 * count];
        Array.Fill(runVindex, -1);
        var partial = new LibraryIndex(count, text, textStart, lineNorm, normLineStart, normLines, ordered, runMax,
            runVindex, singleHash, singleHashId, [], [], keys, postStart, post, denseOf, dense);
        var (runHash, runHashId) = partial.NumberRuns(runVindex);
        return new LibraryIndex(count, text, textStart, lineNorm, normLineStart, normLines, ordered, runMax,
            runVindex, singleHash, singleHashId, runHash, runHashId, keys, postStart, post, denseOf, dense);
    }

    /// <summary>Fills <paramref name="vindex"/> (new run texts numbered after the singles, in run order) and returns the run hash table.</summary>
    private (ulong[] RunHash, int[] RunHashId) NumberRuns(int[] vindex)
    {
        var hashes = new List<ulong>();
        var runIds = new List<int>();
        var firstOfHash = new Dictionary<ulong, int>(); // hash -> first run id with a new text
        Dictionary<string, int>? collided = null;       // 64-bit collisions: run text -> vindex
        int next = NormCount;
        for (int i = 0; i < LineCount; i++)
            for (int len = 2; len <= RunMax[i]; len++)
            {
                int runId = 2 * i + len - 2;
                ulong h = RunHashOf(runId, out var empty);
                if (empty) continue;
                hashes.Add(h);
                runIds.Add(runId);
                var text = RunString(runId);
                int single = FindSingle(text, h);
                if (single >= 0) vindex[runId] = single;
                else if (!firstOfHash.TryGetValue(h, out var firstRun))
                {
                    firstOfHash[h] = runId;
                    vindex[runId] = next++;
                }
                else if (RunEquals(firstRun, text)) vindex[runId] = vindex[firstRun];
                else
                {
                    collided ??= new(StringComparer.Ordinal);
                    if (!collided.TryGetValue(text, out var v)) collided[text] = v = next++;
                    vindex[runId] = v;
                }
            }
        var runHash = hashes.ToArray();
        var runHashId = runIds.ToArray();
        Array.Sort(runHash, runHashId);
        return (runHash, runHashId);
    }

    /// <summary>The distinct trigram keys of " " + norm + " " in first-seen order (seen[key] == stamp marks a key as taken).</summary>
    private static void DistinctTrigrams(ReadOnlySpan<byte> norm, int[] seen, int stamp, List<int> into)
    {
        into.Clear();
        int a = ' ', b = norm[0];
        for (int k = 1; k <= norm.Length; k++)
        {
            int c = k < norm.Length ? norm[k] : ' ';
            int key = Key(a, b, c);
            if (seen[key] != stamp)
            {
                seen[key] = stamp;
                into.Add(key);
            }
            (a, b) = (b, c);
        }
    }

    private static (int[] Keys, int[] PostStart, int[] Post, int[] DenseOf, ulong[] Dense) BuildPostings(byte[] text, int[] textStart)
    {
        int normCount = textStart.Length - 1;
        const int KeySpace = 1 << 21;
        var seen = new int[KeySpace];
        var perKey = new int[KeySpace];
        var tris = new List<int>();
        for (int id = 0; id < normCount; id++)
        {
            DistinctTrigrams(text.AsSpan(textStart[id], textStart[id + 1] - textStart[id]), seen, id + 1, tris);
            foreach (var key in tris) perKey[key]++;
        }

        int denseMin = DenseMin(normCount), words = (normCount + 63) >> 6;
        int present = 0, denseCount = 0;
        long total = 0;
        for (int key = 0; key < KeySpace; key++)
            if (perKey[key] >= denseMin)
            {
                present++;
                denseCount++;
            }
            else if (perKey[key] > 0)
            {
                present++;
                total += perKey[key];
            }
        var keys = new int[present];
        var postStart = new int[present + 1];
        var denseOf = new int[present];
        var dense = new ulong[(long)denseCount * words];
        var slotOf = seen; // reused: key -> slot
        for (int key = 0, s = 0, d = 0, at = 0; key < KeySpace; key++)
        {
            if (perKey[key] == 0) continue;
            keys[s] = key;
            postStart[s] = at;
            slotOf[key] = s;
            if (perKey[key] >= denseMin) denseOf[s] = d++;
            else
            {
                denseOf[s] = -1;
                at += perKey[key];
            }
            postStart[++s] = at;
        }
        var post = new int[total];
        var fill = (int[])postStart.Clone();
        var mark = perKey; // reused as the per-id stamp
        Array.Clear(mark);
        for (int id = 0; id < normCount; id++)
        {
            DistinctTrigrams(text.AsSpan(textStart[id], textStart[id + 1] - textStart[id]), mark, id + 1, tris);
            foreach (var key in tris)
            {
                int s = slotOf[key];
                if (denseOf[s] >= 0) dense[(long)denseOf[s] * words + (id >> 6)] |= 1UL << id;
                else post[fill[s]++] = id;
            }
        }
        return (keys, postStart, post, denseOf, dense);
    }

    // ---- cache file ----

    /// <summary>Bump when the stored arrays, their meaning, normalization or the loader's line order change.</summary>
    internal const int FormatVersion = 1;

    private static ReadOnlySpan<byte> Magic => "PSRSIDX1"u8;

    /// <summary>
    /// The index for <paramref name="released"/>: loaded from <paramref name="path"/> when that cache was written for
    /// the same <paramref name="key"/> (the sha256 of subtitles.jsonl), format and lines; otherwise built and written there
    /// (atomically: a temp file, then a rename). Never throws on a missing, stale, corrupt or unwritable cache.
    /// </summary>
    public static LibraryIndex LoadOrBuild(string path, string key, IReadOnlyList<LibraryLine> released)
    {
        var watch = Stopwatch.StartNew();
        ulong fingerprint = Fingerprint(released);
        var loaded = TryLoad(path, key, released.Count, fingerprint);
        if (loaded is not null)
        {
            Log.Info($"Library index: {released.Count} lines loaded from the cache in {watch.ElapsedMilliseconds} ms");
            return loaded;
        }
        var built = Build(released);
        long buildMs = watch.ElapsedMilliseconds;
        try
        {
            built.Save(path, key, fingerprint);
        }
        catch (Exception ex)
        {
            Log.Warn("Library index: cache not written", ex);
        }
        Log.Info($"Library index: {released.Count} lines built in {buildMs} ms, cache written in {watch.ElapsedMilliseconds - buildMs} ms");
        return built;
    }

    /// <summary>The cached index, or null when the file is missing, stale, written for other lines, or corrupt.</summary>
    public static LibraryIndex? TryLoad(string path, string key, IReadOnlyList<LibraryLine> released) =>
        TryLoad(path, key, released.Count, Fingerprint(released));

    private static LibraryIndex? TryLoad(string path, string key, int lineCount, ulong fingerprint)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var reader = new BinaryReader(file);
            Span<byte> magic = stackalloc byte[8];
            file.ReadExactly(magic);
            if (!magic.SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion || reader.ReadString() != key
                || reader.ReadInt32() != lineCount || reader.ReadUInt64() != fingerprint)
                return null;
            ulong checksum = reader.ReadUInt64();
            var sum = new Checksum();
            T[] Next<T>() where T : unmanaged
            {
                int length = reader.ReadInt32();
                if (length < 0 || (long)length * Unsafe.SizeOf<T>() > file.Length - file.Position)
                    throw new InvalidDataException("Array past the end of the file.");
                var array = new T[length];
                var bytes = MemoryMarshal.AsBytes(array.AsSpan());
                file.ReadExactly(bytes);
                sum.Add(bytes);
                return array;
            }
            var text = Next<byte>();
            var textStart = Next<int>();
            var lineNorm = Next<int>();
            var normLineStart = Next<int>();
            var normLines = Next<int>();
            var ordered = Next<int>();
            var runMax = Next<byte>();
            var runVindex = Next<int>();
            var singleHash = Next<ulong>();
            var singleHashId = Next<int>();
            var runHash = Next<ulong>();
            var runHashId = Next<int>();
            var keys = Next<int>();
            var postStart = Next<int>();
            var post = Next<int>();
            var denseOf = Next<int>();
            var dense = Next<ulong>();
            if (sum.Value != checksum || file.Position != file.Length) return null;

            int normCount = textStart.Length - 1;
            bool shaped = normCount >= 0 && textStart[0] == 0 && textStart[^1] == text.Length
                && lineNorm.Length == lineCount && ordered.Length == lineCount && runMax.Length == lineCount
                && runVindex.Length == 2 * lineCount && normLineStart.Length == normCount + 1
                && normLines.Length == normLineStart[^1] && singleHash.Length == normCount
                && singleHashId.Length == normCount && runHash.Length == runHashId.Length
                && postStart.Length == keys.Length + 1 && denseOf.Length == keys.Length && postStart[^1] == post.Length
                && dense.Length == denseOf.Count(d => d >= 0) * (long)((normCount + 63) >> 6);
            if (!shaped) return null;
            return new LibraryIndex(lineCount, text, textStart, lineNorm, normLineStart, normLines, ordered, runMax,
                runVindex, singleHash, singleHashId, runHash, runHashId, keys, postStart, post, denseOf, dense);
        }
        catch (Exception ex)
        {
            Log.Warn("Library index: cache unreadable, rebuilding", ex);
            return null;
        }
    }

    /// <summary>Writes the index to <paramref name="path"/> through a temp file and a rename. Throws on IO errors.</summary>
    private void Save(string path, string key, ulong fingerprint)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var sum = new Checksum();
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            using (var writer = new BinaryWriter(file))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(key);
                writer.Write(LineCount);
                writer.Write(fingerprint);
                long checksumAt = file.Position;
                writer.Write(0UL);
                void Put<T>(T[] array) where T : unmanaged
                {
                    var bytes = MemoryMarshal.AsBytes(array.AsSpan());
                    writer.Write(array.Length);
                    writer.Write(bytes);
                    sum.Add(bytes);
                }
                Put(Text);
                Put(TextStart);
                Put(LineNorm);
                Put(NormLineStart);
                Put(NormLines);
                Put(Ordered);
                Put(RunMax);
                Put(RunVindex);
                Put(SingleHash);
                Put(SingleHashId);
                Put(RunHash);
                Put(RunHashId);
                Put(Keys);
                Put(PostStart);
                Put(Post);
                Put(DenseOf);
                Put(Dense);
                writer.Flush();
                file.Position = checksumAt;
                writer.Write(sum.Value);
                writer.Flush();
                file.Flush(true);
            }
            File.Move(temp, full, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// What the index depends on in the released lines (their count, English text, Order, Video and Scene), hashed: a
    /// cache written for other lines under the same key is not used.
    /// </summary>
    internal static ulong Fingerprint(IReadOnlyList<LibraryLine> released)
    {
        static ulong Mix(ulong h, string? s)
        {
            if (s is null) return (h ^ 0xFFFF) * FnvPrime;
            foreach (var c in s) h = (h ^ c) * FnvPrime;
            return (h ^ 0xFFFE) * FnvPrime;
        }
        ulong h = FnvOffset;
        foreach (var line in released)
        {
            h = (h ^ (uint)line.Order) * FnvPrime;
            h = Mix(Mix(Mix(h, line.En), line.Video), line.Scene);
        }
        return (h ^ (ulong)released.Count) * FnvPrime;
    }

    /// <summary>A fast 64-bit checksum of the stored arrays (8 bytes at a time), against a damaged cache file.</summary>
    private sealed class Checksum
    {
        public ulong Value { get; private set; } = FnvOffset;

        public void Add(ReadOnlySpan<byte> bytes)
        {
            ulong h = Value;
            var words = MemoryMarshal.Cast<byte, ulong>(bytes);
            foreach (var word in words) h = BitOperations.RotateLeft((h ^ word) * FnvPrime, 29);
            for (int i = words.Length * 8; i < bytes.Length; i++) h = (h ^ bytes[i]) * FnvPrime;
            Value = (h ^ (ulong)bytes.Length) * FnvPrime;
        }
    }
}
