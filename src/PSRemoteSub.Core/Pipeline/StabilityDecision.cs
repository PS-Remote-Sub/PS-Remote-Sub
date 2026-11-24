namespace PSRemoteSub.Core.Pipeline;

public abstract record StabilityDecision
{
    public static readonly StabilityDecision None = new NoneDecision();

    private StabilityDecision() { }

    public sealed record Stable(string Text) : StabilityDecision;

    public sealed record Cleared : StabilityDecision;

    private sealed record NoneDecision : StabilityDecision;
}
