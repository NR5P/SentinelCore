using System.Windows;
using System.Windows.Controls;
using SentinelCore.App.Views;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App.Organizer;

/// <summary>
/// Just a card's comments, opened from the speech bubble on its board tile: the card's title and
/// column over the same feed and "Write a comment…" box the full editor shows, and none of its
/// settings. Comments post at once, as in the editor, so there's only a Close button. Follows sync
/// while open - the feed, and the title and column if they change - and closes itself if the card
/// is deleted on another device.
/// </summary>
internal sealed class CardCommentsWindow : Window
{
    private readonly OrganizerEngine _engine;
    private readonly string _cardId;
    private readonly CommentsEditor _comments;
    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Palette.PrimaryText, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _column = Ui.Caption("");
    private readonly Ui.Coalescer _refresh;

    public CardCommentsWindow(OrganizerEngine engine, string cardId)
    {
        _engine = engine;
        _cardId = cardId;
        _refresh = new Ui.Coalescer(_title, ShowCard);

        Style = Ui.Style("Dialog");
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 480;
        Height = 600;
        MinWidth = 380;
        MinHeight = 340;
        Title = "Comments";

        _comments = new CommentsEditor(engine, cardId);
        Content = Build();
        ShowCard();

        engine.Changed += _refresh.Request;
        Closed += (_, _) =>
        {
            engine.Changed -= _refresh.Request;
            _comments.Detach();
        };
        _ = LoadPeopleAsync();
    }

    private UIElement Build()
    {
        var heading = new StackPanel();
        heading.Children.Add(_title);
        _column.Margin = new Thickness(0, 2, 0, 0);
        heading.Children.Add(_column);
        var header = new Border { Style = Ui.Style("DialogHeader"), Child = heading };

        var close = Ui.Button("Close", Close);
        close.MinWidth = 90;
        close.IsCancel = true;
        var footer = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        var footerBorder = new Border { Style = Ui.Style("DialogFooter"), Child = footer };

        _comments.Margin = new Thickness(18, 16, 18, 16);
        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        root.Children.Add(footerBorder);
        root.Children.Add(new ScrollViewer { Content = _comments, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        return root;
    }

    private void ShowCard()
    {
        if (_engine.Item(_cardId) is not { } card)
        {
            // Deleted elsewhere: there's nothing left to comment on.
            if (IsLoaded) Close();
            return;
        }
        var title = card.Title.Length == 0 ? "Untitled card" : card.Title;
        _title.Text = title;
        Title = $"Comments - {title}";
        var column = card.ParentId is { } parentId ? _engine.Item(parentId)?.Title : null;
        _column.Text = column is { Length: > 0 } ? $"In {column}" : "";
        _column.Visibility = _column.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Display names for the bylines, as the full editor does; usernames until (or unless) they arrive.
    private async Task LoadPeopleAsync()
    {
        try
        {
            _comments.SetPeople(await _engine.PeopleAsync());
        }
        catch (Exception)
        {
            // Login off or offline: the comments still show, just without display names.
        }
    }
}
