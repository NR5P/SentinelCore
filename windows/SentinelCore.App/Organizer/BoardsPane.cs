using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SentinelCore.App.Views;
using SentinelCore.Client.Organizer;

namespace SentinelCore.App.Organizer;

/// <summary>
/// Trello-style boards: columns of cards, dragged between columns or moved from a card's menu -
/// to another column or to a column on another board.
/// Order is a float <c>position</c> on each card and column, and a card dropped between two others
/// takes the midpoint of theirs - so moving one card only ever rewrites that card.
/// </summary>
public sealed class BoardsPane : OrganizerPane
{
    private const string CardFormat = "SentinelCore.OrganizerCard";

    private readonly ComboBox _boardPicker = new() { MinWidth = 220 };
    private readonly StackPanel _columns = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _boardActions = new() { Orientation = Orientation.Horizontal };
    private readonly CheckBox _showDone = new() { Content = "Show finished cards", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
    private readonly ContentControl _body = new();
    // One, reused: a panel can only ever have one parent, so wrapping _columns in a fresh
    // ScrollViewer on each refresh threw - and reusing it keeps the scroll position through a sync.
    private readonly ScrollViewer _scroller;
    private string? _boardId;
    private bool _building;
    private Point _dragStart;
    private string? _pressedCard;
    // Which add box had the typing in it, so a rebuild after Enter puts the cursor back there and
    // several cards can be typed in a row.
    private string? _refocus;

    public BoardsPane(OrganizerEngine engine) : base(engine)
    {
        var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(_boardPicker);
        var add = Ui.Button("+ Board", NewBoard, "PrimaryButton");
        add.Margin = new Thickness(8, 0, 0, 0);
        top.Children.Add(add);
        top.Children.Add(_boardActions);
        top.Children.Add(_showDone);

        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);
        layout.Children.Add(_body);
        Content = layout;

        _boardPicker.SelectionChanged += (_, _) =>
        {
            if (_building || Ui.SelectedTag(_boardPicker) is not { } id) return;
            _boardId = id;
            Refresh();
        };
        _showDone.Click += (_, _) => Refresh();
        _scroller = new ScrollViewer
        {
            Content = _columns,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private List<OrganizerItem> Boards() =>
        Engine.Items(OrganizerKinds.Board).Where(Shows).Where(board => !board.Bool("archived"))
            .OrderBy(board => board.SortPosition).ThenBy(board => board.Title, StringComparer.CurrentCultureIgnoreCase).ToList();

    private List<OrganizerItem> Columns(string boardId) =>
        Engine.Children(boardId, OrganizerKinds.Column).OrderBy(column => column.SortPosition).ToList();

    private List<OrganizerItem> Cards(string columnId) =>
        Engine.Children(columnId, OrganizerKinds.Card).OrderBy(card => card.SortPosition).ToList();

    public override void Refresh()
    {
        var boards = Boards();
        if (_boardId is null || boards.All(board => board.Id != _boardId)) _boardId = boards.FirstOrDefault()?.Id;

        // Rebuilt only when the boards themselves change, so a sync doesn't snap shut a dropdown
        // someone has open.
        var wanted = boards.Select(board =>
        {
            var label = board.Title.Length == 0 ? "Untitled board" : board.Title;
            if (SpaceFilter is null) label += $"  ·  {Ui.SpaceLabel(Engine, board.SpaceId)}";
            return (Label: label, Id: board.Id);
        }).ToList();
        var listed = _boardPicker.Items.OfType<ComboBoxItem>().Select(item => ((string)item.Content, (string)item.Tag)).ToList();
        _building = true;
        if (!listed.SequenceEqual(wanted))
        {
            _boardPicker.Items.Clear();
            foreach (var (label, id) in wanted) _boardPicker.Items.Add(Ui.Option(label, id));
        }
        if (Ui.SelectedTag(_boardPicker) != _boardId) Ui.Select(_boardPicker, _boardId);
        _building = false;

        _boardActions.Children.Clear();
        if (_boardId is null || Engine.Item(_boardId) is not { } current)
        {
            _body.Content = Ui.Caption("No boards yet. A board is a set of columns - To do, Doing, Done - with cards you move across them.");
            return;
        }
        var menu = Ui.Icon("", "Board options", () => { });
        menu.Margin = new Thickness(8, 0, 0, 0);
        menu.ContextMenu = BoardMenu(current);
        menu.Click += (_, _) =>
        {
            menu.ContextMenu.PlacementTarget = menu;
            menu.ContextMenu.IsOpen = true;
        };
        _boardActions.Children.Add(menu);

        _columns.Children.Clear();
        foreach (var column in Columns(current.Id)) _columns.Children.Add(ColumnView(current, column));
        _columns.Children.Add(NewColumnView(current));
        if (_body.Content != _scroller) _body.Content = _scroller;
    }

    private ContextMenu BoardMenu(OrganizerItem board)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Rename…", () => Rename(board, "Rename board")));
        var move = new MenuItem { Header = "Who can see it" };
        foreach (var space in Engine.VisibleSpaces)
        {
            var item = MenuItem(Ui.SpaceLabel(Engine, space.Id), () => Engine.Update(board.Id, "spaceId", space.Id));
            item.IsChecked = space.Id == board.SpaceId;
            move.Items.Add(item);
        }
        menu.Items.Add(move);
        var color = new MenuItem { Header = "Color" };
        foreach (var (name, hex) in Ui.Colors)
        {
            var item = MenuItem(name, () => Engine.Update(board.Id, "color", hex));
            item.IsChecked = string.Equals(board.String("color"), hex, StringComparison.OrdinalIgnoreCase);
            color.Items.Add(item);
        }
        menu.Items.Add(color);
        menu.Items.Add(MenuItem("Archive", () => Engine.Update(board.Id, "archived", true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Delete board…", () =>
        {
            if (!Ui.Confirm(this, $"Delete \"{board.Title}\" with every column and card on it? It goes from every device.")) return;
            Engine.DeleteTree(board.Id);
        }));
        return menu;
    }

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private Border ColumnView(OrganizerItem board, OrganizerItem column)
    {
        var cards = Cards(column.Id);
        var shown = _showDone.IsChecked == true ? cards : cards.Where(card => !card.Bool("completed")).ToList();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var menuButton = Ui.Icon("", "Column options", () => { });
        menuButton.Padding = new Thickness(6, 3, 6, 3);
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Rename…", () => Rename(column, "Rename column")));
        menu.Items.Add(MenuItem("Move left", () => MoveColumn(board, column, -1)));
        menu.Items.Add(MenuItem("Move right", () => MoveColumn(board, column, 1)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Delete column…", () =>
        {
            if (cards.Count > 0 && !Ui.Confirm(this, $"Delete \"{column.Title}\" and its {cards.Count} card{(cards.Count == 1 ? "" : "s")}?")) return;
            Engine.DeleteTree(column.Id);
        }));
        menuButton.ContextMenu = menu;
        menuButton.Click += (_, _) =>
        {
            menu.PlacementTarget = menuButton;
            menu.IsOpen = true;
        };
        DockPanel.SetDock(menuButton, Dock.Right);
        header.Children.Add(menuButton);
        header.Children.Add(new TextBlock
        {
            Text = $"{column.Title}  {cards.Count}",
            FontWeight = FontWeights.SemiBold,
            Foreground = Palette.PrimaryText,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var list = new StackPanel { MinHeight = 40, Background = Brushes.Transparent };
        foreach (var card in shown) list.Children.Add(CardView(board, column, card));

        var quick = new TextBox { ToolTip = "Add a card - type and press Enter", Margin = new Thickness(0, 4, 0, 0) };
        quick.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || quick.Text.Trim().Length == 0) return;
            var position = cards.Count == 0 ? 1.0 : cards.Max(card => card.SortPosition) + 1;
            _refocus = "column:" + column.Id;
            Engine.Create(OrganizerKinds.Card, new Dictionary<string, object?> { ["title"] = quick.Text.Trim(), ["position"] = position }, parentId: column.Id);
            quick.Text = "";
            e.Handled = true;
        };
        Refocus(quick, "column:" + column.Id);

        var stack = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        stack.Children.Add(header);
        var addLabel = Ui.Caption("Add a card");
        addLabel.Margin = new Thickness(0, 6, 0, 0);
        var footer = new StackPanel();
        footer.Children.Add(addLabel);
        footer.Children.Add(quick);
        DockPanel.SetDock(footer, Dock.Bottom);
        stack.Children.Add(footer);
        stack.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var border = new Border
        {
            Width = 270,
            Margin = new Thickness(0, 0, 12, 0),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            Background = Ui.Brush("SidebarBackground"),
            BorderBrush = Ui.Brush("CardBorder"),
            BorderThickness = new Thickness(1),
            Child = stack,
            AllowDrop = true,
        };
        border.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(CardFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        };
        border.Drop += (_, e) =>
        {
            if (e.Data.GetData(CardFormat) is not string cardId) return;
            DropCard(cardId, column, shown, list, e.GetPosition(list));
            e.Handled = true;
        };
        return border;
    }

    private Border NewColumnView(OrganizerItem board)
    {
        var box = new TextBox { ToolTip = "Type a column name and press Enter" };
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || box.Text.Trim().Length == 0) return;
            var columns = Columns(board.Id);
            var position = columns.Count == 0 ? 1.0 : columns.Max(column => column.SortPosition) + 1;
            _refocus = "new-column";
            Engine.Create(OrganizerKinds.Column, new Dictionary<string, object?> { ["title"] = box.Text.Trim(), ["position"] = position }, parentId: board.Id);
            box.Text = "";
            e.Handled = true;
        };
        Refocus(box, "new-column");
        var stack = new StackPanel();
        stack.Children.Add(Ui.Caption("Add a column"));
        stack.Children.Add(box);
        return new Border
        {
            Width = 220,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(8),
            BorderBrush = Ui.Brush("CardBorder"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Child = stack,
        };
    }

    private void Refocus(TextBox box, string key)
    {
        if (_refocus != key) return;
        _refocus = null;
        box.Dispatcher.BeginInvoke(() => box.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private Border CardView(OrganizerItem board, OrganizerItem column, OrganizerItem card)
    {
        var done = card.Bool("completed");
        var content = new StackPanel();

        if (card.String("cover") is { } coverId && Engine.Item(coverId)?.String("hash") is { } coverHash)
        {
            var cover = new Image { MaxHeight = 120, Stretch = Stretch.UniformToFill, Margin = new Thickness(-8, -6, -8, 6) };
            _ = LoadCoverAsync(cover, coverHash);
            content.Children.Add(cover);
        }

        var labels = card.List("labels").OfType<IReadOnlyDictionary<string, object?>>().ToList();
        if (labels.Count > 0)
        {
            var chips = new WrapPanel();
            foreach (var label in labels)
            {
                chips.Children.Add(Ui.Chip(label.GetValueOrDefault("name") as string ?? "", Ui.ColorBrush(label.GetValueOrDefault("color") as string, 90)));
            }
            content.Children.Add(chips);
        }

        content.Children.Add(new TextBlock
        {
            Text = card.Title,
            TextWrapping = TextWrapping.Wrap,
            Foreground = done ? Palette.SecondaryText : Palette.PrimaryText,
            TextDecorations = done ? TextDecorations.Strikethrough : null,
        });

        var badges = new List<string>();
        if (OrganizerDates.Parse(card.String("due"), true, Ui.Zone) is { } due) badges.Add("Due " + Ui.When(due, true));
        var checklist = card.List("checklist").OfType<IReadOnlyDictionary<string, object?>>().ToList();
        if (checklist.Count > 0) badges.Add($"☑ {checklist.Count(item => item.GetValueOrDefault("done") is true)}/{checklist.Count}");
        var files = Engine.Children(card.Id, OrganizerKinds.Attachment).Count;
        if (files > 0) badges.Add($"{files} file{(files == 1 ? "" : "s")}");
        var comments = Engine.Children(card.Id, OrganizerKinds.Comment).Count;
        if (comments > 0) badges.Add($"{comments} comment{(comments == 1 ? "" : "s")}");
        if (card.String("description") is { Length: > 0 }) badges.Add("notes");
        var assignees = card.List("assignees").OfType<string>().ToList();
        if (assignees.Count > 0) badges.Add(string.Join(", ", assignees));
        if (badges.Count > 0)
        {
            var text = Ui.Caption(string.Join(" · ", badges));
            text.Margin = new Thickness(0, 4, 0, 0);
            content.Children.Add(text);
        }

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Open", () => new ItemEditorWindow(Engine, OrganizerKinds.Card, card).ShowDialog()));
        menu.Items.Add(MenuItem(done ? "Mark not done" : "Mark done", () => Engine.Update(card.Id, new Dictionary<string, object?>
        {
            ["completed"] = !done,
            ["completedAt"] = !done ? OrganizerDates.Timestamp() : null,
        })));
        var move = new MenuItem { Header = "Move to" };
        foreach (var target in Columns(board.Id).Where(other => other.Id != column.Id))
        {
            move.Items.Add(MenuItem(target.Title, () => MoveCardToEnd(card.Id, target)));
        }
        move.IsEnabled = move.Items.Count > 0;
        menu.Items.Add(move);
        var toBoard = new MenuItem { Header = "Move to board" };
        foreach (var other in Boards().Where(other => other.Id != board.Id))
        {
            var boardItem = new MenuItem { Header = other.Title.Length == 0 ? "Untitled board" : other.Title };
            foreach (var target in Columns(other.Id))
            {
                boardItem.Items.Add(MenuItem(target.Title, () => MoveCardToEnd(card.Id, target)));
            }
            boardItem.IsEnabled = boardItem.Items.Count > 0;
            toBoard.Items.Add(boardItem);
        }
        toBoard.IsEnabled = toBoard.Items.Count > 0;
        menu.Items.Add(toBoard);
        var images = Engine.Children(card.Id, OrganizerKinds.Attachment)
            .Where(item => item.String("contentType")?.StartsWith("image/", StringComparison.Ordinal) == true).ToList();
        if (images.Count > 0)
        {
            var coverMenu = new MenuItem { Header = "Cover picture" };
            coverMenu.Items.Add(MenuItem("None", () => Engine.Update(card.Id, "cover", null)));
            foreach (var image in images) coverMenu.Items.Add(MenuItem(image.String("name") ?? "picture", () => Engine.Update(card.Id, "cover", image.Id)));
            menu.Items.Add(coverMenu);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Delete", () =>
        {
            if (Ui.Confirm(this, $"Delete \"{card.Title}\"?")) Engine.DeleteTree(card.Id);
        }));

        var border = new Border
        {
            Background = Ui.Brush("CardBackground"),
            BorderBrush = Ui.Brush("CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 8),
            Margin = new Thickness(0, 0, 0, 6),
            Child = content,
            Cursor = Cursors.Hand,
            ContextMenu = menu,
            Tag = card.Id,
            ClipToBounds = true,
        };
        border.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(this);
            _pressedCard = card.Id;
        };
        border.MouseMove += (_, e) =>
        {
            // Only the card the press started on: holding the button down elsewhere and sweeping
            // over a card mustn't pick it up.
            if (e.LeftButton != MouseButtonState.Pressed || _pressedCard != card.Id) return;
            var moved = e.GetPosition(this) - _dragStart;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            border.Opacity = 0.5;
            _pressedCard = null;
            DragDrop.DoDragDrop(border, new DataObject(CardFormat, card.Id), DragDropEffects.Move);
            border.Opacity = 1;
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressedCard != card.Id) return;
            _pressedCard = null;
            var moved = e.GetPosition(this) - _dragStart;
            if (Math.Abs(moved.X) > 4 || Math.Abs(moved.Y) > 4) return;
            new ItemEditorWindow(Engine, OrganizerKinds.Card, card).ShowDialog();
        };
        return border;
    }

    private async Task LoadCoverAsync(Image target, string hash)
    {
        var bytes = await Engine.ImageBytesAsync(hash, thumbnail: true);
        if (bytes is not null) target.Source = AttachmentsEditor.Bitmap(bytes, 540);
    }

    /// <summary>
    /// Puts a dropped card where it landed: between the two cards either side of the drop point,
    /// at the midpoint of their positions.
    /// </summary>
    private void DropCard(string cardId, OrganizerItem column, List<OrganizerItem> shown, StackPanel list, Point point)
    {
        var others = shown.Where(card => card.Id != cardId).ToList();
        var views = list.Children.OfType<Border>().Where(view => (string)view.Tag != cardId).ToList();
        var index = 0;
        while (index < views.Count)
        {
            var view = views[index];
            var top = view.TranslatePoint(new Point(0, 0), list).Y;
            if (point.Y < top + view.ActualHeight / 2) break;
            index++;
        }
        double position;
        if (others.Count == 0) position = 1;
        else if (index == 0) position = others[0].SortPosition - 1;
        else if (index >= others.Count) position = others[^1].SortPosition + 1;
        else position = (others[index - 1].SortPosition + others[index].SortPosition) / 2;

        MoveCard(cardId, column, position);
    }

    /// <summary>
    /// Puts a card into a column, which may be on another board or in another space. The server
    /// would move the card's comments and attachments into the new space anyway; sending the space
    /// too lets this device do the same straight away.
    /// </summary>
    private void MoveCard(string cardId, OrganizerItem column, double position)
    {
        if (Engine.Item(cardId) is not { } card) return;
        var changes = new Dictionary<string, object?> { ["position"] = position };
        if (card.ParentId != column.Id) changes["parentId"] = column.Id;
        if (card.SpaceId != column.SpaceId) changes["spaceId"] = column.SpaceId;
        Engine.Update(cardId, changes);
    }

    private void MoveCardToEnd(string cardId, OrganizerItem column)
    {
        var others = Cards(column.Id).Where(other => other.Id != cardId).ToList();
        MoveCard(cardId, column, others.Count == 0 ? 1.0 : others.Max(other => other.SortPosition) + 1);
    }

    private void MoveColumn(OrganizerItem board, OrganizerItem column, int direction)
    {
        var columns = Columns(board.Id);
        var index = columns.FindIndex(other => other.Id == column.Id);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= columns.Count) return;
        var neighbour = columns[target];
        var beyond = target + direction >= 0 && target + direction < columns.Count ? columns[target + direction] : null;
        var position = beyond is null
            ? neighbour.SortPosition + direction
            : (neighbour.SortPosition + beyond.SortPosition) / 2;
        Engine.Update(column.Id, "position", position);
    }

    private void Rename(OrganizerItem item, string title)
    {
        var prompt = new TextPromptWindow(title, "Name:", initialValue: item.Title) { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() == true) Engine.Update(item.Id, "title", prompt.Value);
    }

    private void NewBoard()
    {
        var prompt = new TextPromptWindow("New board", "What's the board for?") { Owner = Window.GetWindow(this) };
        if (prompt.ShowDialog() != true) return;
        var board = Engine.Create(OrganizerKinds.Board, new Dictionary<string, object?>
        {
            ["title"] = prompt.Value,
            ["position"] = (double)(Boards().Count + 1),
        }, spaceId: NewItemSpace);
        var position = 1.0;
        foreach (var name in new[] { "To do", "Doing", "Done" })
        {
            Engine.Create(OrganizerKinds.Column, new Dictionary<string, object?> { ["title"] = name, ["position"] = position++ }, parentId: board.Id);
        }
        _boardId = board.Id;
        Refresh();
    }

    public override void Reveal(OrganizerItem item)
    {
        var board = item;
        for (var guard = 0; board.Kind != OrganizerKinds.Board && board.ParentId is { } parentId && guard < 8; guard++)
        {
            if (Engine.Item(parentId) is not { } parent) break;
            board = parent;
        }
        if (board.Kind == OrganizerKinds.Board) _boardId = board.Id;
        Refresh();
        if (item.Kind == OrganizerKinds.Card) new ItemEditorWindow(Engine, OrganizerKinds.Card, item).ShowDialog();
    }
}
