using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SentinelCore.Client;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can be replaced wholesale in one notification.
/// </summary>
/// <remarks>
/// Every list here is refilled from scratch on each refresh, and the obvious way to do that -
/// Clear() then Add() per item - raises one notification per element. A window that rebuilds its
/// controls in response then rebuilds them once per row, and every one of those passes but the last
/// sees a half-filled list: the pass right after Clear() sees an empty one and shows the empty
/// state. On the Live grid, where a rebuild means tearing down and recreating a WebView2 player per
/// camera, that turned a routine 30-second refresh into eight full teardowns of every tile, so live
/// video could never stay up for one refresh interval.
///
/// SwiftUI has no equivalent problem - it diffs a whole array against the previous one by identity
/// - so this is the one place the Windows port needs a mechanism the Apple app doesn't.
/// </remarks>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppress;

    /// <summary>
    /// Replaces the contents and raises exactly one Reset, or nothing at all when the new contents
    /// are already what's here.
    /// </summary>
    /// <remarks>
    /// The equality short-circuit matters as much as the batching does. Most refreshes return the
    /// same rows in the same order, and a Reset a subscriber cannot distinguish from a real change
    /// is a rebuild it will perform for nothing.
    /// </remarks>
    public void ReplaceAll(IEnumerable<T> items)
    {
        var incoming = items as IList<T> ?? items.ToList();
        if (SameAs(incoming)) return;

        _suppress = true;
        try
        {
            Items.Clear();
            foreach (var item in incoming) Items.Add(item);
        }
        finally
        {
            _suppress = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private bool SameAs(IList<T> incoming)
    {
        if (incoming.Count != Count) return false;
        var comparer = EqualityComparer<T>.Default;
        for (var index = 0; index < incoming.Count; index++)
        {
            if (!comparer.Equals(this[index], incoming[index])) return false;
        }
        return true;
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppress) return;
        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppress) return;
        base.OnPropertyChanged(e);
    }
}
