namespace PSRemoteSub.Core.Capture;

/// <summary>Pixel format of a capture-card mode; <see cref="Other"/> is anything the app cannot decode.</summary>
public enum CaptureSubtype { Nv12, Yuy2, Mjpg, Other }

/// <summary>One mode (size, rate, pixel format) offered by a capture device.</summary>
public sealed record CaptureMode(int Width, int Height, double Fps, CaptureSubtype Subtype);

/// <summary>Lifecycle of the capture source as shown to the user.</summary>
public enum CaptureState { NoDevice, Opening, Playing, NoSignal, InUse, Blocked, Unplugged, Failed }
