using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PSRemoteSub.App.Views.Controls;
using PSRemoteSub.Core.Settings;

namespace PSRemoteSub.App.Views.Setup;

/// <summary>
/// Step 3 "แปลด้วยอะไรดี" (mockup New-Setup-3): Google (recommended) or AI, and the privacy line. Next sets
/// <c>Translation.Mode</c> through <see cref="SettingsEdits.SetTranslationMode"/> (the Translation page's path). An AI
/// choice without a ready provider is finished on the Translation page (see <see cref="SetupWindow"/>).
/// </summary>
public sealed class TranslatorStep : SetupStep
{
    private const string TranslateGlyph = "M4 5h8M8 3v2M10 5c-1 4-3.5 7-6 8.5M6 9c1.2 2 3 3.5 5 4.3M13 21l4-10 4 10M14.5 17.5h5";
    private const string SparkleGlyph = "M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9zM19 16l.8 2.2L22 19l-2.2.8L19 22l-.8-2.2L16 19l2.2-.8z";
    private const string LockGlyph = "M6 11h12v10H6zM8.5 11V8a3.5 3.5 0 0 1 7 0v3";

    private readonly OptionCard _google;
    private readonly OptionCard _ai;

    public TranslatorStep(AppHost host) : base(host)
    {
        _google = BigCard("SetupTranslator", TranslateGlyph, "Google Translate", recommended: true, Loc.Get("SetupGoogleDesc"),
            Bullets(
                BulletLine(CheckGlyph, "OkBrush", Loc.Get("SetupGoogleGood1")),
                BulletLine(CheckGlyph, "OkBrush", Loc.Get("SetupGoogleGood2")),
                BulletLine(InfoGlyph, "Text3Brush", Loc.Get("SetupGoogleBad"))));
        _ai = BigCard("SetupTranslator", SparkleGlyph, "AI", recommended: false, Loc.Get("SetupAiDesc"),
            Bullets(
                BulletLine(CheckGlyph, "OkBrush", Loc.Get("SetupAiGood")),
                BulletLine(InfoGlyph, "Text3Brush", Loc.Get("SetupAiBad1")),
                BulletLine(InfoGlyph, "Text3Brush", Loc.Get("SetupAiBad2"))));
        _google.Button.IsCheckedChanged += (_, _) => Safe(ShowCards);
        _ai.Button.IsCheckedChanged += (_, _) => Safe(ShowCards);
        bool ai = Settings.Translation.Mode == TranslationMode.Ai;
        _google.Button.IsChecked = !ai;
        _ai.Button.IsChecked = ai;
        ShowCards();

        var lockIcon = new StrokeIcon { Data = LockGlyph, Size = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        lockIcon.Bind(StrokeIcon.ForegroundProperty, lockIcon.GetResourceObservable("Text3Brush"));
        var privacyText = new TextBlock { Classes = { "sub" }, Text = Loc.Get("SetupPrivacy"), TextWrapping = TextWrapping.Wrap };
        var privacy = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        privacy.Children.Add(lockIcon);
        Grid.SetColumn(privacyText, 1);
        privacy.Children.Add(privacyText);

        Content = new StackPanel { Spacing = 22, Children = { TwoColumns(_google.Button, _ai.Button), privacy } };
    }

    public override string StepName => Loc.Get("SetupStepTranslator");

    public override string Title => Loc.Get("SetupTranslatorTitle");

    public override string Subtitle => Loc.Get("SetupTranslatorSub");

    public override void Apply() =>
        SettingsEdits.SetTranslationMode(Host, _ai.Button.IsChecked == true ? TranslationMode.Ai : TranslationMode.Google);

    private void ShowCards()
    {
        _google.Show();
        _ai.Show();
    }

    private static StackPanel Bullets(params Control[] lines)
    {
        var stack = new StackPanel { Spacing = 4 };
        foreach (var line in lines)
            stack.Children.Add(line);
        return stack;
    }
}
