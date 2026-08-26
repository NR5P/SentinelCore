using System.Windows;

namespace SentinelCore.App.Views;

/// <summary>
/// One line of text, asked for in a dialog. WPF has no built-in input box, and the handful of
/// places that need one (a new password, a zone name, a plate) do not each deserve a window.
/// </summary>
public partial class TextPromptWindow : Window
{
    private readonly bool _isPassword;

    public TextPromptWindow(string title, string prompt, bool isPassword = false, string initialValue = "")
    {
        _isPassword = isPassword;
        InitializeComponent();

        Title = title;
        PromptText.Text = prompt;

        if (isPassword)
        {
            ValueBox.Visibility = Visibility.Collapsed;
            SecretBox.Visibility = Visibility.Visible;
            Loaded += (_, _) => SecretBox.Focus();
        }
        else
        {
            ValueBox.Text = initialValue;
            Loaded += (_, _) =>
            {
                ValueBox.Focus();
                ValueBox.SelectAll();
            };
        }
    }

    public string Value { get; private set; } = "";

    private void OnOkClicked(object sender, RoutedEventArgs e)
    {
        Value = _isPassword ? SecretBox.Password : ValueBox.Text.Trim();
        if (Value.Length == 0) return;
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
