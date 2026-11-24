using PSRemoteSub.Core.Pipeline;
using PSRemoteSub.Core.Translation.Ai;

namespace PSRemoteSub.Core.Tests.Pipeline.Fakes;

/// <summary>
/// Records every request. Each call takes the next scripted step (a result or an exception, optionally held behind
/// its own gate); once the script runs out it returns <see cref="Map"/> after waiting for <see cref="Gate"/>, if set.
/// </summary>
public sealed class FakeAiTranslator : IAiTranslator
{
    private readonly Lock _gate = new();
    private readonly Queue<Step> _script = new();
    private readonly List<AiTranslationRequest> _requests = [];
    private readonly List<(int Count, TaskCompletionSource Tcs)> _waiters = [];

    public TaskCompletionSource? Gate { get; set; }
    public Func<AiTranslationRequest, string> Map { get; set; } = r => "ai:" + r.Text;

    public IReadOnlyList<AiTranslationRequest> Requests { get { lock (_gate) return [.. _requests]; } }

    public IReadOnlyList<string> Texts => Requests.Select(r => r.Text).ToList();

    public static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeAiTranslator Returns(string result, TaskCompletionSource? gate = null)
    {
        lock (_gate)
            _script.Enqueue(new Step(gate, () => result));
        return this;
    }

    public FakeAiTranslator Throws(Exception ex, TaskCompletionSource? gate = null)
    {
        lock (_gate)
            _script.Enqueue(new Step(gate, () => throw ex));
        return this;
    }

    /// <summary>Completes once <see cref="TranslateAsync"/> has been entered <paramref name="count"/> times.</summary>
    public Task WaitForCallsAsync(int count)
    {
        lock (_gate)
        {
            if (_requests.Count >= count)
                return Task.CompletedTask;
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, tcs));
            return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public async Task<string> TranslateAsync(AiTranslationRequest request, CancellationToken ct)
    {
        Step? step;
        TaskCompletionSource? gate;
        List<TaskCompletionSource> done;
        lock (_gate)
        {
            _requests.Add(request);
            _script.TryDequeue(out step);
            gate = step is null ? Gate : step.Gate;
            done = _waiters.Where(w => w.Count <= _requests.Count).Select(w => w.Tcs).ToList();
            _waiters.RemoveAll(w => w.Count <= _requests.Count);
        }
        foreach (var tcs in done)
            tcs.TrySetResult();
        if (gate is not null)
            await gate.Task.WaitAsync(ct);
        return step is null ? Map(request) : step.Result();
    }

    private sealed record Step(TaskCompletionSource? Gate, Func<string> Result);
}

/// <summary>An <see cref="IAiTranslatorSource"/> whose handle the test sets directly.</summary>
public sealed class FakeAiTranslatorSource : IAiTranslatorSource
{
    private AiTranslatorHandle _current;

    public FakeAiTranslatorSource(AiTranslatorHandle current) => _current = current;

    public AiTranslatorHandle Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
