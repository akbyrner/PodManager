using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using PodManager.Core;

namespace PodManager.App;

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly StringComparer Cmp = StringComparer.CurrentCultureIgnoreCase;

    private readonly StartupOptions _options;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private LibraryCache? _cache;
    private List<Track> _all = [];
    private CancellationTokenSource? _cts;

    public MainViewModel(StartupOptions options)
    {
        _options = options;
        SidebarView = new ListCollectionView(SidebarItems.ToList());
        SidebarView.GroupDescriptions!.Add(new PropertyGroupDescription(nameof(SidebarItem.Group)));
        _selectedSidebar = SidebarItems[1];

        bool Ready(object? _) => HasLibrary && !IsBusy;
        RescanCommand = new RelayCommand(_ => Run(() => LoadAsync(Location!)), Ready);
        EjectCommand = new RelayCommand(_ => Eject(), _ => Location?.IsDevice == true && !IsBusy);
        EditCommand = new RelayCommand(p => Run(() => EditAsync(p)), p => Ready(p) && Resolve(p).Count > 0);
        DeleteCommand = new RelayCommand(p => Run(() => DeleteAsync(p)), p => Ready(p) && Resolve(p).Count > 0);
        ShowInExplorerCommand = new RelayCommand(p => { if (Resolve(p).FirstOrDefault() is { } t) ShellActions.ShowInExplorer(t.FullPath); },
            p => Resolve(p).Count > 0);
        OpenAlbumCommand = new RelayCommand(p => { if (p is AlbumVm a) ShowAlbum(a); });
        CloseAlbumCommand = new RelayCommand(_ => OpenAlbum = null);
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => CanCancel);
        InitSyncCommands(Ready);
        OpenSettingsCommand = new RelayCommand(_ => View = "settings");
        InitSettingsCommands();

        _poll.Tick += OnPoll;
    }

    public Window? Owner { get; set; }

    // ---------------------------------------------------------------- commands
    public ICommand RescanCommand { get; }
    public ICommand EjectCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ShowInExplorerCommand { get; }
    public ICommand OpenAlbumCommand { get; }
    public ICommand CloseAlbumCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    // ---------------------------------------------------------------- sidebar & views
    public IReadOnlyList<SidebarItem> SidebarItems { get; } =
    [
        new("songs", "Songs", "", "LIBRARY"),
        new("albums", "Albums", "", "LIBRARY"),
        new("artists", "Artists", "", "LIBRARY"),
        new("sync", "Sync", "", "SERVER"),
        new("log", "Transfer Log", "", "SERVER"),
        new("settings", "Settings", "", "PODMANAGER"),
    ];

    public ListCollectionView SidebarView { get; }

    private SidebarItem? _selectedSidebar;
    public SidebarItem? SelectedSidebar
    {
        get => _selectedSidebar;
        set
        {
            if (Set(ref _selectedSidebar, value) && value is not null)
                View = value.Key;
        }
    }

    private string _view = "albums";
    public string View
    {
        get => _view;
        set
        {
            if (!Set(ref _view, value)) return;
            if (value != "albums") _openAlbum = null;
            SelectedSidebar = SidebarItems.FirstOrDefault(s => s.Key == value);
            NotifyViews();
            if (value == "sync" && !_syncLoaded && HasLibrary && !IsBusy)
                Run(() => RefreshSyncAsync(null));
            if (value == "settings")
            {
                if (!SettingsDirty) LoadSettingsForm();
                RefreshDeviceInfo();
            }
        }
    }

    public bool ShowEmpty => !HasLibrary && View is not ("log" or "settings");
    public bool ShowSongs => HasLibrary && View == "songs";
    public bool ShowAlbums => HasLibrary && View == "albums" && OpenAlbum is null;
    public bool ShowAlbumDetail => HasLibrary && View == "albums" && OpenAlbum is not null;
    public bool ShowArtists => HasLibrary && View == "artists";
    public bool ShowSync => HasLibrary && View == "sync";
    public bool ShowLog => View == "log";
    public bool ShowSettings => View == "settings";
    public bool ShowNoMusic => HasLibrary && _all.Count == 0 && View is "songs" or "albums" or "artists" && !IsBusy;

    private void NotifyViews()
    {
        foreach (var n in new[] { nameof(ShowEmpty), nameof(ShowSongs), nameof(ShowAlbums), nameof(ShowAlbumDetail), nameof(ShowArtists), nameof(ShowSync), nameof(ShowLog), nameof(ShowSettings), nameof(ShowNoMusic), nameof(OpenAlbum), nameof(OpenAlbumTracks) })
            OnPropertyChanged(n);
    }

    // ---------------------------------------------------------------- library data
    public ListCollectionView SongsView { get; private set; } = new(new List<Track>());
    public ListCollectionView AlbumsView { get; private set; } = new(new List<AlbumVm>());
    public ListCollectionView ArtistsView { get; private set; } = new(new List<ArtistVm>());

    public IList<Track> SelectedTracks { get; set; } = [];

    private AlbumVm? _openAlbum;
    public AlbumVm? OpenAlbum
    {
        get => _openAlbum;
        set { _openAlbum = value; NotifyViews(); }
    }

    public List<Track> OpenAlbumTracks =>
        OpenAlbum?.Tracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.DisplayTitle, Cmp).ToList() ?? [];

    private ArtistVm? _selectedArtist;
    public ArtistVm? SelectedArtist { get => _selectedArtist; set => Set(ref _selectedArtist, value); }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!Set(ref _searchText, value)) return;
            SongsView.Refresh();
            AlbumsView.Refresh();
            ArtistsView.Refresh();
            SyncTracksView.Refresh();
        }
    }

    // ---------------------------------------------------------------- device & status
    private LibraryLocation? _location;
    public LibraryLocation? Location
    {
        get => _location;
        private set
        {
            _location = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasLibrary));
            OnPropertyChanged(nameof(DeviceTitle));
            NotifyViews();
        }
    }

    public bool HasLibrary => Location is not null;
    public string DeviceTitle => Location?.Label ?? "No iPod connected";

    private string _statusText = "Looking for your iPod…";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(ShowNoMusic)); }
    }

    public ICommand GoToSyncCommand => new RelayCommand(_ => View = "sync");

    private double _progressValue;
    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

    private bool _isIndeterminate = true;
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }

    private bool _canCancel;
    public bool CanCancel { get => _canCancel; private set => Set(ref _canCancel, value); }

    public string LibrarySummary =>
        HasLibrary
            ? $"{_all.Count:N0} songs · {AlbumsView.Count:N0} albums · {Format.LongDuration(TimeSpan.FromTicks(_all.Sum(t => t.Duration.Ticks)))}"
            : "";

    // ---------------------------------------------------------------- capacity bar
    public GridLength AudioWidth { get; private set; } = new(0, GridUnitType.Star);
    public GridLength OtherWidth { get; private set; } = new(0, GridUnitType.Star);
    public GridLength FreeWidth { get; private set; } = new(1, GridUnitType.Star);
    public string AudioText { get; private set; } = "";
    public string OtherText { get; private set; } = "";
    public string FreeText { get; private set; } = "";
    public string CapacityText { get; private set; } = "";

    private void UpdateStorage()
    {
        if (Location is null)
        {
            AudioWidth = OtherWidth = new GridLength(0, GridUnitType.Star);
            FreeWidth = new GridLength(1, GridUnitType.Star);
            AudioText = OtherText = FreeText = CapacityText = "";
        }
        else
        {
            var (total, free) = IpodLocator.GetSpace(Location.Root);
            var audio = _all.Sum(t => t.Size);
            var other = Math.Max(0, total - free - audio);
            AudioWidth = new GridLength(audio, GridUnitType.Star);
            OtherWidth = new GridLength(other, GridUnitType.Star);
            FreeWidth = new GridLength(Math.Max(free, 1), GridUnitType.Star);
            AudioText = $"Audio {Format.Bytes(audio)}";
            OtherText = $"Other {Format.Bytes(other)}";
            FreeText = $"Free {Format.Bytes(free)}";
            CapacityText = $"Capacity {Format.Bytes(total)}";
        }
        foreach (var n in new[] { nameof(AudioWidth), nameof(OtherWidth), nameof(FreeWidth), nameof(AudioText), nameof(OtherText), nameof(FreeText), nameof(CapacityText), nameof(LibrarySummary) })
            OnPropertyChanged(n);
    }

    // ================================================================ lifecycle
    public async Task StartAsync()
    {
        if (_options.Library is { } folder)
        {
            await LoadAsync(IpodLocator.FromFolder(folder));
            return;
        }
        var found = IpodLocator.FindIpod();
        if (found is not null) await LoadAsync(found);
        else ShowNoDevice("Connect your iPod to see its music.");
        _poll.Start();
    }

    private async void OnPoll(object? sender, EventArgs e)
    {
        if (IsBusy) return;
        try
        {
            var found = IpodLocator.FindIpod();
            if (string.Equals(found?.Root, Location?.Root, StringComparison.OrdinalIgnoreCase)) return;
            if (found is null) ShowNoDevice("iPod disconnected.");
            else await LoadAsync(found);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = ex.Message;
        }
    }

    private void ShowNoDevice(string message)
    {
        Location = null;
        _all = [];
        _cache = null;
        SetViews([]);
        ClearSync();
        UpdateStorage();
        RefreshDeviceInfo();
        StatusText = message;
    }

    public async Task LoadAsync(LibraryLocation location, string? finalStatus = null, bool refreshSync = false)
    {
        BeginBusy("Reading songs…", cancellable: true);
        try
        {
            if (!string.Equals(Location?.Root, location.Root, StringComparison.OrdinalIgnoreCase))
                ArtLoader.Cache.Clear();
            Location = location;
            var ct = _cts!.Token;
            var progress = new Progress<ScanProgress>(p =>
            {
                if (p.Total == 0) return;
                IsIndeterminate = false;
                ProgressValue = 100.0 * p.Done / p.Total;
                StatusText = $"Reading songs… {p.Done:N0} of {p.Total:N0}";
            });
            var (cache, tracks) = await Task.Run(() =>
            {
                // Keyed by capacity, not name, so renaming the iPod doesn't throw away the tag cache.
                var key = location.IsDevice ? $"ipod|{IpodLocator.GetSpace(location.Root).Total}" : location.Root;
                var c = new LibraryCache(LibraryCache.PathFor(key));
                return (c, LibraryScanner.Scan(location.Root, c, progress, ct));
            }, ct);
            _cache = cache;
            _all = tracks;
            SetViews(tracks);
            _syncLoaded = false;
            UpdateStorage();
            StatusText = finalStatus ?? LibrarySummary;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped reading the iPod.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Couldn't read the iPod: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
        // Sync status: on connect if the setting says so, whenever the Sync view is open, and after syncs.
        if (HasLibrary && (refreshSync || View == "sync" || SyncSettings.Load().CheckSyncOnConnect))
            await RefreshSyncAsync(finalStatus);
        else if (HasLibrary)
            ClearSync();
        RefreshDeviceInfo();
    }

    /// <summary>Rebuilds songs/albums/artists from the track list, keeping the open album and artist if they still exist.</summary>
    private void SetViews(List<Track> tracks)
    {
        var keepAlbumPaths = OpenAlbum?.Tracks.Select(t => t.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keepArtist = SelectedArtist?.Name;

        var sorted = tracks
            .OrderBy(t => SortName(t.DisplayAlbumArtist), Cmp).ThenBy(t => SortName(t.DisplayAlbum), Cmp)
            .ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.DisplayTitle, Cmp)
            .ToList();

        var albums = sorted
            .GroupBy(t => (t.DisplayAlbumArtist.ToLowerInvariant(), t.DisplayAlbum.ToLowerInvariant()))
            .Select(g => new AlbumVm($"{g.Key.Item1}\u001f{g.Key.Item2}", g.First().DisplayAlbum, g.First().DisplayAlbumArtist, g.ToList()))
            .ToList();

        var artists = albums
            .GroupBy(a => a.Artist, Cmp)
            .Select(g => new ArtistVm(g.First().Artist, g.ToList()))
            .ToList();

        SongsView = new ListCollectionView(sorted) { Filter = o => Matches(o is Track t ? [t.DisplayTitle, t.Artist, t.AlbumArtist, t.Album, t.Genre] : []) };
        AlbumsView = new ListCollectionView(albums) { Filter = o => Matches(o is AlbumVm a ? [a.Album, a.Artist] : []) };
        ArtistsView = new ListCollectionView(artists) { Filter = o => Matches(o is ArtistVm a ? [a.Name] : []) };
        OnPropertyChanged(nameof(SongsView));
        OnPropertyChanged(nameof(AlbumsView));
        OnPropertyChanged(nameof(ArtistsView));

        _openAlbum = keepAlbumPaths is null ? null : albums.FirstOrDefault(a => a.Tracks.Any(t => keepAlbumPaths.Contains(t.FullPath)));
        SelectedArtist = artists.FirstOrDefault(a => Cmp.Equals(a.Name, keepArtist)) ?? artists.FirstOrDefault();
        SelectedTracks = [];
        NotifyViews();
        OnPropertyChanged(nameof(LibrarySummary));
    }

    /// <summary>iTunes-style sort: "The Harbor Lights" files under H.</summary>
    private static string SortName(string name) =>
        name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && name.Length > 4 ? name[4..] : name;

    private bool Matches(string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        return SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(term => fields.Any(f => f?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true));
    }

    private void ShowAlbum(AlbumVm album)
    {
        View = "albums";
        OpenAlbum = album;
    }

    /// <summary>What a command acts on: an album, an artist, or the selected songs.</summary>
    private IList<Track> Resolve(object? parameter) => parameter switch
    {
        AlbumVm a => a.Tracks,
        ArtistVm a => a.Albums.SelectMany(x => x.Tracks).ToList(),
        Track t => [t],
        _ => SelectedTracks,
    };

    // ================================================================ edit
    private async Task EditAsync(object? parameter)
    {
        var targets = Resolve(parameter);
        var editable = targets.Where(t => !t.IsITunesManaged).ToList();
        if (editable.Count == 0)
        {
            Message("Songs added by iTunes can only be changed in iTunes.");
            return;
        }

        var dialog = new EditTagsWindow(editable) { Owner = Owner };
        if (dialog.ShowDialog() != true || dialog.Result is not { IsEmpty: false } edit) return;

        BeginBusy($"Saving {editable.Count:N0} song{(editable.Count == 1 ? "" : "s")}…");
        try
        {
            var root = Location!.Root;
            var results = await Task.Run(() => editable.Select(t =>
            {
                try { return (Old: t, New: TagEditor.Apply(root, t, edit), Error: (string?)null); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return (Old: t, New: t, Error: ex.Message); }
            }).ToList());

            var byPath = results.ToDictionary(r => r.Old.FullPath, r => r.New, StringComparer.OrdinalIgnoreCase);
            _all = _all.Select(t => byPath.TryGetValue(t.FullPath, out var updated) ? updated : t).ToList();
            if (edit.Artwork is not null || edit.RemoveArtwork || edit.Album is not null || edit.Artist is not null || edit.AlbumArtist is not null)
                ArtLoader.Cache.Clear();
            await Task.Run(() => _cache?.Replace(_all));
            SetViews(_all);

            var failed = results.Where(r => r.Error is not null).ToList();
            var skipped = targets.Count - editable.Count;
            StatusText = (failed.Count == 0
                    ? $"Saved changes to {results.Count:N0} song{(results.Count == 1 ? "" : "s")}."
                    : $"Saved {results.Count - failed.Count:N0}; {failed.Count:N0} failed ({failed[0].Error}).")
                + (skipped > 0 ? $" {skipped} iTunes song{(skipped == 1 ? " was" : "s were")} left alone." : "");
        }
        finally { EndBusy(); }
    }

    // ================================================================ delete
    private async Task DeleteAsync(object? parameter)
    {
        var targets = Resolve(parameter);
        var deletable = targets.Where(t => !t.IsITunesManaged).ToList();
        if (deletable.Count == 0)
        {
            Message("Songs added by iTunes can only be removed with iTunes.");
            return;
        }

        var what = deletable.Count == 1 ? $"\"{deletable[0].DisplayTitle}\"" : $"{deletable.Count:N0} songs";
        var itunesNote = targets.Count > deletable.Count
            ? $"\n\n{targets.Count - deletable.Count} song(s) added by iTunes will be left alone." : "";
        var answer = MessageBox.Show(Owner!,
            $"Delete {what} ({Format.Bytes(deletable.Sum(t => t.Size))}) from the iPod?{itunesNote}\n\n" +
            "The files are removed from the iPod permanently and marked \"Never sync\", so a full sync won't bring them back. " +
            "The lossless originals stay on the server. To put them back later, find them in Sync and choose Allow Sync, then sync.",
            "Delete from iPod", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        BeginBusy("Deleting…");
        try
        {
            var root = Location!.Root;
            var target = SyncSettings.Load().TargetFolder;
            var result = await Task.Run(() =>
            {
                var r = TrackDeleter.Delete(root, deletable);
                var failed = r.Failures.Select(f => f.Track.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var gone = deletable.Where(t => !failed.Contains(t.FullPath)).ToList();
                ExclusionList.Add(root, gone.Select(t => ExclusionList.KeyFor(t, target)).OfType<string>());
                return (Result: r, Gone: gone.Select(t => t.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase));
            });

            _all = _all.Where(t => !result.Gone.Contains(t.FullPath)).ToList();
            await Task.Run(() => _cache?.Replace(_all));
            SetViews(_all);
            UpdateStorage();
            _log.ClearErrors(result.Gone.Select(p => Path.GetRelativePath(Path.Combine(root, target), p)));
            _log.Save();
            await RefreshSyncAsync(null);

            var r = result.Result;
            StatusText = $"Deleted {r.Deleted:N0} song{(r.Deleted == 1 ? "" : "s")}, freed {Format.Bytes(r.BytesFreed)}." +
                         (r.Failures.Count > 0 ? $" {r.Failures.Count} couldn't be deleted ({r.Failures[0].Reason})." : "");
        }
        finally { EndBusy(); }
    }

    // ================================================================ eject
    private void Eject()
    {
        if (Location is not { IsDevice: true } device) return;
        try
        {
            ShellActions.Eject(device.Root);
            StatusText = "Ejecting…";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            StatusText = $"Couldn't eject: {ex.Message}";
            return;
        }

        var check = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            if (IpodLocator.IsIpodRoot(device.Root))
                StatusText = "The iPod is still connected. Close anything that's using it and try again.";
            else
                ShowNoDevice("iPod ejected. You can unplug it.");
        };
        check.Start();
    }

    // ================================================================ helpers
    private void BeginBusy(string status, bool cancellable = false)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        CanCancel = cancellable;
        IsIndeterminate = true;
        ProgressValue = 0;
        StatusText = status;
        CommandManager.InvalidateRequerySuggested();
    }

    private void EndBusy()
    {
        IsBusy = false;
        CanCancel = false;
        _cts?.Dispose();
        _cts = null;
        CommandManager.InvalidateRequerySuggested();
    }

    private void Message(string text) =>
        MessageBox.Show(Owner!, text, "PodManager", MessageBoxButton.OK, MessageBoxImage.Information);

    private async void Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            EndBusy();
            StatusText = ex.Message;
        }
    }
}
