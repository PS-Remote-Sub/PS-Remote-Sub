using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.Core.Tests.Settings;

public sealed class CaptureSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "psrs-" + Guid.NewGuid().ToString("N"));
    private string PathOf => Path.Combine(_dir, "settings.json");

    public CaptureSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Defaults_RemotePlayAndFullVolume()
    {
        var s = new AppSettings();
        Assert.Equal(PictureSource.RemotePlay, s.PictureSource);
        Assert.Equal(1.0, s.Capture.Volume);
        Assert.Null(s.Capture.Mode);
        Assert.Null(s.Capture.DeviceId);
    }

    [Fact]
    public void RoundTrip_KeepsCaptureSettings()
    {
        var store = new SettingsStore(PathOf);
        var s = new AppSettings
        {
            PictureSource = PictureSource.CaptureCard,
            Capture = new CaptureSettings
            {
                DeviceId = "dev",
                Mode = new CaptureModeSetting(2560, 1440, 50),
                Volume = 0.4,
                Muted = true,
                Window = new WindowPlacement(10, 20, 1280, 720),
                FullScreen = true,
            },
        };
        store.Save(s);

        Assert.Contains("\"CaptureCard\"", File.ReadAllText(PathOf));

        var r = store.Load();
        Assert.Equal(PictureSource.CaptureCard, r.PictureSource);
        Assert.Equal("dev", r.Capture.DeviceId);
        Assert.Equal(new CaptureModeSetting(2560, 1440, 50), r.Capture.Mode);
        Assert.Equal(0.4, r.Capture.Volume);
        Assert.True(r.Capture.Muted);
        Assert.Equal(new WindowPlacement(10, 20, 1280, 720), r.Capture.Window);
        Assert.True(r.Capture.FullScreen);
    }

    [Fact]
    public void OldFile_WithoutCapture_GetsDefaults()
    {
        File.WriteAllText(PathOf, "{\"Profiles\":[],\"SettingsVersion\":2}");
        var s = new SettingsStore(PathOf).Load();
        Assert.NotNull(s.Capture);
        Assert.Equal(PictureSource.RemotePlay, s.PictureSource);
        Assert.Equal(1.0, s.Capture.Volume);
    }

    [Theory]
    [InlineData(3.0, 1.0)]
    [InlineData(-1.0, 0.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(0.4, 0.4)]
    public void Normalize_ClampsVolume(double input, double expected)
    {
        var s = new AppSettings();
        s.Capture.Volume = input;
        s.Normalize();
        Assert.Equal(expected, s.Capture.Volume);
    }

    [Fact]
    public void Normalize_NullCapture_BecomesDefault()
    {
        File.WriteAllText(PathOf, "{\"capture\":null}");
        var s = new SettingsStore(PathOf).Load();
        Assert.NotNull(s.Capture);
        Assert.Equal(1.0, s.Capture.Volume);
    }

    [Theory]
    [InlineData(0, 1080, 60.0)]
    [InlineData(1920, 0, 60.0)]
    [InlineData(-1, 1080, 60.0)]
    [InlineData(1920, 1080, 0.0)]
    [InlineData(1920, 1080, -5.0)]
    [InlineData(1920, 1080, double.NaN)]
    [InlineData(1920, 1080, double.PositiveInfinity)]
    public void Normalize_InvalidMode_BecomesNull(int w, int h, double fps)
    {
        var s = new AppSettings();
        s.Capture.Mode = new CaptureModeSetting(w, h, fps);
        s.Normalize();
        Assert.Null(s.Capture.Mode);
    }

    [Fact]
    public void Normalize_ValidMode_Kept()
    {
        var s = new AppSettings();
        s.Capture.Mode = new CaptureModeSetting(1920, 1080, 60);
        s.Normalize();
        Assert.Equal(new CaptureModeSetting(1920, 1080, 60), s.Capture.Mode);
    }
}
