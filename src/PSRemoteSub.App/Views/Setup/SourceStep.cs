using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App.Views.Setup;

/// <summary>
/// Step 1 "ภาพเกมมาจากไหน" (mockup New-Setup-1): PS Remote Play (recommended; "found" pill while its window is found)
/// or the capture card (HDCP line; disabled with the unavailable text when the card cannot be used on this PC).
/// Next applies the choice with <see cref="AppHost.SetPictureSource"/>, as the Source page does.
/// </summary>
public sealed class SourceStep : SetupStep
{
    private const string MonitorGlyph = "M3 5h18v12H3zM8 21h8M12 17v4";
    private const string ChipGlyph = "M7 7h10v10H7zM10 3v4M14 3v4M10 17v4M14 17v4M3 10h4M3 14h4M17 10h4M17 14h4";

    private readonly OptionCard _remote;
    private readonly OptionCard _card;
    private readonly Border _foundPill;
    private readonly TextBlock _cardLine = new() { Classes = { "sub" }, TextWrapping = TextWrapping.Wrap };

    public SourceStep(AppHost host) : base(host)
    {
        _foundPill = new Border
        {
            Classes = { "pill", "ok" },
            IsVisible = false,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new StrokeIcon { Data = CheckGlyph, Size = 14, StrokeThickness = 2.4, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = Loc.Get("SetupRemotePlayFound") },
                },
            },
        };
        _remote = BigCard("SetupSource", MonitorGlyph, "PS Remote Play", recommended: true, Loc.Get("SetupRemotePlayDesc"), _foundPill);
        _card = BigCard("SetupSource", ChipGlyph, "Capture card", recommended: false, Loc.Get("SetupCaptureDesc"), _cardLine);
        _remote.Button.IsCheckedChanged += (_, _) => Safe(ShowCards);
        _card.Button.IsCheckedChanged += (_, _) => Safe(ShowCards);

        bool available = Host.IsCaptureAvailable;
        _card.Button.IsEnabled = available;
        _cardLine.Text = Loc.Get(available ? "SetupCaptureHdcp" : "CaptureUnavailable");
        bool card = available && Host.PictureSource == PictureSource.CaptureCard;
        _remote.Button.IsChecked = !card;
        _card.Button.IsChecked = card;
        ShowCards();

        Content = new StackPanel
        {
            Spacing = 22,
            Children =
            {
                TwoColumns(_remote.Button, _card.Button),
                InfoNote(InfoGlyph, Loc.Get("SetupSourceNoteLead"), "PS Remote Play"),
            },
        };
    }

    public override string StepName => Loc.Get("SetupStepSource");

    public override string Title => Loc.Get("SetupSourceTitle");

    public override string Subtitle => Loc.Get("SetupSourceSub");

    public override void OnShown() => ShowFound();

    protected override void OnHostStateChangedCore() => ShowFound();

    public override void Apply()
    {
        var source = _card.Button.IsChecked == true && Host.IsCaptureAvailable ? PictureSource.CaptureCard : PictureSource.RemotePlay;
        Host.SetPictureSource(source); // applies and saves; does nothing when unchanged
    }

    private void ShowCards()
    {
        _remote.Show();
        _card.Show();
    }

    /// <summary>The pill shows only what the app knows: the Remote Play window is found right now.</summary>
    private void ShowFound() => _foundPill.IsVisible = Host.Status.RemotePlayFound;
}
