using PSRemoteSub.Core.Imaging;
using PSRemoteSub.Core.Platform;

namespace PSRemoteSub.Core.Tests.Pipeline.Fakes;

/// <summary>Returns scripted results in order; once the script runs out it returns no lines.</summary>
public sealed class ScriptedOcr : IOcrEngine
{
    private readonly Queue<Func<IReadOnlyList<OcrLine>>> _script = new();
    private readonly Lock _gate = new();
    private readonly List<(int Count, TaskCompletionSource Tcs)> _waiters = [];
    private int _calls;

    public int MaxImageDimension => 10_000;
    public int Calls { get { lock (_gate) return _calls; } }
    public string? LastTag { get; private set; }
    public Frame? LastImage { get; private set; }

    /// <summary>What <see cref="IsAvailable"/> reports (whether the language pack is installed).</summary>
    public bool Available { get; set; } = true;

    public bool IsAvailable(string ocrTag) => Available;

    public ScriptedOcr Returns(params string[] texts)
    {
        lock (_gate)
            foreach (var t in texts)
                _script.Enqueue(() => t.Length == 0 ? [] : [new OcrLine(t, new PixelRect(0, 0, 10, 10))]);
        return this;
    }

    /// <summary>One read of <paramref name="text"/> split over lines at <paramref name="lineBounds"/> (OCR-image pixels).</summary>
    public ScriptedOcr ReturnsAt(string text, params PixelRect[] lineBounds)
    {
        lock (_gate)
            _script.Enqueue(() => [.. lineBounds.Select((b, i) => new OcrLine(i == 0 ? text : "", b))]);
        return this;
    }

    /// <summary>One read with one OCR line per text, top to bottom (no word boxes).</summary>
    public ScriptedOcr ReturnsLines(params string[] lineTexts)
    {
        lock (_gate)
            _script.Enqueue(() => [.. lineTexts.Select((t, i) => new OcrLine(t, new PixelRect(0, i * 12, 40, 10)))]);
        return this;
    }

    /// <summary>One read returning exactly <paramref name="lines"/>.</summary>
    public ScriptedOcr ReturnsRaw(params OcrLine[] lines)
    {
        lock (_gate)
            _script.Enqueue(() => lines);
        return this;
    }

    public ScriptedOcr Throws(Exception ex)
    {
        lock (_gate)
            _script.Enqueue(() => throw ex);
        return this;
    }

    /// <summary>Completes once <see cref="RecognizeAsync"/> has been entered <paramref name="count"/> times.</summary>
    public Task WaitForCallsAsync(int count)
    {
        lock (_gate)
        {
            if (_calls >= count)
                return Task.CompletedTask;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, tcs));
            return tcs.Task;
        }
    }

    public Task<IReadOnlyList<OcrLine>> RecognizeAsync(Frame image, string ocrTag, CancellationToken ct)
    {
        Func<IReadOnlyList<OcrLine>>? next;
        List<TaskCompletionSource> done;
        lock (_gate)
        {
            _calls++;
            LastTag = ocrTag;
            LastImage = image;
            _script.TryDequeue(out next);
            done = _waiters.Where(w => w.Count <= _calls).Select(w => w.Tcs).ToList();
            _waiters.RemoveAll(w => w.Count <= _calls);
        }
        foreach (var tcs in done)
            tcs.TrySetResult();
        return Task.FromResult(next is null ? (IReadOnlyList<OcrLine>)[] : next());
    }
}
