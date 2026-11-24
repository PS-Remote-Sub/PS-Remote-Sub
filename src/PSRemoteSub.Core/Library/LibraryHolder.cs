namespace PSRemoteSub.Core.Library;

/// <summary>
/// The library the pipeline and the AI lane read on every line. <see cref="Current"/> is a plain volatile read (no
/// lock), so it is safe on the tick and under the AI lane's lock. Null while no library is loaded for the game.
/// </summary>
public sealed class LibraryHolder
{
    private volatile ActiveLibrary? _current;

    public ActiveLibrary? Current => _current;

    /// <summary>Libraries are English → Thai: they apply only to that language pair.</summary>
    public static bool AppliesTo(string? sourceCode, string? targetCode) =>
        sourceCode == "en" && targetCode == "th";

    /// <summary>Raised (on the caller's thread) after <see cref="Set"/> replaced or dropped the library.</summary>
    public event Action? Changed;

    public void Set(ActiveLibrary? lib)
    {
        _current = lib;
        Changed?.Invoke();
    }
}
