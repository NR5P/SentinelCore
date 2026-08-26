using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SentinelCore.Client;

/// <summary>
/// The smallest useful INotifyPropertyChanged base. Hand-written rather than pulled from a toolkit
/// package: this app needs exactly this much of one, and a dependency for twenty lines is a
/// dependency to keep updated forever.
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises the notification. Virtual so a model whose properties are written from background
    /// work can marshal it somewhere its subscribers can safely handle it - see AppModel, where
    /// every handler is a window.
    /// </summary>
    protected virtual void RaisePropertyChanged(PropertyChangedEventArgs args) =>
        PropertyChanged?.Invoke(this, args);

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        RaisePropertyChanged(new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
