namespace PSRemoteSub.Core.Layout;

/// <summary>
/// <c>Custom</c>: in the profile's caption box, which the user sets apart from the OCR box. <c>Fit</c>: over the game's
/// own subtitle, where OCR found it (in the caption box when that is not known).
/// <c>Cover</c> and <c>Above</c> are legacy: kept only so old settings files load (an unknown name would reset the whole
/// file); <see cref="Settings.AppSettings"/> migrates them to <c>Custom</c> on load.
/// </summary>
public enum OverlayPlacement { Cover, Above, Fit, Custom }
