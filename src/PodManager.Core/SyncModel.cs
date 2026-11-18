using System.Text.RegularExpressions;

namespace PodManager.Core;

public enum SyncStatus
{
    /// <summary>On the iPod.</summary>
    OnIpod,
    /// <summary>A newer conversion is waiting on the server (e.g. Lidarr upgraded the master).</summary>
    UpdateReady,
    /// <summary>Converted and waiting in the server outbox.</summary>
    Ready,
    /// <summary>Only the lossless master exists; it'll be converted during the sync.</summary>
    NeedsConversion,
    /// <summary>The last attempt to sync it failed (see Error).</summary>
    Failed,
    /// <summary>Marked "never sync".</summary>
    NeverSync,
    /// <summary>On the iPod but not in the server library (deleted on the server, or added another way).</summary>
    OnlyOnIpod,
}

/// <summary>Everything on the server: lossless masters and converted copies waiting in the outbox.</summary>
public sealed record ServerListing(IReadOnlyList<RemoteFile> Masters, IReadOnlyList<RemoteFile> Outbox);

/// <summary>One song as the sync sees it: where it is on the server, and whether the iPod has it.</summary>
public sealed record SyncItem
{
    /// <summary>Path inside the iPod's Music folder (sanitized), e.g. "Artist\Album (2001)\Artist - Album - 01 - Song.mp3".
    /// Also the key for the never-sync list and the transfer log.</summary>
    public required string Key { get; init; }
    public required string LocalPath { get; init; }
    public string? MasterRel { get; init; }
    public long MasterSize { get; init; }
    public string? OutboxRel { get; init; }
    public long? OutboxSize { get; init; }
    public bool OnIpod { get; init; }
    public long IpodSize { get; init; }
    public bool NeverSync { get; init; }
    public string? Error { get; init; }
    public SyncStatus Status { get; init; }

    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string Title { get; init; } = "";
    public uint TrackNumber { get; init; }
    public uint Year { get; init; }

    /// <summary>Needs copying to make the iPod match the server.</summary>
    public bool NeedsSync => Status is SyncStatus.Ready or SyncStatus.NeedsConversion or SyncStatus.Failed or SyncStatus.UpdateReady;

    /// <summary>Size on the iPod if it's there; otherwise what the copy will weigh (converted size if known, else ~a quarter of the FLAC).</summary>
    public long EstimatedBytes => Status == SyncStatus.OnIpod || Status == SyncStatus.OnlyOnIpod ? IpodSize
        : OutboxSize ?? (MasterSize > 0 ? MasterSize / 4 : 0);
}

public static partial class SyncPlanner
{
    /// <summary>Master path on the server -> path of its converted copy (same rule as the transcoder).</summary>
    public static string OutboxRelFor(string masterRel) => Path.ChangeExtension(masterRel, ".mp3");

    /// <summary>Works out every song's sync state.</summary>
    public static List<SyncItem> Build(ServerListing listing, string ipodRoot, string targetFolder,
        ISet<string> neverSync, IReadOnlyDictionary<string, string> errors)
    {
        var musicRoot = Path.Combine(ipodRoot, targetFolder);
        var outboxByRel = listing.Outbox
            .Where(f => LibraryScanner.AudioExtensions.Contains(Path.GetExtension(f.RelativePath)))
            .ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var items = new Dictionary<string, SyncItem>(StringComparer.OrdinalIgnoreCase);

        SyncItem Make(string key, string? masterRel, long masterSize, RemoteFile? outbox)
        {
            var local = Path.Combine(musicRoot, key);
            var info = new FileInfo(local);
            var onIpod = info.Exists;
            var name = ParseName(outbox?.RelativePath ?? masterRel ?? key.Replace('\\', '/'));
            var never = neverSync.Contains(key);
            errors.TryGetValue(key, out var error);
            var status =
                onIpod && outbox is not null && info.Length != outbox.Size ? SyncStatus.UpdateReady :
                onIpod ? SyncStatus.OnIpod :
                never ? SyncStatus.NeverSync :
                error is not null ? SyncStatus.Failed :
                outbox is not null ? SyncStatus.Ready :
                SyncStatus.NeedsConversion;
            if (status == SyncStatus.UpdateReady && never) status = SyncStatus.OnIpod;
            return new SyncItem
            {
                Key = key, LocalPath = local, MasterRel = masterRel, MasterSize = masterSize,
                OutboxRel = outbox?.RelativePath, OutboxSize = outbox?.Size,
                OnIpod = onIpod, IpodSize = onIpod ? info.Length : 0, NeverSync = never,
                Error = status == SyncStatus.Failed ? error : null, Status = status,
                Artist = name.Artist, Album = name.Album, Title = name.Title, TrackNumber = name.Track, Year = name.Year,
            };
        }

        foreach (var master in listing.Masters)
        {
            var outRel = OutboxRelFor(master.RelativePath);
            var key = Sanitizer.LocalRelative(outRel);
            outboxByRel.Remove(outRel, out var outbox);
            items[key] = Make(key, master.RelativePath, master.Size, outbox);
        }
        // Converted copies whose master is gone still count (they're copyable).
        foreach (var outbox in outboxByRel.Values)
        {
            var key = Sanitizer.LocalRelative(outbox.RelativePath);
            if (!items.ContainsKey(key)) items[key] = Make(key, null, 0, outbox);
        }
        // Songs on the iPod that the server doesn't know about.
        if (Directory.Exists(musicRoot))
        {
            foreach (var file in LibraryScanner.EnumerateAudioFiles(musicRoot))
            {
                var key = Path.GetRelativePath(musicRoot, file);
                if (items.ContainsKey(key)) continue;
                var name = ParseName(key.Replace('\\', '/'));
                items[key] = new SyncItem
                {
                    Key = key, LocalPath = file, OnIpod = true, IpodSize = new FileInfo(file).Length,
                    NeverSync = neverSync.Contains(key), Status = SyncStatus.OnlyOnIpod,
                    Artist = name.Artist, Album = name.Album, Title = name.Title, TrackNumber = name.Track, Year = name.Year,
                };
            }
        }

        return items.Values
            .OrderBy(i => SortName(i.Artist), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.Year).ThenBy(i => i.Album, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.TrackNumber).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static string SortName(string name) =>
        name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) && name.Length > 4 ? name[4..] : name;

    [GeneratedRegex(@"^(?:.*? - )?(?:.*? - )?(\d{1,3}) - (.+)$")]
    private static partial Regex LidarrTrack();   // "Artist - Album - 01 - Title"

    [GeneratedRegex(@"^(\d{1,3})[\s.\-_]+(.+)$")]
    private static partial Regex NumberedTrack(); // "01. Title", "01 - Title"

    [GeneratedRegex(@"^(.*?)\s*\((\d{4})\)\s*$")]
    private static partial Regex AlbumYear();

    /// <summary>Names from a Lidarr-style path: "Artist/Album (2001)/[Disc/]Artist - Album - 01 - Title.ext".</summary>
    public static (string Artist, string Album, string Title, uint Track, uint Year) ParseName(string rel)
    {
        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stem = Path.GetFileNameWithoutExtension(parts[^1]);
        string title = stem;
        uint track = 0, year = 0;
        var m = LidarrTrack().Match(stem);
        if (!m.Success) m = NumberedTrack().Match(stem);
        if (m.Success)
        {
            track = uint.Parse(m.Groups[1].Value);
            title = m.Groups[2].Value;
        }
        var artist = parts.Length >= 2 ? parts[0] : "Unknown Artist";
        var album = parts.Length >= 3 ? parts[1] : "Unknown Album";
        var y = AlbumYear().Match(album);
        if (y.Success)
        {
            album = y.Groups[1].Value;
            year = uint.Parse(y.Groups[2].Value);
        }
        return (artist, album, title, track, year);
    }
}
