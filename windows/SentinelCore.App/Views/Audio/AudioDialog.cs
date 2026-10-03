using System.Windows;
using System.Windows.Controls;
using SentinelCore.Client;

namespace SentinelCore.App.Views.Audio;

/// <summary>
/// A dialog in the app's own look, built in code: a header with a title and a line of
/// explanation, a scrolling body, and a footer of buttons with a message line.
/// </summary>
public abstract class AudioDialog : Window
{
    private readonly StackPanel _footerButtons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };

    protected AudioDialog(AppModel model, string title, string? explanation, double width = 640, double height = 560)
    {
        Model = model;
        Style = AudioUi.StyleOf("Dialog");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = title;
        Width = width;
        Height = height;
        MinWidth = Math.Min(width, 460);
        MinHeight = Math.Min(height, 320);
        _message.Style = AudioUi.StyleOf("Caption");

        var header = new Border { Style = AudioUi.StyleOf("DialogHeader") };
        var headerText = new StackPanel();
        headerText.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = AudioUi.Resource("Green") });
        if (!string.IsNullOrWhiteSpace(explanation)) headerText.Children.Add(AudioUi.Caption(explanation, 3));
        header.Child = headerText;

        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.Children.Add(_message);
        Grid.SetColumn(_footerButtons, 1);
        footerGrid.Children.Add(_footerButtons);
        var footer = new Border { Style = AudioUi.StyleOf("DialogFooter"), Child = footerGrid };

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = Body,
            Padding = new Thickness(18, 14, 18, 14),
        };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(scroller);
        Content = dock;
    }

    protected AppModel Model { get; }
    protected StackPanel Body { get; } = new();

    protected Button AddFooterButton(string text, Action click, bool primary = false, bool isCancel = false)
    {
        var button = new Button { Content = text, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = isCancel };
        if (primary)
        {
            button.Style = AudioUi.StyleOf("PrimaryButton");
            button.IsDefault = true;
        }
        button.Click += (_, _) => click();
        _footerButtons.Children.Add(button);
        return button;
    }

    protected void Message(string? text, bool error = false)
    {
        _message.Text = text ?? "";
        _message.Foreground = error ? Palette.Red : Palette.SecondaryText;
    }

    protected void Fail(Exception error) => Message(AppModel.UserFacingMessage(error), error: true);

    protected static TextBlock Label(string text) => new() { Text = text, Style = AudioUi.StyleOf("FieldLabel") };

    protected void Done()
    {
        DialogResult = true;
        Close();
    }
}
