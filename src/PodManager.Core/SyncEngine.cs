namespace PodManager.Core;

public sealed record SyncOutcome(TransferRun Run, IReadOnlySet<string> Succeeded);

/// <summary>
/// Runs a sync: converts on the server whatever isn't converted yet, copies to the iPod, verifies,
/// deletes delivered copies from the outbox, and records every failure (with a reason) in the log.
/// </summary>
public sealed class SyncEngine(ServerSync server, string ipodRoot, TransferLog log)
{
    public async Task<SyncOutcome> SyncAsync(IReadOnlyList<SyncItem> requested, string kind,
        IProgress<SyncProgress>? progress, CancellationToken ct = default)
    {
        var started = DateTime.Now;
        var failures = new List<LoggedFailure>();
        var byKey = requested.Where(i => i.NeedsSync || i.Status == SyncStatus.NeverSync)
                             .ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        void Fail(SyncItem item, string reason) => failures.Add(new LoggedFailure(item.Key, Describe(item), reason, DateTime.Now));

        // Explicitly syncing a never-sync song means it's wanted again.
        var unblocked = byKey.Values.Where(i => i.NeverSync).Select(i => i.Key).ToList();
        if (unblocked.Count > 0) ExclusionList.Remove(ipodRoot, unblocked);

        string? note = null;
        int converted = 0, copied = 0;
        long bytes = 0;
        var succeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 1. Convert what isn't in the outbox yet.
            var outboxFor = byKey.Values.Where(i => i.OutboxRel is not null)
                                 .ToDictionary(i => i.Key, i => new RemoteFile(i.OutboxRel!, i.OutboxSize!.Value), StringComparer.OrdinalIgnoreCase);
            var toConvert = byKey.Values.Where(i => i.OutboxRel is null && i.MasterRel is not null).ToList();
            if (toConvert.Count > 0)
            {
                progress?.Report(new SyncProgress(0, byKey.Count, 0, 0, $"Converting {toConvert.Count:N0} song{(toConvert.Count == 1 ? "" : "s")} on the server"));
                var results = await server.ConvertAsync(toConvert.Select(i => i.MasterRel!).ToList(), ct);
                var byMaster = results.ToDictionary(r => r.MasterRel, StringComparer.Ordinal);
                foreach (var item in toConvert)
                {
                    var r = byMaster[item.MasterRel!];
                    if (r.Ok) { outboxFor[item.Key] = new RemoteFile(r.OutboxRel!, r.Size); converted++; }
                    else Fail(item, $"Couldn't convert on the server: {r.Error}");
                }
            }
            foreach (var item in byKey.Values.Where(i => i.OutboxRel is null && i.MasterRel is null))
                Fail(item, "Not on the server.");

            // 2. Album covers sitting in the outbox for the albums being copied (and missing on the iPod).
            var files = outboxFor.Values.ToList();
            var keyByOutbox = outboxFor.ToDictionary(kv => kv.Value.RelativePath, kv => kv.Key, StringComparer.Ordinal);
            if (files.Count > 0)
            {
                var listing = await server.ListServerAsync(ct);
                var dirs = files.Select(f => DirOf(f.RelativePath)).ToHashSet(StringComparer.Ordinal);
                foreach (var cover in listing.Outbox.Where(f => f.RelativePath.EndsWith("/cover.jpg", StringComparison.OrdinalIgnoreCase) && dirs.Contains(DirOf(f.RelativePath))))
                    if (!File.Exists(ServerSync.LocalPath(ipodRoot, server.Settings.TargetFolder, cover)))
                        files.Add(cover);
            }

            // 3. Copy, album by album.
            if (files.Count > 0)
            {
                var albums = files.GroupBy(f => DirOf(f.RelativePath), StringComparer.Ordinal)
                    .Select(g =>
                    {
                        var parts = g.Key.Split('/');
                        return new RemoteAlbum(g.Key, parts[0], parts.Length > 1 ? string.Join(" / ", parts[1..]) : "", g.ToList());
                    }).ToList();
                var result = await server.DownloadAsync(ipodRoot, albums, progress, ct);
                var failedPaths = result.Failed.ToDictionary(f => f.RelativePath, f => f.Reason, StringComparer.Ordinal);
                foreach (var (outRel, key) in keyByOutbox)
                {
                    if (failedPaths.TryGetValue(outRel, out var reason)) Fail(byKey[key], reason);
                    else { succeeded.Add(key); copied++; bytes += outboxFor[key].Size; }
                }
            }
        }
        catch (OperationCanceledException)
        {
            note = "Stopped. Songs that finished copying were kept.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            note = ex.Message;
            foreach (var item in byKey.Values.Where(i => !succeeded.Contains(i.Key) && failures.All(f => f.Key != i.Key)))
                Fail(item, ex.Message);
        }

        // 4. Clean converted copies of songs the iPod already has out of the outbox (full sync only).
        if (kind == "Full sync" && note is null)
        {
            var stale = requested.Where(i => i.Status == SyncStatus.OnIpod && i.OutboxRel is not null && i.OutboxSize == i.IpodSize)
                                 .Select(i => new RemoteFile(i.OutboxRel!, i.OutboxSize!.Value)).ToList();
            try { await server.RemoveFromServerAsync(stale, CancellationToken.None); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
        }

        var run = new TransferRun
        {
            Started = started, Finished = DateTime.Now, Kind = kind, Requested = byKey.Count,
            Converted = converted, Copied = copied, BytesCopied = bytes, Note = note, Failures = failures,
        };
        log.Record(run, succeeded);
        log.Save();
        return new SyncOutcome(run, succeeded);
    }

    private static string DirOf(string rel) => rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : "";

    public static string Describe(SyncItem i) =>
        $"{i.Artist} – {i.Title}" + (string.IsNullOrEmpty(i.Album) ? "" : $" ({i.Album})");
}
