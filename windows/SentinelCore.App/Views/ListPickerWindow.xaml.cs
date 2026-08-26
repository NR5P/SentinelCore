using System.Windows;

namespace SentinelCore.App.Views;

/// <summary>
/// Pick one item from a list. Used by the settings screen's lookups and option sources, whose
/// results are both just a list of labels the server produced.
/// </summary>
public partial class ListPickerWindow : Window
{
    public ListPickerWindow(string title, IReadOnlyList<string> items)
    {
        InitializeComponent();
        Title = title;
        Items.ItemsSource = items;
        if (items.Count > 0) Items.SelectedIndex = 0;
    }

    public int SelectedIndex { get; private set; }

    private void OnChooseClicked(object sender, RoutedEventArgs e)
    {
        if (Items.SelectedIndex < 0) return;
        SelectedIndex = Items.SelectedIndex;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
