using PSRemoteSub.Core.Capture;
using PSRemoteSub.MacOS.Capture;

namespace PSRemoteSub.MacOS.Tests;

// None of these opens a real device or asks for camera/microphone access: an unbundled test process touching the camera
// or the microphone is killed by TCC. Unknown device ids are rejected before any authorization check.
public class AvCaptureCardTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    // Device discovery only: never opens a device, so no camera/microphone authorization is requested.
    [MacFact]
    public async Task ListDevicesAsync_NoThrow_ReturnsList()
    {
        using var card = new AvCaptureCard();
        var devices = await card.ListDevicesAsync();
        Assert.NotNull(devices);
        Assert.All(devices, d =>
        {
            Assert.False(string.IsNullOrEmpty(d.Id));
            Assert.NotNull(d.Name);
        });
    }

    [MacFact]
    public void ListModes_UnknownDevice_ReturnsEmpty()
    {
        using var card = new AvCaptureCard();
        Assert.Empty(card.ListModes("no-such-device"));
    }

    [MacFact]
    public void Open_UnknownDevice_StateUnplugged()
    {
        using var card = new AvCaptureCard();
        var states = new List<CaptureState>();
        using var unplugged = new ManualResetEventSlim();
        card.StateChanged += (_, s) =>
        {
            lock (states)
                states.Add(s);
            if (s == CaptureState.Unplugged)
                unplugged.Set();
        };

        card.Open("no-such-device", null);

        Assert.True(unplugged.Wait(Wait), "never reported Unplugged");
        Assert.Equal(CaptureState.Unplugged, card.State);
        Assert.Null(card.CurrentMode);
        lock (states)
            Assert.Equal([CaptureState.Opening, CaptureState.Unplugged], states);
        card.Close();
        Assert.Equal(CaptureState.NoDevice, card.State);
    }

    [MacFact]
    public void Close_Twice_NoThrow()
    {
        using var card = new AvCaptureCard();
        card.Close();
        card.Close();
        card.Open("no-such-device", null);
        card.Close();
        card.Close();
        Assert.Equal(CaptureState.NoDevice, card.State);
        card.Dispose();
        card.Close(); // after Dispose too
    }

    [MacFact]
    public void CreateAudio_StartUnknownDevice_IsRunningFalse_NoThrow()
    {
        using var card = new AvCaptureCard();
        using var audio = card.CreateAudio();
        bool raised = false;
        audio.RunningChanged += (_, _) => raised = true;
        audio.Volume = 0.5;
        audio.Muted = true;

        audio.Start("no-such-device");
        Thread.Sleep(500); // the native side looks the device up asynchronously

        Assert.False(audio.IsRunning);
        Assert.False(raised);
        Assert.Equal(0.5, audio.Volume);
        Assert.True(audio.Muted);
        audio.Stop();
        audio.Stop();
        Assert.False(audio.IsRunning);
    }

    [MacFact]
    public void Audio_Volume_Clamped()
    {
        using var card = new AvCaptureCard();
        using var audio = card.CreateAudio();
        audio.Volume = 2;
        Assert.Equal(1, audio.Volume);
        audio.Volume = -1;
        Assert.Equal(0, audio.Volume);
        audio.Volume = double.NaN;
        Assert.Equal(0, audio.Volume);
    }

    [MacFact]
    public void Frames_NothingOpen_NoFrame()
    {
        using var card = new AvCaptureCard();
        card.Frames.Start(new(0, 0, default, false));
        Assert.Null(card.Frames.Latest);
        Assert.Null(card.Frames.LastFrameAt);
        Assert.Null(card.LastFrameAt);
        card.Frames.Stop();
    }

    [MacFact]
    public void Open_AfterDispose_Throws()
    {
        var card = new AvCaptureCard();
        card.Dispose();
        card.Dispose();
        Assert.Throws<ObjectDisposedException>(() => card.Open("no-such-device", null));
    }

    [Fact]
    public void PictureRect_Letterboxed_UsedAsIs()
    {
        var rect = AvCaptureCard.PictureRect(10.2, 20.5, 100.3, 50.1, letterboxed: true, new CaptureMode(1920, 1080, 60, CaptureSubtype.Nv12));
        Assert.Equal(new Core.Imaging.PixelRect(10, 20, 101, 51), rect);
    }

    [Fact]
    public void PictureRect_NotLetterboxed_FitsTheMode()
    {
        // A 4:3 view holding a 16:9 picture: bars above and below.
        var rect = AvCaptureCard.PictureRect(0, 100, 800, 600, letterboxed: false, new CaptureMode(1920, 1080, 60, CaptureSubtype.Nv12));
        Assert.Equal(new Core.Imaging.PixelRect(0, 175, 800, 450), rect);
    }

    [Fact]
    public void PictureRect_NoMode_WholeView()
    {
        var rect = AvCaptureCard.PictureRect(-50, 0, 640, 480, letterboxed: false, null);
        Assert.Equal(new Core.Imaging.PixelRect(-50, 0, 640, 480), rect);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, VideoSurfaceKey.ToggleFullScreen)]
    [InlineData(2, VideoSurfaceKey.LeaveFullScreen)]
    [InlineData(9, null)]
    public void Surface_MapKey(int nativeKind, VideoSurfaceKey? expected) =>
        Assert.Equal(expected, AvVideoSurface.MapKey(nativeKind));
}
