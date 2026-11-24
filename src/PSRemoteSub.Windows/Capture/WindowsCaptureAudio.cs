using PSRemoteSub.Core.Capture;

namespace PSRemoteSub.Windows.Capture;

/// <summary>The Windows <see cref="ICaptureAudio"/>: a <see cref="CaptureAudio"/> (WinRT AudioGraph).</summary>
public sealed class WindowsCaptureAudio : ICaptureAudio
{
    private readonly CaptureAudio _audio = new();

    public bool IsRunning => _audio.IsRunning;

    public event EventHandler<bool>? RunningChanged
    {
        add => _audio.RunningChanged += value;
        remove => _audio.RunningChanged -= value;
    }

    public double Volume
    {
        get => _audio.Volume;
        set => _audio.Volume = value;
    }

    public bool Muted
    {
        get => _audio.Muted;
        set => _audio.Muted = value;
    }

    public void Start(string audioDeviceId) => _audio.Start(audioDeviceId);

    public void Stop() => _audio.Stop();

    public void Dispose() => _audio.Dispose();
}
