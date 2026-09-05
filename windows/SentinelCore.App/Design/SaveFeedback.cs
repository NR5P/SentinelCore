using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SentinelCore.App.Design;

/// <summary>
/// The moment after Save, made unmissable.
/// </summary>
/// <remarks>
/// Every dialog that saves in place reports through a caption-styled <see cref="TextBlock"/> in its
/// footer, in the same dim grey as every other footer note, and the text stays there for good. In a
/// tall window the Save button and that footer can be a long way apart, so the second press looks
/// exactly like the first: the button dips and nothing on screen changes. Hence the recurring
/// question of whether it actually saved.
///
/// This gives those dialogs the same signals the web dashboard's toast gives: the footer line turns
/// green and bold for a few seconds, and the Save button itself reads "✓ Saved" for the same few
/// seconds before going back to "Save". Failures turn the line red and leave it that way until the
/// next message, since an error is something to read rather than a flash to notice.
/// </remarks>
public static class SaveFeedback
{
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(3);

    // Bumped on every flash so a stale timer from an earlier press doesn't undo a newer one.
    private static readonly DependencyProperty GenerationProperty = DependencyProperty.RegisterAttached(
        "SaveFeedbackGeneration", typeof(int), typeof(SaveFeedback), new PropertyMetadata(0));

    public static void Saved(TextBlock message, Button? button, string text = "Saved.")
    {
        Flash(message, text, "Green", revert: true);
        if (button is not null) FlashButton(button);
    }

    public static void Failed(TextBlock message, string text) => Flash(message, text, "Red", revert: false);

    /// <summary>An ordinary footer note - plain caption styling, no colour left over from before.</summary>
    public static void Note(TextBlock message, string text)
    {
        Bump(message);
        message.Text = text;
        message.ClearValue(TextBlock.ForegroundProperty);
        message.ClearValue(TextBlock.FontWeightProperty);
    }

    private static void Flash(TextBlock message, string text, string brushKey, bool revert)
    {
        var generation = Bump(message);
        message.Text = text;
        message.Foreground = (Brush)message.FindResource(brushKey);
        message.FontWeight = FontWeights.SemiBold;
        if (!revert) return;
        Later(message, () =>
        {
            if ((int)message.GetValue(GenerationProperty) != generation) return;
            message.ClearValue(TextBlock.ForegroundProperty);
            message.ClearValue(TextBlock.FontWeightProperty);
        });
    }

    private static void FlashButton(Button button)
    {
        var generation = Bump(button);
        var original = button.Content;
        button.Content = "✓ Saved";
        Later(button, () =>
        {
            if ((int)button.GetValue(GenerationProperty) != generation) return;
            button.Content = original;
        });
    }

    private static int Bump(DependencyObject target)
    {
        var next = (int)target.GetValue(GenerationProperty) + 1;
        target.SetValue(GenerationProperty, next);
        return next;
    }

    private static void Later(DispatcherObject target, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, target.Dispatcher) { Interval = Hold };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}
