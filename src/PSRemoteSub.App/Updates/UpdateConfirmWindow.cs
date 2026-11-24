using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PSRemoteSub.App.Updates;

/// <summary>Small Yes/No dialog for "a new version is available". Built in code; UI thread only.</summary>
internal sealed class UpdateConfirmWindow : Window
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public UpdateConfirmWindow(string message)
    {
        Title = "PS Remote Sub";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var yes = new Button { Content = Loc.Get("UpdateYes"), IsDefault = true, MinWidth = 90 };
        var later = new Button { Content = Loc.Get("UpdateLater"), IsCancel = true, MinWidth = 90 };
        yes.Click += (_, _) => Finish(true);
        later.Click += (_, _) => Finish(false);
        Closed += (_, _) => _result.TrySetResult(false);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    FontFamily = new FontFamily("Leelawadee UI"),
                    FontSize = 15,
                    TextWrapping = TextWrapping.Wrap,
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { later, yes },
                },
            },
        };
    }

    /// <summary>Shows the window and completes with the user's choice (closing the window counts as "later").</summary>
    public Task<bool> AskAsync()
    {
        Show();
        return _result.Task;
    }

    private void Finish(bool answer)
    {
        _result.TrySetResult(answer);
        Close();
    }
}
