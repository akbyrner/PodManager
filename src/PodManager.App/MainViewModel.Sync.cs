using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using PodManager.Core;

namespace PodManager.App;

/// <summary>The Sync and Transfer Log views: what's on the iPod vs the server, and syncing it.</summary>
public sealed partial class MainViewModel
{
    private TransferLog _log = TransferLog.Load();
    private ServerListing? _listing;
    private List<SyncItem> _syncItems = [];
    private bool _syncLoaded;
    private string? _syncError;

    private void InitSyncCommands(Func<object?, bool> ready)
    {
        RefreshSyncCommand = new RelayCommand(_ => Run(() => RefreshSyncAsync(null)), ready);
        SyncAllCommand = new RelayCommand(_ => Run(() => RunSyncAsync(_syncItems.Where(i => i.NeedsSync).ToList(), "Full sync")),
            p => ready(p) && _syncLoaded && CountNeedsSync > 0);
        SyncSelectedCommand = new RelayCommand(p => Run(() => SyncSelectedAsync(p)),
            p => ready(p) && SyncTargets(p).Any(i => i.NeedsSync || i.Status == SyncStatus.NeverSync));
        RetryFailedCommand = new RelayCommand(_ => Run(() => RunSyncAsync(_syncItems.Where(i => i.Status == SyncStatus.Failed).ToList(), "Retry")),
            p => ready(p) && CountFailed > 0);
        NeverSyncCommand = new RelayCommand(p => Run(() => NeverSyncAsync(p)),
            p => ready(p) && SyncTargets(p).Any(i => !i.NeverSync));
        AllowSyncCommand = new RelayCommand(p => Run(() => AllowSyncAsync(p)),
            p => ready(p) && SyncTargets(p).Any(i => i.NeverSync));
        ClearHistoryCommand = new RelayCommand(_ =>
        {
            _log.Runs.Clear();
            _log.Save();
            NotifyLog();
        }, _ => _log.Runs.Count > 0);
    }

    public ICommand RefreshSyncCommand { get; private set; } = null!;
    public ICommand SyncAllCommand { get; private set; } = null!;
    public ICommand SyncSelectedCommand { get; private set; } = null!;
    public ICommand RetryFailedCommand { get; private set; } = null!;
    public ICommand NeverSyncCommand { get; private set; } = null!;
    public ICommand AllowSyncCommand { get; private set; } = null!;
    public ICommand ClearHistoryCommand { get; private set; } = null!;
    public ICommand GoToLogCommand => new RelayCommand(_ => View = "log");

    // ---------------------------------------------------------------- tree, list, filters
    public ObservableCollection<SyncNodeVm> SyncTree { get; } = [];

    private SyncNodeVm? _selectedSyncNode;
    public SyncNodeVm? SelectedSyncNode
    {
        get => _selectedSyncNode;
        set
        {
            _selectedSyncNode = value;
            OnPropertyChanged();
            RebuildSyncList();
        }
    }

    public ListCollectionView SyncTracksView { get; private set; } = new(new List<SyncItem>());

    private IList<SyncItem> _selectedSyncItems = [];
    public IList<SyncItem> SelectedSyncItems
    {
        get => _selectedSyncItems;
        set { _selectedSyncItems = value; OnPropertyChanged(nameof(SyncSelectionText)); }
    }

    private string _syncFilter = "all";
    public string SyncFilter
    {
        get => _syncFilter;
        set
        {
            if (!Set(ref _syncFilter, value)) return;
            foreach (var n in new[] { nameof(FilterAll), nameof(FilterMissing), nameof(FilterOnIpod), nameof(FilterNever), nameof(FilterFailed), nameof(FilterOnlyIpod) })
                OnPropertyChanged(n);
            SyncTracksView.Refresh();
        }
    }

    public bool FilterAll { get => SyncFilter == "all"; set { if (value) SyncFilter = "all"; } }
    public bool FilterMissing { get => SyncFilter == "missing"; set { if (value) SyncFilter = "missing"; } }
    public bool FilterOnIpod { get => SyncFilter == "onipod"; set { if (value) SyncFilter = "onipod"; } }
    public bool FilterNever { get => SyncFilter == "never"; set { if (value) SyncFilter = "never"; } }
    public bool FilterFailed { get => SyncFilter == "failed"; set { if (value) SyncFilter = "failed"; } }
    public bool FilterOnlyIpod { get => SyncFilter == "onlyipod"; set { if (value) SyncFilter = "onlyipod"; } }

    private bool PassesFilter(SyncItem i) => SyncFilter switch
    {
        "missing" => i.NeedsSync,
        "onipod" => i.Status is SyncStatus.OnIpod or SyncStatus.UpdateReady,
        "never" => i.NeverSync,
        "failed" => i.Status == SyncStatus.Failed,
        "onlyipod" => i.Status == SyncStatus.OnlyOnIpod,
        _ => true,
    };

    // ---------------------------------------------------------------- counts & labels
    public int CountAll => _syncItems.Count;
    public int CountOnIpod => _syncItems.Count(i => i.Status is SyncStatus.OnIpod or SyncStatus.UpdateReady);
    public int CountNeedsSync => _syncItems.Count(i => i.NeedsSync);
    public int CountNever => _syncItems.Count(i => i.NeverSync);
    public int CountFailed => _syncItems.Count(i => i.Status == SyncStatus.Failed);
    public int CountOnlyIpod => _syncItems.Count(i => i.Status == SyncStatus.OnlyOnIpod);
    public bool HasFailures => CountFailed > 0;

    public string SyncAllButtonText =>
        CountNeedsSync == 0 ? "Sync" : $"Sync {CountNeedsSync:N0} song{(CountNeedsSync == 1 ? "" : "s")} (~{Format.Bytes(_syncItems.Where(i => i.NeedsSync).Sum(i => i.EstimatedBytes))})";

    public string SyncStatusText =>
        _syncError is not null ? $"Couldn't check the server: {_syncError}" :
        !_syncLoaded ? "Checking what's on the server…" :
        CountNeedsSync == 0
            ? $"Everything on the server is on the iPod ({CountOnIpod:N0} songs)." + (CountNever > 0 ? $" {CountNever:N0} marked never sync." : "")
            : $"{CountNeedsSync:N0} song{(CountNeedsSync == 1 ? "" : "s")} on the server {(CountNeedsSync == 1 ? "isn't" : "aren't")} on the iPod yet. " +
              "Songs that aren't converted yet are converted during the sync; lossless originals always stay on the server.";

    /// <summary>Short version for the bottom bar.</summary>
    public string SyncSummaryShort =>
        !HasLibrary || !_syncLoaded ? "" :
        CountFailed > 0 ? $"{CountFailed:N0} failed to sync" :
        CountNeedsSync > 0 ? $"{CountNeedsSync:N0} not synced" : "In sync with the server";

    public string SyncSelectionText
    {
        get
        {
            var targets = SyncTargets(null);
            if (targets.Count == 0) return "";
            var source = SelectedSyncItems.Count > 0 ? "selected" : SelectedSyncNode?.Kind == "all" ? "in All Music" : $"in {SelectedSyncNode?.Name}";
            var pending = targets.Count(i => i.NeedsSync);
            return $"{targets.Count:N0} song{(targets.Count == 1 ? "" : "s")} {source}" + (pending > 0 ? $" · {pending:N0} not synced" : "");
        }
    }

    /// <summary>What the Sync Selected / Never Sync / Allow Sync buttons act on.</summary>
    private IList<SyncItem> SyncTargets(object? parameter) => parameter switch
    {
        SyncNodeVm node => node.Items,
        SyncItem item => [item],
        _ when SelectedSyncItems.Count > 0 => SelectedSyncItems,
        _ => SelectedSyncNode?.Items ?? [],
    };

    // ---------------------------------------------------------------- transfer log
    public IReadOnlyList<TransferRun> Runs => _log.Runs;

    private TransferRun? _selectedRun;
    public TransferRun? SelectedRun { get => _selectedRun; set => Set(ref _selectedRun, value); }

    public List<LoggedFailure> CurrentErrors => _log.CurrentErrors.Values.OrderByDescending(f => f.Time).ToList();
    public int CurrentErrorCount => _log.CurrentErrors.Count;
    public bool HasLogErrors => CurrentErrorCount > 0;

    public string LogStatusText =>
        CurrentErrorCount == 0
            ? (_log.Runs.Count == 0 ? "No syncs yet." : $"No problems. Last sync {_log.Runs[0].Finished:g}: {_log.Runs[0].Summary}.")
            : $"{CurrentErrorCount:N0} song{(CurrentErrorCount == 1 ? "" : "s")} failed to sync last time {(CurrentErrorCount == 1 ? "it was" : "they were")} tried. " +
              "They stay listed until they sync successfully.";

    private void NotifyLog()
    {
        foreach (var n in new[] { nameof(Runs), nameof(CurrentErrors), nameof(CurrentErrorCount), nameof(HasLogErrors), nameof(LogStatusText) })
            OnPropertyChanged(n);
        SelectedRun = _log.Runs.FirstOrDefault();
    }

    // ================================================================ refresh
    private void ClearSync()
    {
        _listing = null;
        _syncItems = [];
        _syncLoaded = false;
        _syncError = null;
        SyncTree.Clear();
        SyncTracksView = new ListCollectionView(new List<SyncItem>());
        OnPropertyChanged(nameof(SyncTracksView));
        NotifySyncCounts();
    }

    public async Task RefreshSyncAsync(string? finalStatus)
    {
        if (Location is null) return;
        BeginBusy("Checking the server…", cancellable: true);
        try
        {
            _listing = await new ServerSync(SyncSettings.Load()).ListServerAsync(_cts!.Token);
            _syncError = null;
            await RebuildSyncAsync();
            _syncLoaded = true;
            StatusText = finalStatus ?? (CountNeedsSync > 0 ? $"{LibrarySummary} · {SyncSummaryShort}" : LibrarySummary);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped checking the server.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception)
        {
            _syncError = ex.Message;
            StatusText = finalStatus ?? $"Couldn't check the server: {ex.Message}";
        }
        finally
        {
            EndBusy();
            NotifySyncCounts();
            NotifyLog();
        }
    }

    private async Task RebuildSyncAsync()
    {
        if (_listing is null || Location is null) return;
        var root = Location.Root;
        var target = SyncSettings.Load().TargetFolder;
        var listing = _listing;
        var errors = _log.ErrorReasons;
        _syncItems = await Task.Run(() => SyncPlanner.Build(listing, root, target, ExclusionList.Load(root), errors));

        // Drop logged errors for songs that are now fine (e.g. copied some other way) or no longer exist
        // anywhere (nothing left to retry). The run history keeps the record either way.
        var known = _syncItems.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        var resolved = _log.CurrentErrors.Keys
            .Where(k => !known.TryGetValue(k, out var i) || i.Status is SyncStatus.OnIpod or SyncStatus.NeverSync)
            .ToList();
        if (resolved.Count > 0)
        {
            _log.ClearErrors(resolved);
            _log.Save();
        }

        var keepPath = SelectedSyncNode?.Path;
        var keepKind = SelectedSyncNode?.Kind;
        var expanded = SyncTree.SelectMany(r => r.Children).Where(a => a.IsExpanded).Select(a => a.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        var artists = _syncItems
            .GroupBy(i => i.Artist, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new SyncNodeVm(a.First().Artist, "artist", a.ToList(),
                a.GroupBy(i => (i.Album, i.Year))
                 .Select(al => new SyncNodeVm(al.Key.Album + (al.Key.Year > 0 ? $" ({al.Key.Year})" : ""), "album", al.ToList(), []))
                 .ToList())
            { IsExpanded = expanded.Contains(a.First().Artist) })
            .ToList();
        var rootNode = new SyncNodeVm("All Music", "all", _syncItems, artists) { IsExpanded = true };
        SyncTree.Clear();
        SyncTree.Add(rootNode);

        _selectedSyncNode = keepKind switch
        {
            "artist" => artists.FirstOrDefault(a => a.Path == keepPath),
            "album" => artists.SelectMany(a => a.Children).FirstOrDefault(a => a.Path == keepPath),
            _ => null,
        } ?? rootNode;
        OnPropertyChanged(nameof(SelectedSyncNode));
        RebuildSyncList();
        NotifySyncCounts();
    }

    private void RebuildSyncList()
    {
        var source = SelectedSyncNode?.Items ?? _syncItems;
        SyncTracksView = new ListCollectionView(source.ToList())
        {
            Filter = o => o is SyncItem i && PassesFilter(i) && Matches([i.Title, i.Artist, i.Album]),
        };
        OnPropertyChanged(nameof(SyncTracksView));
        SelectedSyncItems = [];
    }

    private void NotifySyncCounts()
    {
        foreach (var n in new[] { nameof(CountAll), nameof(CountOnIpod), nameof(CountNeedsSync), nameof(CountNever), nameof(CountFailed),
                     nameof(CountOnlyIpod), nameof(HasFailures), nameof(SyncAllButtonText), nameof(SyncStatusText), nameof(SyncSummaryShort), nameof(SyncSelectionText) })
            OnPropertyChanged(n);
        CommandManager.InvalidateRequerySuggested();
    }

    // ================================================================ actions
    private async Task SyncSelectedAsync(object? parameter)
    {
        var targets = SyncTargets(parameter).Where(i => i.NeedsSync || i.Status == SyncStatus.NeverSync).ToList();
        if (targets.Count == 0)
        {
            Message("Everything selected is already on the iPod.");
            return;
        }
        var never = targets.Count(i => i.NeverSync);
        if (never > 0 && MessageBox.Show(Owner!,
                $"{never:N0} of the selected songs {(never == 1 ? "is" : "are")} marked \"Never sync\". Sync {(never == 1 ? "it" : "them")} anyway and allow syncing from now on?",
                "Sync Selected", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            targets = targets.Where(i => !i.NeverSync).ToList();
        if (targets.Count > 0) await RunSyncAsync(targets, "Selected");
    }

    private async Task RunSyncAsync(List<SyncItem> targets, string kind)
    {
        if (targets.Count == 0 || Location is null) return;
        var device = Location;
        TransferRun run;
        BeginBusy($"Syncing {targets.Count:N0} song{(targets.Count == 1 ? "" : "s")}…", cancellable: true);
        var progress = new Progress<SyncProgress>(p =>
        {
            if (p.Converting && p.FilesTotal > 0)
            {
                IsIndeterminate = p.FilesDone == 0;
                ProgressValue = 100.0 * p.FilesDone / p.FilesTotal;
                StatusText = p.FilesDone == 0 ? p.Current + "…" : p.Current;
                return;
            }
            if (p.BytesTotal <= 0)
            {
                IsIndeterminate = true;
                StatusText = p.Current + "…";
                return;
            }
            IsIndeterminate = false;
            ProgressValue = 100.0 * p.BytesDone / p.BytesTotal;
            StatusText = $"Copying {p.Current}  ·  {Format.Bytes(p.BytesDone)} of {Format.Bytes(p.BytesTotal)}";
        });
        try
        {
            var engine = new SyncEngine(new ServerSync(SyncSettings.Load()), device.Root, _log);
            var outcome = await engine.SyncAsync(targets, kind, progress, _cts!.Token);
            run = outcome.Run;
        }
        finally { EndBusy(); }

        var status = $"{kind}: {run.Summary}.";
        await LoadAsync(device, status, refreshSync: true); // re-reads the library and refreshes sync status
        NotifyLog();
        if (device.IsDevice) await WriteKnurlIndexAsync(device.Root, status);

        if (run.Failed == 0 && run.Note is null && device.IsDevice && SyncSettings.Load().EjectAfterSync)
        {
            EjectCommand.Execute(null);
            return;
        }

        if (run.Failed > 0 &&
            MessageBox.Show(Owner!, $"{run.Failed:N0} song{(run.Failed == 1 ? "" : "s")} couldn't be synced" +
                                    (run.Note is null ? "." : $": {run.Note}") + "\n\nOpen the Transfer Log to see why?",
                "Sync finished with problems", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            View = "log";
    }

    /// <summary>
    /// Writes .knurl\library.bin and art.bin for the Knurl firmware, so the iPod's own screens always
    /// match what is on it, and creates its battery log if missing. A failure here never fails the sync.
    /// </summary>
    private async Task WriteKnurlIndexAsync(string root, string status)
    {
        var tracks = _all.ToList();
        BeginBusy("Writing the Knurl library index…");
        try
        {
            var r = await Task.Run(() =>
            {
                var written = KnurlIndex.Write(root, tracks, new WpfImageDecoder());
                KnurlDevice.EnsureBatteryLog(root);
                KnurlDevice.EnsureSettings(root);
                return written;
            });
            StatusText = $"{status} Knurl index: {r.Tracks:N0} songs, {r.Albums:N0} albums.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusText = $"{status} Couldn't write the Knurl index: {ex.Message}";
        }
        finally { EndBusy(); }
    }

    private async Task NeverSyncAsync(object? parameter)
    {
        var targets = SyncTargets(parameter).Where(i => !i.NeverSync).ToList();
        if (targets.Count == 0 || Location is null) return;
        var onIpod = targets.Where(i => i.OnIpod).ToList();
        var deleteFromIpod = false;
        if (onIpod.Count > 0)
        {
            var answer = MessageBox.Show(Owner!,
                $"Mark {targets.Count:N0} song{(targets.Count == 1 ? "" : "s")} \"Never sync\"?\n\n" +
                $"{onIpod.Count:N0} of them {(onIpod.Count == 1 ? "is" : "are")} on the iPod now ({Format.Bytes(onIpod.Sum(i => i.IpodSize))}). " +
                "Delete those from the iPod too?\n\nYes = mark and delete from the iPod\nNo = mark only (keep what's on the iPod)",
                "Never Sync", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.Cancel) return;
            deleteFromIpod = answer == MessageBoxResult.Yes;
        }

        var root = Location.Root;
        BeginBusy("Updating never-sync list…");
        try
        {
            await Task.Run(() =>
            {
                ExclusionList.Add(root, targets.Select(i => i.Key));
                if (deleteFromIpod)
                {
                    var tracks = onIpod.Select(i => LibraryScanner.ReadTrack(root, i.LocalPath)).ToList();
                    TrackDeleter.Delete(root, tracks);
                }
            });
            _log.ClearErrors(targets.Select(i => i.Key));
            _log.Save();
            // Converted copies waiting on the server aren't needed any more (masters stay).
            var pending = targets.Where(i => i.OutboxRel is not null).Select(i => new RemoteFile(i.OutboxRel!, i.OutboxSize!.Value)).ToList();
            await new ServerSync(SyncSettings.Load()).RemoveFromServerAsync(pending, CancellationToken.None);
        }
        finally { EndBusy(); }

        var status = $"Marked {targets.Count:N0} song{(targets.Count == 1 ? "" : "s")} never sync" + (deleteFromIpod ? $" and deleted {onIpod.Count:N0} from the iPod." : ".");
        if (deleteFromIpod) await LoadAsync(Location, status);
        else await RefreshSyncAsync(status);
    }

    private async Task AllowSyncAsync(object? parameter)
    {
        var targets = SyncTargets(parameter).Where(i => i.NeverSync).ToList();
        if (targets.Count == 0 || Location is null) return;
        var root = Location.Root;
        await Task.Run(() => ExclusionList.Remove(root, targets.Select(i => i.Key)));
        await RefreshSyncAsync($"{targets.Count:N0} song{(targets.Count == 1 ? "" : "s")} can sync again. Use Sync to copy them.");
    }
}
