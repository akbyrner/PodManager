using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PodManager.Core;

namespace PodManager.App;

public partial class MainWindow : Window
{
    public MainViewModel Vm { get; }

    public MainWindow()
    {
        InitializeComponent();
        Vm = new MainViewModel(App.Options) { Owner = this };
        DataContext = Vm;

        if (App.Options.Snapshot is not null)
        {
            // Render off-screen without stealing focus.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000;
            Top = 0;
            ShowActivated = false;
            ShowInTaskbar = false;
        }

        Loaded += async (_, _) =>
        {
            await Vm.StartAsync();
            if (App.Options.Snapshot is { } png)
                await SnapshotAsync(png, App.Options.View);
        };
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Vm.SelectedTracks = ((DataGrid)sender).SelectedItems.OfType<Track>().ToList();

    private void SyncTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is SyncNodeVm node && !ReferenceEquals(node, Vm.SelectedSyncNode))
            Vm.SelectedSyncNode = node;
    }

    private void SyncGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        Vm.SelectedSyncItems = ((DataGrid)sender).SelectedItems.OfType<SyncItem>().ToList();

    private void Song_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d) is not null && Vm.EditCommand.CanExecute(null))
            Vm.EditCommand.Execute(null);
    }

    private void Album_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<ListBoxItem>(d) is not null && ((ListBox)sender).SelectedItem is AlbumVm album)
            Vm.OpenAlbumCommand.Execute(album);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // ------------------------------------------------------------ --snapshot (testing)
    private async Task SnapshotAsync(string png, string view)
    {
        try
        {
            var firstAlbum = Vm.AlbumsView.OfType<AlbumVm>().FirstOrDefault();
            switch (view)
            {
                case "album" when firstAlbum is not null:
                    Vm.OpenAlbumCommand.Execute(Vm.AlbumsView.OfType<AlbumVm>().FirstOrDefault(a => a.Tracks.Any(t => t.HasEmbeddedArt)) ?? firstAlbum);
                    break;
                case "edit" when firstAlbum is not null:
                    var dialog = new EditTagsWindow(firstAlbum.Tracks) { Owner = this, Left = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false };
                    dialog.Show();
                    await Task.Delay(1500);
                    Save((FrameworkElement)dialog.Content, png);
                    dialog.Close();
                    Close();
                    return;
                case "songs" or "artists" or "sync" or "log" or "settings":
                    Vm.View = view;
                    break;
                case "settings-end":
                    Vm.View = "settings";
                    Vm.TestConnectionCommand.Execute(null);
                    for (var i = 0; i < 60 && Vm.ConnectionResult.StartsWith("Connecting", StringComparison.Ordinal) || i == 0; i++)
                        await Task.Delay(250);
                    await Task.Delay(300);
                    foreach (var sv in Descendants<ScrollViewer>(Root).Where(s => s.IsVisible)) sv.ScrollToEnd();
                    break;
                case "sync-missing":
                    Vm.View = "sync";
                    Vm.SyncFilter = "missing";
                    break;
            }

            // Let art and the server listing finish (bounded).
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(200);
                var artDone = Vm.AlbumsView.OfType<AlbumVm>().Take(24).All(a => a.ArtLoaded) || view is not ("albums" or "album" or "artists");
                if (!Vm.IsBusy && artDone && i >= 4) break;
            }
            await Task.Delay(300);
            Save(Root, png);
        }
        finally
        {
            Close();
        }
    }

    private static void Save(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(element);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
