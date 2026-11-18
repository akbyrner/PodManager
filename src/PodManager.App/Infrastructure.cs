using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using PodManager.Core;

namespace PodManager.App;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is long b ? Format.Bytes(b) : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class DurationConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is TimeSpan d ? Format.Duration(d) : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class ZeroAsBlankConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is uint u && u == 0 ? "" : value?.ToString() ?? "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class SyncStatusBrushConverter : IValueConverter
{
    private static readonly Dictionary<SyncStatus, System.Windows.Media.Brush> Brushes = new()
    {
        [SyncStatus.OnIpod] = Freeze("#3DAA5C"),
        [SyncStatus.UpdateReady] = Freeze("#3D88D9"),
        [SyncStatus.Ready] = Freeze("#3D88D9"),
        [SyncStatus.NeedsConversion] = Freeze("#E0A030"),
        [SyncStatus.Failed] = Freeze("#D64545"),
        [SyncStatus.NeverSync] = Freeze("#A3AAB3"),
        [SyncStatus.OnlyOnIpod] = Freeze("#8A7DC2"),
    };

    internal static System.Windows.Media.Brush Freeze(string hex)
    {
        var b = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is SyncStatus s ? Brushes[s] : System.Windows.Media.Brushes.Transparent;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class SyncStatusTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        SyncStatus.OnIpod => "On iPod",
        SyncStatus.UpdateReady => "Update ready",
        SyncStatus.Ready => "Ready to sync",
        SyncStatus.NeedsConversion => "Not synced",
        SyncStatus.Failed => "Failed",
        SyncStatus.NeverSync => "Never sync",
        SyncStatus.OnlyOnIpod => "Only on iPod",
        _ => "",
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Dot colour for a Sync tree row ("synced", "partial", "none", "failed", "never").</summary>
public sealed class NodeStateBrushConverter : IValueConverter
{
    private static readonly System.Windows.Media.Brush Synced = SyncStatusBrushConverter.Freeze("#3DAA5C"),
        Partial = SyncStatusBrushConverter.Freeze("#E0A030"), None = SyncStatusBrushConverter.Freeze("#C9CED5"),
        Failed = SyncStatusBrushConverter.Freeze("#D64545"), Never = SyncStatusBrushConverter.Freeze("#A3AAB3");

    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        "synced" => Synced, "partial" => Partial, "failed" => Failed, "never" => Never, _ => None,
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1e9:0.0} GB",
        >= 1_000_000 => $"{bytes / 1e6:0.0} MB",
        >= 1_000 => $"{bytes / 1e3:0} KB",
        _ => $"{bytes} B",
    };

    public static string Duration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{d.Minutes}:{d.Seconds:00}";

    public static string LongDuration(TimeSpan d)
    {
        if (d.TotalHours >= 1) return $"{d.TotalHours:0.0} hours";
        var minutes = d == TimeSpan.Zero ? 0 : Math.Max(1, (int)Math.Round(d.TotalMinutes));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }
}
