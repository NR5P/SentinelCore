using System.Windows;

namespace SentinelCore.App.Views;

/// <summary>
/// How a section opens a detail on top of itself.
/// </summary>
/// <remarks>
/// The Apple client pushes these into the same window, and coming back leaves the list exactly as
/// it was - same scroll position, same filters, same selection. Opening a separate window instead
/// loses all of that, and on a screen like Live it means the camera you are watching is in one
/// window while everything about that camera is in another.
///
/// An interface rather than a direct reference to the window so a section can be built and tested
/// without one.
/// </remarks>
public interface INavigator
{
    void Push(FrameworkElement view, string title);

    void Pop();

    /// <summary>
    /// Hands the whole window to the view on top: the sidebar and the back bar go away, and with
    /// <paramref name="fullScreen"/> the window's own frame and the taskbar too. Used by the movie
    /// player, whose page draws its own close button. A navigator that has no chrome to hide need
    /// not do anything.
    /// </summary>
    void SetImmersive(bool immersive, bool fullScreen = false)
    {
    }
}

/// <summary>A navigator that goes nowhere, for a view built outside the shell.</summary>
public sealed class NullNavigator : INavigator
{
    public static NullNavigator Instance { get; } = new();

    public void Push(FrameworkElement view, string title)
    {
    }

    public void Pop()
    {
    }
}
