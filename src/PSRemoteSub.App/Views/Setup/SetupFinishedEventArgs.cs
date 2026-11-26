namespace PSRemoteSub.App.Views.Setup;

/// <summary>
/// How the setup wizard ended. <see cref="Completed"/>: "เสร็จแล้ว" or "ข้ามไปก่อน" (setup is marked done and saved);
/// false: the window was closed with X (not marked done, shown again next launch). <see cref="NextPage"/>: the main
/// window page to open after a completed wizard — <see cref="MainPage.Translation"/> when AI translation is chosen but
/// its provider is not ready (no key), so the user can finish it there; <see cref="MainPage.Home"/> otherwise.
/// </summary>
public sealed class SetupFinishedEventArgs(bool completed, MainPage nextPage) : EventArgs
{
    public bool Completed { get; } = completed;

    public MainPage NextPage { get; } = nextPage;
}
