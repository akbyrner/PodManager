using System.Windows.Media.Imaging;
using PodManager.Core;

namespace PodManager.App;

public sealed class AlbumVm(string key, string album, string artist, List<Track> tracks) : ObservableObject
{
    public string Key { get; } = key;
    public string Album { get; } = album;
    public string Artist { get; } = artist;
    public List<Track> Tracks { get; } = tracks;

    public uint Year => Tracks.Select(t => t.Year).Where(y => y > 0).DefaultIfEmpty().Max();
    public int TrackCount => Tracks.Count;
    public long Bytes => Tracks.Sum(t => t.Size);
    public TimeSpan Duration => TimeSpan.FromTicks(Tracks.Sum(t => t.Duration.Ticks));
    public bool IsITunes => Tracks.All(t => t.IsITunesManaged);
    public string Quality => string.Join(", ", Tracks.Select(t => t.QualityText).Where(q => q.Length > 0).Distinct().Take(3));

    public string Summary =>
        $"{(Year > 0 ? $"{Year} · " : "")}{TrackCount} song{(TrackCount == 1 ? "" : "s")} · {Format.LongDuration(Duration)} · {Format.Bytes(Bytes)}";

    private BitmapSource? _art;
    private bool _artRequested;

    /// <summary>Album art, loaded in the background the first time anything binds to it.</summary>
    public BitmapSource? Art
    {
        get
        {
            if (!_artRequested)
            {
                _artRequested = true;
                _ = LoadArtAsync();
            }
            return _art;
        }
    }

    public bool ArtLoaded { get; private set; }

    private async Task LoadArtAsync()
    {
        if (!ArtLoader.Cache.TryGetValue(Key, out _art))
        {
            var source = Tracks.FirstOrDefault(t => t.HasEmbeddedArt) ?? Tracks[0];
            _art = await ArtLoader.LoadAsync(source, 320);
            ArtLoader.Cache[Key] = _art;
        }
        ArtLoaded = true;
        OnPropertyChanged(nameof(Art));
    }
}

public sealed class ArtistVm(string name, List<AlbumVm> albums) : ObservableObject
{
    public string Name { get; } = name;
    public List<AlbumVm> Albums { get; } = albums;
    public string Summary => $"{Albums.Count} album{(Albums.Count == 1 ? "" : "s")}, {Albums.Sum(a => a.TrackCount)} songs";
}

/// <summary>A row in the Sync tree: "All Music", an artist, or an album, with its songs rolled up.</summary>
public sealed class SyncNodeVm(string name, string kind, List<SyncItem> items, List<SyncNodeVm> children) : ObservableObject
{
    public string Name { get; } = name;
    /// <summary>"all", "artist" or "album".</summary>
    public string Kind { get; } = kind;
    public List<SyncItem> Items { get; } = items;
    public List<SyncNodeVm> Children { get; } = children;
    public string Path => Kind == "album" ? $"{Items.FirstOrDefault()?.Artist}\u001f{Name}" : Name;

    public int Total => Items.Count(i => i.Status != SyncStatus.OnlyOnIpod);
    public int OnIpod => Items.Count(i => i.Status is SyncStatus.OnIpod or SyncStatus.UpdateReady);
    public int NotSynced => Items.Count(i => i.NeedsSync);
    public int Failed => Items.Count(i => i.Status == SyncStatus.Failed);
    public int Never => Items.Count(i => i.Status == SyncStatus.NeverSync);

    /// <summary>"synced", "partial", "none", "failed" or "never" (drives the dot colour).</summary>
    public string State =>
        Failed > 0 ? "failed" :
        Total > 0 && Never == Total ? "never" :
        NotSynced == 0 ? "synced" :
        OnIpod == 0 ? "none" : "partial";

    public string Counts => Total == 0 ? $"{Items.Count}" : $"{OnIpod}/{Total}";

    public string Tooltip =>
        $"{OnIpod:N0} of {Total:N0} songs on the iPod" + (NotSynced > 0 ? $", {NotSynced:N0} not synced" : "") +
        (Never > 0 ? $", {Never:N0} never sync" : "") + (Failed > 0 ? $", {Failed:N0} failed" : "");

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
}

public sealed record SidebarItem(string Key, string Label, string Icon, string Group);

/// <summary>Loads and downsizes album art off the UI thread, a few at a time (USB reads are the bottleneck).</summary>
public static class ArtLoader
{
    private static readonly SemaphoreSlim Gate = new(3);

    /// <summary>Decoded art by album key; survives view rebuilds. Cleared when a library loads.</summary>
    public static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.Ordinal);

    public static async Task<BitmapSource?> LoadAsync(Track track, int pixelWidth)
    {
        await Gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                var bytes = ArtworkProvider.Load(track);
                return bytes is null ? null : Decode(bytes, pixelWidth);
            });
        }
        finally { Gate.Release(); }
    }

    public static BitmapSource? Decode(byte[] bytes, int pixelWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = pixelWidth;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            return null; // not a usable image
        }
    }
}
