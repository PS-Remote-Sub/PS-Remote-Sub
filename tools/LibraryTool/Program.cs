using System.Globalization;
using System.Text;
using PSRemoteSub.Core.Library;
using PSRemoteSub.Core.Library.Build;

const string Usage = """
    usage:
      LibraryTool append <game>
          library/<game>/pending.jsonl ({"en","th","speaker"?}) -> subtitles.jsonl (rows without "th" are skipped)
      LibraryTool pack <version YYYYMMDD.N> <outDir>
    scene library build (work/<game>, output library/<game>):
      LibraryTool import <game> <dialogue_by_scene.csv>
      LibraryTool show <game> <chunk>
      LibraryTool submit <game> <chunk>
      LibraryTool check <game>
      LibraryTool assemble <game> <version YYYYMMDD.N>
    """;

Console.OutputEncoding = new UTF8Encoding(false);
try
{
    return Run(args);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static int Fail(string msg)
{
    Console.Error.WriteLine(msg);
    Console.Error.WriteLine(Usage);
    return 1;
}

static string? FindRepoRoot()
{
    for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "PSRemoteSub.sln")))
            return d.FullName;
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "PSRemoteSub.sln")))
            return d.FullName;
    return null;
}

static bool TryChunk(string s, out int n) =>
    int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 0;

static int Run(string[] args)
{
    if (args.Length == 0) return Fail("missing command");
    var repo = FindRepoRoot();
    if (repo is null) return Fail("PSRemoteSub.sln not found above the current directory");
    var root = Path.Combine(repo, "library");
    string Work(string game) => Path.Combine(repo, "work", game);
    string Next(string game) => Path.Combine(root, game);

    switch (args[0])
    {
        case "append" when args.Length == 2:
        {
            var dir = Path.Combine(root, args[1]);
            var pending = Path.Combine(dir, "pending.jsonl");
            if (!File.Exists(pending)) return Fail($"{pending} not found");
            var n = LibraryPack.Append(dir, pending);
            File.Delete(pending);
            Console.WriteLine($"appended {n}");
            return 0;
        }
        case "pack" when args.Length == 3:
        {
            if (!LibraryVersion.TryParse(args[1], out var version)) return Fail($"bad version '{args[1]}'");
            if (!Directory.Exists(root)) return Fail("library folder not found");
            var packed = 0;
            foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (!File.Exists(Path.Combine(dir, LibraryFiles.Manifest))) continue;
                Console.WriteLine(LibraryPack.Pack(dir, Path.GetFileName(dir), version, args[2]));
                packed++;
            }
            if (packed == 0) return Fail("no library with manifest.json found");
            return 0;
        }
        case "import" when args.Length == 3:
        {
            var r = BuildWork.Import(args[2], Work(args[1]));
            Console.WriteLine($"rows {r.Rows}, segments {r.Segments}, chunks {r.Chunks}");
            return 0;
        }
        case "show" when args.Length == 3:
        {
            if (!TryChunk(args[2], out var n) || n > BuildWork.ChunkCount(Work(args[1]))) return Fail($"no chunk '{args[2]}'");
            Console.Write(BuildWork.Show(Work(args[1]), Next(args[1]), n));
            return 0;
        }
        case "submit" when args.Length == 3:
        {
            if (!TryChunk(args[2], out var n) || n > BuildWork.ChunkCount(Work(args[1]))) return Fail($"no chunk '{args[2]}'");
            var r = BuildWork.Submit(Work(args[1]), n);
            if (r.Ok)
            {
                Console.WriteLine($"OK {n:D4} ({r.Rows.Count} lines)");
                return 0;
            }
            Console.WriteLine($"FAIL {n:D4} — fix these lines in the draft and submit again:");
            foreach (var p in r.Problems) Console.WriteLine("  " + p);
            return 2;
        }
        case "check" when args.Length == 2:
        {
            var s = BuildWork.CheckAll(Work(args[1]));
            Console.WriteLine($"passed {s.Passed.Count}/{s.Total}, failed {s.Failed.Count}, missing {s.Missing.Count}");
            return 0;
        }
        case "assemble" when args.Length == 3:
        {
            if (!LibraryVersion.TryParse(args[2], out var version)) return Fail($"bad version '{args[2]}'");
            var n = BuildWork.Assemble(Work(args[1]), Next(args[1]), version);
            Console.WriteLine($"assembled {n} lines -> {Next(args[1])}");
            return 0;
        }
        default:
            return Fail("bad arguments");
    }
}
