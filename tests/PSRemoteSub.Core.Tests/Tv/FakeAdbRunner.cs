using PSRemoteSub.Core.Tv;

namespace PSRemoteSub.Core.Tests.Tv;

/// <summary>
/// Scripted runner for adb tests. Every call must start with the private server port (<c>-P 5038</c>);
/// <see cref="Calls"/>, <see cref="Timeouts"/> and <see cref="Handler"/> get the adb arguments after it (without the exe).
/// </summary>
internal sealed class FakeAdbRunner : IProcessRunner
{
    public List<string[]> Calls { get; } = [];
    public List<TimeSpan> Timeouts { get; } = [];
    public Func<string[], ProcessResult> Handler { get; set; } = _ => new ProcessResult(0, "", "");

    public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        Assert.Equal("adb.exe", exe);
        Assert.Equal(["-P", "5038"], args.Take(2));
        var a = args.Skip(2).ToArray();
        Calls.Add(a);
        Timeouts.Add(timeout);
        return Task.FromResult(Handler(a));
    }

    public bool Called(string verb) => Calls.Any(c => c.Contains(verb));
    public int Count(string verb) => Calls.Count(c => c.Contains(verb));
}
