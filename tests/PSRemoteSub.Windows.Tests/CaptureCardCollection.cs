namespace PSRemoteSub.Windows.Tests;

/// <summary>Tests that open the physical capture card run one at a time, never alongside each other.</summary>
[CollectionDefinition(Name)]
public sealed class CaptureCardCollection
{
    public const string Name = "Capture card";
}
