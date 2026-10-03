using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SentinelCore.App.Audio;
using SentinelCore.Client.Audio;

namespace SentinelCore.App.Views.Audio;

/// <summary>Controls the mini player and the Now Playing screen share.</summary>
internal static class PlayerParts
{
    /// <summary>
    /// A seek bar that follows playback but leaves the thumb alone while it is being dragged, and
    /// seeks when it is let go (or the track is clicked).
    /// </summary>
    public sealed class Scrubber
    {
        private bool _dragging;

        public Scrubber(PlayerController player, double width = double.NaN)
        {
            Slider = new Slider { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true, Width = width, VerticalAlignment = VerticalAlignment.Center };
            AudioUi.AutomationName(Slider, "Position");
            Slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
            Slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
            {
                _dragging = false;
                player.Seek(Slider.Value);
            }));
            Slider.PreviewMouseLeftButtonUp += (_, _) =>
            {
                if (!_dragging) player.Seek(Slider.Value);
            };
            Slider.KeyUp += (_, args) =>
            {
                if (args.Key is Key.Left or Key.Right or Key.PageUp or Key.PageDown or Key.Home or Key.End) player.Seek(Slider.Value);
            };
            Slider.SmallChange = 5;
            Slider.LargeChange = 30;
        }

        public Slider Slider { get; }
        public TextBlock Elapsed { get; } = new() { Foreground = Palette.SecondaryText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, MinWidth = 42, TextAlignment = TextAlignment.Right };
        public TextBlock Left { get; } = new() { Foreground = Palette.SecondaryText, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, MinWidth = 46 };

        public void Update(double position, double duration)
        {
            if (_dragging || Slider.IsMouseCaptureWithin) return;
            Slider.Maximum = Math.Max(1, duration);
            Slider.Value = Math.Clamp(position, 0, Slider.Maximum);
            Elapsed.Text = AudioTime.Clock(position);
            Left.Text = duration > 0 ? AudioTime.Remaining(position, duration) : "";
        }
    }

    /// <summary>The sleep timer's menu: minutes, end of the item, or off.</summary>
    public static ContextMenu SleepMenu(PlayerController player)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var minutes in new[] { 5, 10, 15, 30, 45, 60, 90 })
            {
                menu.Items.Add(AudioUi.Item($"{minutes} minutes", () => player.SetSleepMinutes(minutes)));
            }
            menu.Items.Add(AudioUi.Item(player.IsEpisode ? "End of this episode" : "End of this track", player.SetSleepEndOfItem));
            if (player.Sleep.IsActive)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(AudioUi.Item("Turn off the sleep timer", player.CancelSleep));
            }
        };
        menu.Items.Add(new MenuItem { Header = "…" });
        return menu;
    }

    /// <summary>The speed menu (§11: podcasts by default, remembered per show).</summary>
    public static ContextMenu SpeedMenu(PlayerController player)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) =>
        {
            menu.Items.Clear();
            foreach (var speed in PodcastPlayback.Speeds)
            {
                var item = AudioUi.Item(PodcastPlayback.SpeedLabel(speed), () => player.SetSpeed(speed));
                item.IsChecked = Math.Abs(player.Speed - speed) < 0.01;
                menu.Items.Add(item);
            }
        };
        menu.Items.Add(new MenuItem { Header = "…" });
        return menu;
    }

    public static void Popup(Button button, ContextMenu menu)
    {
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    public static string RepeatGlyph(RepeatMode mode) => mode == RepeatMode.One ? AudioUi.RepeatOneGlyph : AudioUi.RepeatAllGlyph;

    public static string RepeatTip(RepeatMode mode) => mode switch
    {
        RepeatMode.All => "Repeat all - click for repeat one",
        RepeatMode.One => "Repeat one - click to turn off",
        _ => "Repeat off - click for repeat all",
    };
}
