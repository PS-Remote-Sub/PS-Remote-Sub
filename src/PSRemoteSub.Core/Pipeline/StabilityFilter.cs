using PSRemoteSub.Core.Text;

namespace PSRemoteSub.Core.Pipeline;

public sealed class StabilityFilter(StabilityOptions options)
{
    private readonly TimeSpan _clearAfter = options.ClearAfter ?? TimeSpan.FromMilliseconds(1500);
    private string? _candidate;
    private int _count;
    private string? _lastEmitted;
    private DateTimeOffset? _emptySince;

    public StabilityDecision Feed(string? text, DateTimeOffset now)
    {
        if (text is null)
        {
            _candidate = null;
            _count = 0;
            _emptySince ??= now;
            if (_lastEmitted != null && now - _emptySince.Value >= _clearAfter)
            {
                _lastEmitted = null;
                return new StabilityDecision.Cleared();
            }
            return StabilityDecision.None;
        }

        _emptySince = null;

        if (_candidate != null && text.Length > _candidate.Length
            && text.StartsWith(_candidate, StringComparison.OrdinalIgnoreCase))
        {
            _candidate = text;
            _count = 1;
            return StabilityDecision.None;
        }

        if (_candidate != null && Similarity.Ratio(text, _candidate) >= options.SameThreshold)
        {
            _count++;
        }
        else
        {
            _candidate = text;
            _count = 1;
        }

        if (_count == options.RequiredReads)
        {
            if (_lastEmitted != null && Similarity.Ratio(text, _lastEmitted) >= options.DuplicateThreshold)
                return StabilityDecision.None;
            _lastEmitted = text;
            return new StabilityDecision.Stable(text);
        }
        return StabilityDecision.None;
    }

    public void Reset()
    {
        _candidate = null;
        _count = 0;
        _lastEmitted = null;
        _emptySince = null;
    }
}
