using PSRemoteSub.Core.Capture;
using Vortice.MediaFoundation;
using Windows.Devices.Enumeration;

namespace PSRemoteSub.Windows.Capture;

/// <summary>Lists capture devices, their paired audio input and the video modes they offer.</summary>
public static class CaptureDeviceCatalog
{
    private const string ContainerIdProperty = "System.Devices.ContainerId";
    private const int MfENoMoreTypes = unchecked((int)0xC00D36B9);
    private const int MfEShutdown = unchecked((int)0xC00D3E85);

    /// <summary>
    /// Lists video capture devices, each with its paired audio input (<see cref="CaptureDeviceRules.PairAudio"/>) and
    /// whether it is built into the PC (it reports a panel, lid or dock location, as a laptop camera does).
    /// </summary>
    public static async Task<IReadOnlyList<CaptureDevice>> ListAsync()
    {
        string[] props = [ContainerIdProperty];
        var audios = await DeviceInformation.FindAllAsync(
            DeviceInformation.GetAqsFilterFromDeviceClass(DeviceClass.AudioCapture), props);
        var videosWithProps = await DeviceInformation.FindAllAsync(
            DeviceInformation.GetAqsFilterFromDeviceClass(DeviceClass.VideoCapture), props);

        var audioInputs = audios.Select(a => (a.Id, ContainerOf(a))).ToList();
        var result = new List<CaptureDevice>();
        foreach (var video in videosWithProps)
        {
            string? audioId = CaptureDeviceRules.PairAudio(ContainerOf(video), audioInputs);
            result.Add(new CaptureDevice(video.Id, video.Name, audioId, IsBuiltIn(video)));
        }
        return result;
    }

    /// <summary>
    /// Lists the native video modes of a device through a Media Foundation Source Reader.
    /// <c>MFStartup</c>/<c>MFShutdown</c> are paired inside each call (Media Foundation reference-counts them).
    /// </summary>
    public static IReadOnlyList<CaptureMode> ListModes(string deviceId)
    {
        MediaFactory.MFStartup().CheckError();
        try
        {
            using var attributes = MediaFactory.MFCreateAttributes(2);
            attributes.Set(CaptureDeviceAttributeKeys.SourceType, CaptureDeviceAttributeKeys.SourceTypeVidcap);
            attributes.Set(CaptureDeviceAttributeKeys.SourceTypeVidcapSymbolicLink, deviceId);

            using var source = MediaFactory.MFCreateDeviceSource(attributes);
            try
            {
                using var reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, null);
                var modes = new List<CaptureMode>();
                for (int i = 0; ; i++)
                {
                    IMFMediaType type;
                    try
                    {
                        type = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, i);
                    }
                    catch (SharpGen.Runtime.SharpGenException ex) when (ex.HResult == MfENoMoreTypes)
                    {
                        break;
                    }

                    using (type)
                        modes.Add(ToMode(type));
                }
                return modes;
            }
            finally
            {
                // Disposing the reader already shuts the source down, so MF_E_SHUTDOWN here is expected.
                try { source.Shutdown(); }
                catch (SharpGen.Runtime.SharpGenException ex) when (ex.HResult == MfEShutdown) { }
            }
        }
        finally
        {
            MediaFactory.MFShutdown();
        }
    }

    /// <summary>The size, rate and subtype of a native video media type.</summary>
    internal static CaptureMode ToMode(IMFMediaType type)
    {
        MediaFactory.MFGetAttributeSize(type, MediaTypeAttributeKeys.FrameSize, out uint w, out uint h);
        MediaFactory.MFGetAttributeRatio(type, MediaTypeAttributeKeys.FrameRate, out uint num, out uint den);
        double fps = den == 0 ? 0 : (double)num / den;
        var subtype = type.GetGUID(MediaTypeAttributeKeys.Subtype);
        return new CaptureMode((int)w, (int)h, fps, MapSubtype(subtype));
    }

    private static Guid? ContainerOf(DeviceInformation info) =>
        info.Properties.TryGetValue(ContainerIdProperty, out var value) && value is Guid g ? g : null;

    private static bool IsBuiltIn(DeviceInformation info)
    {
        try
        {
            return info.EnclosureLocation is { } location
                && (location.Panel != Panel.Unknown || location.InLid || location.InDock);
        }
        catch (Exception)
        {
            return false; // no location information: treated as external
        }
    }

    private static CaptureSubtype MapSubtype(Guid subtype) =>
        subtype == VideoFormatGuids.NV12 ? CaptureSubtype.Nv12
        : subtype == VideoFormatGuids.YUY2 ? CaptureSubtype.Yuy2
        : subtype == VideoFormatGuids.Mjpg ? CaptureSubtype.Mjpg
        : CaptureSubtype.Other;
}
