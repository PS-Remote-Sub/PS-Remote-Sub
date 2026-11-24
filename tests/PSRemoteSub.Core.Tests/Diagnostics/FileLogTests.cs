using System.Text.RegularExpressions;
using PSRemoteSub.Core.Diagnostics;

namespace PSRemoteSub.Core.Tests.Diagnostics;

public sealed class FileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PSRemoteSubLogTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Write_CreatesFolderAndFile_OneFormattedLinePerEntry()
    {
        var log = new FileLog(Path.Combine(_dir, "sub", "log.txt"));

        log.Write("INFO", "started");
        log.Write("ERROR", "two\r\nlines\nhere");

        var lines = File.ReadAllLines(log.Path);
        Assert.Equal(2, lines.Length);
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[INFO\] started$"), lines[0]);
        Assert.EndsWith("[ERROR] two lines here", lines[1]);
    }

    [Fact]
    public void Write_RollsToDotOne_WhenFull()
    {
        var log = new FileLog(Path.Combine(_dir, "log.txt"), maxBytes: 200);
        Assert.Equal(Path.Combine(_dir, "log.1.txt"), log.RolledPath);

        for (var i = 0; i < 10; i++)
            log.Write("INFO", $"entry {i} " + new string('x', 20));

        Assert.True(File.Exists(log.RolledPath));
        Assert.True(new FileInfo(log.Path).Length <= 200);
        Assert.True(new FileInfo(log.RolledPath).Length <= 200);
        Assert.EndsWith("entry 9 " + new string('x', 20), File.ReadAllLines(log.Path)[^1]);
    }

    [Fact]
    public void Write_NeverThrows_WhenPathIsUnusable()
    {
        Directory.CreateDirectory(_dir);
        var log = new FileLog(_dir); // a folder, not a file

        log.Write("INFO", "ignored");
    }

    [Fact]
    public async Task Write_IsThreadSafe()
    {
        var log = new FileLog(Path.Combine(_dir, "log.txt"));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
                log.Write("INFO", $"t{t} i{i}");
        })));

        var lines = File.ReadAllLines(log.Path);
        Assert.Equal(400, lines.Length);
        Assert.All(lines, l => Assert.Matches(@"\[INFO\] t\d i\d+$", l));
    }

    [Fact]
    public void Throttle_LogsFirstAndEveryHundredth_UntilReset()
    {
        var throttle = new LogThrottle();
        var logged = Enumerable.Range(0, 250).Where(_ => throttle.ShouldLog(out _)).Count();
        Assert.Equal(3, logged); // 1st, 100th, 200th

        throttle.Reset();
        Assert.True(throttle.ShouldLog(out var count));
        Assert.Equal(1, count);
    }
}
