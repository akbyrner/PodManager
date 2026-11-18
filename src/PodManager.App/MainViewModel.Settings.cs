using System.ComponentModel;
using System.Reflection;
using System.Windows.Input;
using PodManager.Core;

namespace PodManager.App;

/// <summary>The Settings view: the iPod's name and details, sync options, and the server connection.</summary>
public sealed partial class MainViewModel
{
    private void InitSettingsCommands()
    {
        SaveSettingsCommand = new RelayCommand(_ => Run(SaveSettingsAsync), _ => SettingsDirty && !IsBusy);
        RevertSettingsCommand = new RelayCommand(_ => LoadSettingsForm(), _ => SettingsDirty);
        RestoreDefaultsCommand = new RelayCommand(_ => FillForm(new SyncSettings()));
        TestConnectionCommand = new RelayCommand(_ => Run(TestConnectionAsync), _ => !_testing);
        RenameIpodCommand = new RelayCommand(_ => Run(RenameIpodAsync),
            _ => Location is { IsDevice: true } && !IsBusy && NewIpodName.Trim() != (Location?.Label ?? ""));
        OpenDataFolderCommand = new RelayCommand(_ =>
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            ShellActions.OpenFolder(AppPaths.DataDirectory);
        });
        ClearCacheCommand = new RelayCommand(_ => Run(ClearCacheAsync), _ => !IsBusy);
        LoadSettingsForm();
    }

    public ICommand SaveSettingsCommand { get; private set; } = null!;
    public ICommand RevertSettingsCommand { get; private set; } = null!;
    public ICommand RestoreDefaultsCommand { get; private set; } = null!;
    public ICommand TestConnectionCommand { get; private set; } = null!;
    public ICommand RenameIpodCommand { get; private set; } = null!;
    public ICommand OpenDataFolderCommand { get; private set; } = null!;
    public ICommand ClearCacheCommand { get; private set; } = null!;

    // ---------------------------------------------------------------- form fields (edited copy of settings.json)
    private SyncSettings _saved = new();
    private bool _loadingForm;

    private string _setRemote = "", _setLibraryRoot = "", _setRemoteRoot = "", _setTargetFolder = "", _setContainer = "";
    private bool _setDeleteAfterTransfer, _setCheckOnConnect, _setEjectAfterSync;

    public string SetRemote { get => _setRemote; set => Field(ref _setRemote, value); }
    public string SetLibraryRoot { get => _setLibraryRoot; set => Field(ref _setLibraryRoot, value); }
    public string SetRemoteRoot { get => _setRemoteRoot; set => Field(ref _setRemoteRoot, value); }
    public string SetTargetFolder { get => _setTargetFolder; set => Field(ref _setTargetFolder, value); }
    public string SetContainer { get => _setContainer; set => Field(ref _setContainer, value); }
    public bool SetDeleteAfterTransfer { get => _setDeleteAfterTransfer; set => Field(ref _setDeleteAfterTransfer, value); }
    public bool SetCheckOnConnect { get => _setCheckOnConnect; set => Field(ref _setCheckOnConnect, value); }
    public bool SetEjectAfterSync { get => _setEjectAfterSync; set => Field(ref _setEjectAfterSync, value); }

    private void Field<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name) || _loadingForm) return;
        SettingsMessage = "";
        OnPropertyChanged(nameof(SettingsDirty));
        OnPropertyChanged(nameof(OutboxDeleteWarning));
        CommandManager.InvalidateRequerySuggested();
    }

    private SyncSettings FormSettings() => _saved with
    {
        Remote = SetRemote.Trim(),
        LibraryRoot = SetLibraryRoot.Trim(),
        RemoteRoot = SetRemoteRoot.Trim(),
        TargetFolder = SetTargetFolder.Trim(),
        TranscoderContainer = SetContainer.Trim(),
        DeleteAfterTransfer = SetDeleteAfterTransfer,
        CheckSyncOnConnect = SetCheckOnConnect,
        EjectAfterSync = SetEjectAfterSync,
    };

    public bool SettingsDirty => FormSettings() != _saved;

    /// <summary>Shown when deletion is on but the outbox path won't allow it (the safety guard).</summary>
    public string OutboxDeleteWarning =>
        SetDeleteAfterTransfer && !FormSettings().CanDelete
            ? "Delete-after-transfer only works on a folder named ipod-outbox (a safety guard so the lossless library can never be deleted from). With this path, synced files will stay on the server."
            : "";

    private string _settingsMessage = "";
    public string SettingsMessage { get => _settingsMessage; private set => Set(ref _settingsMessage, value); }

    private bool _settingsMessageIsError;
    public bool SettingsMessageIsError { get => _settingsMessageIsError; private set => Set(ref _settingsMessageIsError, value); }

    private void LoadSettingsForm() => FillForm(SyncSettings.Load(), markSaved: true);

    private void FillForm(SyncSettings s, bool markSaved = false)
    {
        if (markSaved) _saved = s;
        _loadingForm = true;
        SetRemote = s.Remote;
        SetLibraryRoot = s.LibraryRoot;
        SetRemoteRoot = s.RemoteRoot;
        SetTargetFolder = s.TargetFolder;
        SetContainer = s.TranscoderContainer;
        SetDeleteAfterTransfer = s.DeleteAfterTransfer;
        SetCheckOnConnect = s.CheckSyncOnConnect;
        SetEjectAfterSync = s.EjectAfterSync;
        _loadingForm = false;
        if (markSaved) SettingsMessage = "";
        OnPropertyChanged(nameof(SettingsDirty));
        OnPropertyChanged(nameof(OutboxDeleteWarning));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ShowSettingsMessage(string text, bool error = false)
    {
        SettingsMessageIsError = error;
        SettingsMessage = text;
    }

    private async Task SaveSettingsAsync()
    {
        var s = FormSettings();
        var problems = s.Validate();
        if (problems.Count > 0)
        {
            ShowSettingsMessage(string.Join("\n", problems), error: true);
            return;
        }
        var serverChanged = s.Remote != _saved.Remote || s.LibraryRoot != _saved.LibraryRoot || s.RemoteRoot != _saved.RemoteRoot
                            || s.TargetFolder != _saved.TargetFolder || s.TranscoderContainer != _saved.TranscoderContainer;
        try { s.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowSettingsMessage($"Couldn't save settings: {ex.Message}", error: true);
            return;
        }
        _saved = s;
        OnPropertyChanged(nameof(SettingsDirty));
        ShowSettingsMessage("Settings saved.");
        if (serverChanged && HasLibrary)
        {
            _syncLoaded = false;
            await RefreshSyncAsync("Settings saved. Checked the server with the new server settings.");
        }
    }

    // ---------------------------------------------------------------- test connection
    private bool _testing;
    private string _connectionResult = "";
    public string ConnectionResult { get => _connectionResult; private set => Set(ref _connectionResult, value); }
    private bool _connectionOk;
    public bool ConnectionOk { get => _connectionOk; private set => Set(ref _connectionOk, value); }

    private async Task TestConnectionAsync()
    {
        var s = FormSettings();
        var problems = s.Validate();
        if (problems.Count > 0)
        {
            ConnectionOk = false;
            ConnectionResult = problems[0];
            return;
        }
        _testing = true;
        ConnectionResult = $"Connecting to {s.Remote}…";
        try
        {
            var server = new ServerSync(s);
            var listing = await server.ListServerAsync();
            string converter;
            try { converter = await server.CheckTranscoderAsync(); }
            catch (InvalidOperationException ex) { converter = ex.Message; }
            ConnectionOk = true;
            ConnectionResult = $"Connected. Library: {listing.Masters.Count:N0} lossless songs. Outbox: {listing.Outbox.Count:N0} files waiting. " +
                               $"Converter: {converter}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception)
        {
            ConnectionOk = false;
            ConnectionResult = ex.Message;
        }
        finally
        {
            _testing = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ---------------------------------------------------------------- iPod name & details
    private string _newIpodName = "";
    public string NewIpodName
    {
        get => _newIpodName;
        set
        {
            if (!Set(ref _newIpodName, value)) return;
            OnPropertyChanged(nameof(IpodNameProblem));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string IpodNameProblem =>
        string.IsNullOrEmpty(NewIpodName) || NewIpodName == Location?.Label ? "" : IpodName.Problem(NewIpodName.Trim()) ?? "";

    private async Task RenameIpodAsync()
    {
        if (Location is not { IsDevice: true } device) return;
        var name = NewIpodName.Trim();
        if (IpodName.Problem(name) is { } problem)
        {
            ShowSettingsMessage(problem, error: true);
            return;
        }
        try
        {
            await Task.Run(() => ShellActions.SetVolumeLabel(device.Root, name));
        }
        catch (Win32Exception ex)
        {
            ShowSettingsMessage($"Couldn't rename the iPod: {ex.Message}", error: true);
            return;
        }
        var actual = new DriveInfo(device.Root).VolumeLabel;
        Location = device with { Label = string.IsNullOrWhiteSpace(actual) ? name : actual };
        NewIpodName = Location.Label;
        RefreshDeviceInfo();
        ShowSettingsMessage(actual == name ? $"The iPod is now called \"{actual}\"." : $"The iPod is now called \"{actual}\" (Windows stores FAT32 names this way).");
        StatusText = $"Renamed the iPod to \"{Location.Label}\".";
    }

    public string DeviceModel { get; private set; } = "";
    public string DeviceFirmware { get; private set; } = "";
    public string DeviceDrive { get; private set; } = "";
    public string DeviceSpace { get; private set; } = "";
    public bool HasDevice => Location is { IsDevice: true };
    public string AppVersion => $"PodManager {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
    public string DataFolder => AppPaths.DataDirectory;

    private void RefreshDeviceInfo()
    {
        if (Location is { } loc)
        {
            var knurl = KnurlDevice.Describe(loc.Root);
            DeviceModel = knurl is null ? "Unknown" : "iPod Video (5th generation)";   // the only model Knurl runs on
            DeviceFirmware = knurl ?? @"No Knurl firmware (.knurl\knurl.ipod) on this drive";
            DeviceDrive = loc.Root;
            var (total, free) = IpodLocator.GetSpace(loc.Root);
            DeviceSpace = $"{Format.Bytes(free)} free of {Format.Bytes(total)}";
            if (string.IsNullOrEmpty(NewIpodName) || !NewIpodName.Equals(loc.Label, StringComparison.Ordinal)) NewIpodName = loc.Label;
        }
        else
        {
            DeviceModel = DeviceFirmware = DeviceDrive = DeviceSpace = "";
            NewIpodName = "";
        }
        foreach (var n in new[] { nameof(DeviceModel), nameof(DeviceFirmware), nameof(DeviceDrive), nameof(DeviceSpace), nameof(HasDevice) })
            OnPropertyChanged(n);
    }

    // ---------------------------------------------------------------- advanced
    private async Task ClearCacheAsync()
    {
        var removed = 0;
        foreach (var f in Directory.Exists(AppPaths.DataDirectory) ? Directory.GetFiles(AppPaths.DataDirectory, "cache-*.json") : [])
        {
            try { File.Delete(f); removed++; } catch (IOException) { }
        }
        ArtLoader.Cache.Clear();
        ShowSettingsMessage($"Cleared {removed} tag cache file{(removed == 1 ? "" : "s")}." + (HasLibrary ? " Re-reading the iPod…" : ""));
        if (Location is { } loc) await LoadAsync(loc, "Tag cache cleared; re-read every song on the iPod.");
    }
}
