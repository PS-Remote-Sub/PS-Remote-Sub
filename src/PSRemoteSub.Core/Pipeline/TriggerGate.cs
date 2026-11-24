namespace PSRemoteSub.Core.Pipeline;

public enum TriggerChange { None, On, Off }

/// <summary>Switches a zone on at the first trigger match and off after a few misses in a row (hysteresis).</summary>
public sealed class TriggerGate
{
    public const int MissesToTurnOff = 2;

    private int _misses;

    public bool Active { get; private set; }

    public TriggerChange Feed(bool matched)
    {
        if (matched)
        {
            _misses = 0;
            if (Active) return TriggerChange.None;
            Active = true;
            return TriggerChange.On;
        }

        if (!Active) return TriggerChange.None;
        if (++_misses < MissesToTurnOff) return TriggerChange.None;
        Reset();
        return TriggerChange.Off;
    }

    public void Reset()
    {
        Active = false;
        _misses = 0;
    }
}
