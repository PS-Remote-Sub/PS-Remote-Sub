using System.Diagnostics;
using PSRemoteSub.Windows.Capture;

namespace PSRemoteSub.Windows.Tests;

[Collection(CaptureCardCollection.Name)]
public class CaptureAudioTests
{
    private const string CardId = "VID_345F&PID_2130";

    [Fact]
    public void Volume_ClampsToUnitRange()
    {
        using var audio = new CaptureAudio();
        audio.Volume = 2;
        Assert.Equal(1, audio.Volume);
        audio.Volume = -1;
        Assert.Equal(0, audio.Volume);
    }

    [CaptureCardFact]
    public async Task Start_TestCardAudio_Runs()
    {
        var devices = await CaptureDeviceCatalog.ListAsync();
        string audioId = devices.First(d => d.Id.Contains(CardId, StringComparison.OrdinalIgnoreCase)).AudioDeviceId
            ?? throw new InvalidOperationException("The test card has no paired audio device.");

        using var audio = new CaptureAudio();
        audio.Start(audioId);
        Assert.True(WaitUntil(() => audio.IsRunning, TimeSpan.FromSeconds(3)), "Audio did not start within 3 s.");
        audio.Stop();
        Assert.False(audio.IsRunning);
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (condition())
                return true;
            Thread.Sleep(20);
        }
        return condition();
    }
}
