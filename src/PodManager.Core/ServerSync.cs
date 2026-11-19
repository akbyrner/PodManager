using System.Text;
using System.Text.Json;

namespace PodManager.Core;

public sealed record SyncSettings
{
    /// <summary>SSH target for the server (key-based login; PodManager never asks for a password).</summary>
    public string Remote { get; init; } = "user@server";

    /// <summary>The outbox of iPod-ready MP3s made by the ipod-transcoder container (not the lossless library).</summary>
    public string RemoteRoot { get; init; } = "/srv/music/ipod-outbox";
    public string TargetFolder { get; init; } = "Music";

    /// <summary>The lossless masters (read-only to PodManager: only listed, never changed).</summary>
    public string LibraryRoot { get; init; } = "/srv/music/library";

    /// <summary>Docker container that converts masters for the iPod (transcoder.py convert ...).</summary>
    public string TranscoderContainer { get; init; } = "ipod-transcoder";

    /// <summary>Delete files from the outbox once they're verified on the iPod.</summary>
    public bool DeleteAfterTransfer { get; init; } = true;

    /// <summary>Check what's on the server as soon as the iPod connects (otherwise only when Sync is opened).</summary>
    public bool CheckSyncOnConnect { get; init; } = true;

    /// <summary>Eject the iPod when a sync finishes with no problems.</summary>
    public bool EjectAfterSync { get; init; }

    /// <summary>Hard guard: deletion only ever happens in a folder named ipod-outbox, never the lossless library.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanDelete => DeleteAfterTransfer && RemoteRoot.TrimEnd('/').EndsWith("/ipod-outbox", StringComparison.Ordinal);

    public static string FilePath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    /// <summary>Loads settings.json, writing the defaults the first time so they're easy to find and edit.</summary>
    public static SyncSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<SyncSettings>(File.ReadAllText(FilePath)) ?? new();
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new SyncSettings(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Problems that would stop syncing from working (empty = fine).</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(Remote) || Remote.Contains(' '))
            problems.Add("Server address must look like user@host (no spaces).");
        foreach (var (label, path) in new[] { ("Lossless library", LibraryRoot), ("iPod outbox", RemoteRoot) })
            if (!path.StartsWith('/') || path.Contains('\'') || path.Contains('"'))
                problems.Add($"{label} must be an absolute server path (starting with /) without quotes.");
        if (string.Equals(LibraryRoot.TrimEnd('/'), RemoteRoot.TrimEnd('/'), StringComparison.Ordinal))
            problems.Add("The outbox can't be the lossless library: synced files are deleted from the outbox.");
        if (string.IsNullOrWhiteSpace(TargetFolder) || TargetFolder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            problems.Add("iPod music folder must be a plain folder name, e.g. Music.");
        if (string.IsNullOrWhiteSpace(TranscoderContainer) || TranscoderContainer.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')))
            problems.Add("Converter container must be a Docker container name, e.g. ipod-transcoder.");
        return problems;
    }
}

/// <summary>The iPod's name: its FAT32 volume label (what Windows and iTunes show).</summary>
public static class IpodName
{
    public const int MaxLength = 11;
    private static readonly char[] Forbidden = ['*', '?', '/', '\\', '|', ',', ';', ':', '+', '=', '<', '>', '[', ']', '"', '.'];

    /// <summary>Why a name can't be used, or null if it's fine.</summary>
    public static string? Problem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the iPod a name.";
        if (name.Length > MaxLength) return $"iPod names can be at most {MaxLength} characters (a FAT32 limit). This one is {name.Length}.";
        var bad = name.Where(c => Forbidden.Contains(c) || c < 32).Distinct().ToArray();
        if (bad.Length > 0) return $"These characters aren't allowed in an iPod name: {string.Join(" ", bad)}";
        if (name.Any(c => c > 127)) return "Use plain letters, numbers, spaces and simple punctuation (FAT32 names can't hold accents or emoji).";
        return null;
    }
}

/// <summary>A file on the server, path relative to RemoteRoot with '/'.</summary>
public sealed record RemoteFile(string RelativePath, long Size);

public sealed record RemoteAlbum(string Key, string Artist, string Album, IReadOnlyList<RemoteFile> Files)
{
    public long TotalBytes => Files.Sum(f => f.Size);
}

/// <param name="Converting">True while the server is converting; FilesDone/FilesTotal then count songs converted.</param>
public sealed record SyncProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, string Current, bool Converting = false);

/// <summary>A file that didn't make it, and why (path relative to the outbox).</summary>
public sealed record TransferFailure(string RelativePath, string Reason);

public sealed record SyncResult(int Copied, long BytesCopied, IReadOnlyList<TransferFailure> Failed);

public sealed record ConvertResult(string MasterRel, bool Ok, string? OutboxRel, long Size, string? Error);

public sealed class ServerSync(SyncSettings settings)
{
    public SyncSettings Settings { get; } = settings;

    private static readonly string[] MasterPatterns = ["*.flac", "*.m4a", "*.alac", "*.wav", "*.aif", "*.aiff", "*.ape", "*.wv"];

    /// <summary>Lists the lossless library and the outbox in one SSH call.</summary>
    public async Task<ServerListing> ListServerAsync(CancellationToken ct = default)
    {
        var names = string.Join(" -o ", MasterPatterns.Select(p => $"-iname '{p}'"));
        var command =
            $"cd {ShellQuote(Settings.LibraryRoot)} && find . -type f ! -path '*/.*' \\( {names} \\) -printf 'M\\t%P\\t%s\\n'; " +
            $"cd {ShellQuote(Settings.RemoteRoot)} && find . -type f ! -name '.*' -printf 'O\\t%P\\t%s\\n'";
        var result = await ProcessRunner.RunAsync("ssh",
            ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", Settings.Remote, command], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Couldn't reach the server ({Settings.Remote}): {FirstLine(result.StdErr)}");
        return ParseServerListing(result.StdOut);
    }

    /// <summary>Asks the converter for its status ("masters: N  converted: N ..."); throws if it can't be reached.</summary>
    public async Task<string> CheckTranscoderAsync(CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync("ssh",
            ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", Settings.Remote,
             $"docker exec {ShellQuote(Settings.TranscoderContainer)} python3 transcoder.py status"], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Converter not reachable: {FirstLine(result.StdErr)}");
        return string.Join(" · ", result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    internal static ServerListing ParseServerListing(string output)
    {
        var masters = new List<RemoteFile>();
        var outbox = new List<RemoteFile>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 4 || line[1] != '\t') continue;
            var rest = line[2..];
            var tab = rest.LastIndexOf('\t');
            if (tab <= 0 || !long.TryParse(rest.AsSpan(tab + 1), out var size)) continue;
            var file = new RemoteFile(rest[..tab], size);
            if (line[0] == 'M') masters.Add(file);
            else if (line[0] == 'O') outbox.Add(file);
        }
        return new ServerListing(masters, outbox);
    }

    /// <summary>
    /// Has the server convert these masters now (even if they were delivered before), via
    /// "docker exec ipod-transcoder python3 transcoder.py convert". Returns one result per master.
    /// <paramref name="onSongDone"/> is called as each song finishes (the converter prints one line per song).
    /// </summary>
    public async Task<List<ConvertResult>> ConvertAsync(IReadOnlyList<string> masterRels, CancellationToken ct = default,
        Action<ConvertResult>? onSongDone = null)
    {
        var results = new List<ConvertResult>();
        foreach (var chunk in masterRels.Chunk(100))
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(chunk)));
            var command = $"docker exec {ShellQuote(Settings.TranscoderContainer)} python3 transcoder.py convert {payload}";
            var run = await ProcessRunner.RunAsync("ssh",
                ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", Settings.Remote, command], ct,
                onSongDone is null ? null : line => { if (ParseConvertLine(line) is { } r) onSongDone(r); });
            var parsed = ParseConvertOutput(run.StdOut);
            foreach (var rel in chunk)
            {
                results.Add(parsed.TryGetValue(rel, out var r) ? r
                    : new ConvertResult(rel, false, null, 0,
                        run.ExitCode != 0 ? $"converter didn't run: {FirstLine(run.StdErr)}" : "converter gave no answer for this file"));
            }
        }
        return results;
    }

    internal static Dictionary<string, ConvertResult> ParseConvertOutput(string output)
    {
        var map = new Dictionary<string, ConvertResult>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
            if (ParseConvertLine(line) is { } r) map[r.MasterRel] = r;
        return map;
    }

    /// <summary>One line of converter output, or null if the line isn't a result.</summary>
    internal static ConvertResult? ParseConvertLine(string line)
    {
        if (!line.TrimStart().StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var rel = root.GetProperty("rel").GetString()!;
            return root.GetProperty("ok").GetBoolean()
                ? new ConvertResult(rel, true, root.GetProperty("out").GetString(), root.GetProperty("size").GetInt64(), null)
                : new ConvertResult(rel, false, null, 0, root.TryGetProperty("error", out var e) ? e.GetString() : "conversion failed");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no details";

    public async Task<List<RemoteFile>> ListRemoteAsync(CancellationToken ct = default)
    {
        var command = $"cd {ShellQuote(Settings.RemoteRoot)} && find . -type f ! -name '.*' -printf '%P\\t%s\\n'";
        var result = await ProcessRunner.RunAsync("ssh",
            ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", Settings.Remote, command], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"Couldn't list {Settings.Remote}:{Settings.RemoteRoot} ({result.StdErr.Trim()})");
        return ParseListing(result.StdOut);
    }

    internal static List<RemoteFile> ParseListing(string output)
    {
        var files = new List<RemoteFile>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.LastIndexOf('\t');
            if (tab <= 0 || !long.TryParse(line.AsSpan(tab + 1), out var size)) continue;
            files.Add(new RemoteFile(line[..tab], size));
        }
        return files;
    }

    /// <summary>Server files the iPod doesn't have (or has at a different size), grouped by album folder.</summary>
    public static List<RemoteAlbum> FindMissing(IEnumerable<RemoteFile> remote, string libraryRoot, string targetFolder)
    {
        var missing = remote.Where(f =>
        {
            var local = LocalPath(libraryRoot, targetFolder, f);
            var info = new FileInfo(local);
            return !info.Exists || info.Length != f.Size;
        });

        return missing
            .GroupBy(f => f.RelativePath.Contains('/') ? f.RelativePath[..f.RelativePath.LastIndexOf('/')] : "",
                     StringComparer.Ordinal)
            .Select(g =>
            {
                var parts = g.Key.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var artist = parts.Length >= 1 ? parts[0] : "(top level)";
                var album = parts.Length >= 2 ? string.Join(" / ", parts[1..]) : "(not in an album folder)";
                return new RemoteAlbum(g.Key, artist, album, g.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ToList());
            })
            .OrderBy(a => a.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Album, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static string LocalPath(string libraryRoot, string targetFolder, RemoteFile f) =>
        Path.Combine(libraryRoot, targetFolder, Sanitizer.LocalRelative(f.RelativePath));

    /// <summary>
    /// Copies the albums to the iPod, one sftp session per album. Files download as *.part and are
    /// renamed only when complete, so an interrupted copy never leaves a truncated song behind.
    /// </summary>
    public async Task<SyncResult> DownloadAsync(string libraryRoot, IReadOnlyList<RemoteAlbum> albums,
        IProgress<SyncProgress>? progress, CancellationToken ct = default)
    {
        using var syncLock = NamedLock.TryAcquire(NamedLock.IpodSyncName)
            ?? throw new InvalidOperationException("Another sync is copying to the iPod right now. Try again when it finishes.");

        var all = albums.SelectMany(a => a.Files).ToList();
        long total = all.Sum(f => f.Size), done = 0, bytesCopied = 0;
        var (_, free) = IpodLocator.GetSpace(libraryRoot);
        if (total > free - 200L * 1024 * 1024)
            throw new InvalidOperationException(
                $"Not enough space on the iPod: this needs {total / 1e9:0.0} GB and {free / 1e9:0.0} GB is free.");

        var failed = new List<TransferFailure>();
        int copied = 0, filesDone = 0;
        var batchFile = Path.Combine(Path.GetTempPath(), $"podmanager-{Environment.ProcessId}.sftp");
        try
        {
            foreach (var album in albums)
            {
                ct.ThrowIfCancellationRequested();
                var jobs = album.Files.Select(f =>
                {
                    var dst = LocalPath(libraryRoot, Settings.TargetFolder, f);
                    return (File: f, Dst: dst, Part: dst + ".part");
                }).ToList();

                var batch = new StringBuilder();
                var unsupported = new HashSet<string>(StringComparer.Ordinal);
                foreach (var job in jobs)
                {
                    var remote = SftpQuote($"{Settings.RemoteRoot.TrimEnd('/')}/{job.File.RelativePath}");
                    var local = SftpQuote(job.Part.Replace('\\', '/'));
                    if (remote is null || local is null)
                    {
                        unsupported.Add(job.File.RelativePath); // stays on the server
                        continue;
                    }
                    try { Directory.CreateDirectory(Path.GetDirectoryName(job.Dst)!); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    batch.Append("-get ").Append(remote).Append(' ').Append(local).Append('\n');
                }
                await File.WriteAllTextAsync(batchFile, batch.ToString(), new UTF8Encoding(false), ct);

                var label = $"{album.Artist} – {album.Album}";
                var run = ProcessRunner.RunAsync("sftp",
                    ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-b", batchFile, Settings.Remote], ct);
                ProcessResult sftp;
                try
                {
                    while (!run.IsCompleted)
                    {
                        await Task.WhenAny(run, Task.Delay(400, ct));
                        var inFlight = jobs.Sum(j => SafeLength(j.Part) ?? SafeLength(j.Dst) ?? 0);
                        progress?.Report(new SyncProgress(filesDone, all.Count, done + inFlight, total, label));
                    }
                    sftp = await run;
                }
                catch (OperationCanceledException)
                {
                    foreach (var job in jobs) TryDelete(job.Part);
                    throw;
                }

                // A size that doesn't match the listing usually means the server converted the song
                // again while we were syncing (the master was replaced). If what arrived matches the
                // file on the server now, the copy is complete: it's just the newer version.
                var resized = new Dictionary<string, long>(StringComparer.Ordinal);
                if (sftp.ExitCode == 0)
                {
                    var odd = jobs.Where(j => !unsupported.Contains(j.File.RelativePath) && SafeLength(j.Part) is > 0 and var g && g != j.File.Size)
                                  .Select(j => j.File.RelativePath).ToList();
                    if (odd.Count > 0)
                        foreach (var (rel, size) in await RemoteSizesAsync(odd, CancellationToken.None))
                            resized[rel] = size;
                }

                var verified = new List<RemoteFile>();
                foreach (var job in jobs)
                {
                    var got = SafeLength(job.Part);
                    string? reason = null;
                    var expected = resized.TryGetValue(job.File.RelativePath, out var now) && now == got ? now : job.File.Size;
                    if (unsupported.Contains(job.File.RelativePath))
                        reason = "The file name contains both ' and \", which the copier can't handle. Rename it on the server.";
                    else if (got == expected)
                    {
                        try
                        {
                            File.Move(job.Part, job.Dst, overwrite: true);
                            copied++;
                            bytesCopied += expected;
                            verified.Add(job.File with { Size = expected });
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            reason = $"Copied, but couldn't be saved on the iPod: {ex.Message}";
                        }
                    }
                    else
                    {
                        reason = ExplainFailure(job.File.RelativePath, got, job.File.Size, sftp);
                    }
                    if (reason is not null)
                    {
                        TryDelete(job.Part);
                        failed.Add(new TransferFailure(job.File.RelativePath, reason));
                    }
                    filesDone++;
                    done += job.File.Size;
                }
                await RemoveFromServerAsync(verified, CancellationToken.None); // don't strand delivered files on cancel
                progress?.Report(new SyncProgress(filesDone, all.Count, done, total, label));
            }
        }
        finally
        {
            TryDelete(batchFile);
        }
        return new SyncResult(copied, bytesCopied, failed);
    }

    /// <summary>Current sizes of outbox files (relative paths); files that are gone are left out.</summary>
    private async Task<Dictionary<string, long>> RemoteSizesAsync(IReadOnlyList<string> rels, CancellationToken ct)
    {
        var command = $"cd {ShellQuote(Settings.RemoteRoot)} && stat -c '%s\t%n' -- " + string.Join(' ', rels.Select(ShellQuote)) + " 2>/dev/null";
        var result = await ProcessRunner.RunAsync("ssh", ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", Settings.Remote, command], ct);
        return ParseSizes(result.StdOut);
    }

    internal static Dictionary<string, long> ParseSizes(string output)
    {
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab > 0 && long.TryParse(line.AsSpan(0, tab), out var size))
                sizes[line[(tab + 1)..]] = size;
        }
        return sizes;
    }

    /// <summary>Best explanation for a file that didn't arrive complete, using sftp's own messages.</summary>
    internal static string ExplainFailure(string relativePath, long? got, long expected, ProcessResult sftp)
    {
        var name = relativePath[(relativePath.LastIndexOf('/') + 1)..];
        var lines = (sftp.StdErr + "\n" + sftp.StdOut).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mention = lines.LastOrDefault(l => l.Contains(name, StringComparison.Ordinal) && !l.StartsWith("sftp>", StringComparison.Ordinal)
                                                && !l.StartsWith("Fetching", StringComparison.Ordinal));
        if (mention is not null)
        {
            // sftp couldn't create the file on our side (the iPod), e.g. a path Windows can't hold
            if (mention.Contains("local file", StringComparison.OrdinalIgnoreCase))
                return $"Couldn't write it to the iPod: {mention}";
            if (mention.Contains("not found", StringComparison.OrdinalIgnoreCase) || mention.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                return "Not on the server any more (it may have been converted again or removed). Refresh and try again.";
            if (mention.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                return "Permission denied on the server.";
            return $"Server said: {mention}";
        }
        if (sftp.ExitCode != 0 && lines.Any(l => l.Contains("Connection", StringComparison.OrdinalIgnoreCase) || l.Contains("timed out", StringComparison.OrdinalIgnoreCase)))
            return "Lost the connection to the server during the copy.";
        if (got is > 0 && sftp.ExitCode == 0)
            return $"The file changed on the server during the sync ({got.Value / 1e6:0.0} MB arrived, {expected / 1e6:0.0} MB was listed). Refresh and sync again.";
        if (got is > 0)
            return $"Incomplete copy ({got.Value / 1e6:0.0} of {expected / 1e6:0.0} MB arrived). The iPod may have been disconnected or run out of space.";
        return sftp.ExitCode != 0
            ? $"Copy failed (sftp exit {sftp.ExitCode}: {lines.LastOrDefault(l => !l.StartsWith("sftp>", StringComparison.Ordinal)) ?? "no details"})."
            : "Nothing arrived for this file.";
    }

    /// <summary>
    /// Deletes delivered files from the outbox (one sftp session). Does nothing unless
    /// <see cref="SyncSettings.CanDelete"/>, so the lossless library can never be touched.
    /// </summary>
    public async Task<int> RemoveFromServerAsync(IReadOnlyCollection<RemoteFile> files, CancellationToken ct = default)
    {
        if (!Settings.CanDelete || files.Count == 0) return 0;
        var batchFile = Path.Combine(Path.GetTempPath(), $"podmanager-rm-{Environment.ProcessId}.sftp");
        try
        {
            var batch = new StringBuilder();
            foreach (var f in files)
                if (SftpQuote($"{Settings.RemoteRoot.TrimEnd('/')}/{f.RelativePath}") is { } quoted)
                    batch.Append("-rm ").Append(quoted).Append('\n');
            await File.WriteAllTextAsync(batchFile, batch.ToString(), new UTF8Encoding(false), ct);
            var result = await ProcessRunner.RunAsync("sftp",
                ["-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-b", batchFile, Settings.Remote], ct);
            return result.ExitCode == 0 ? files.Count : 0;
        }
        finally { TryDelete(batchFile); }
    }

    private static long? SafeLength(string path)
    {
        try { var i = new FileInfo(path); return i.Exists ? i.Length : null; }
        catch (IOException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    internal static string SftpEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Quotes a path for an sftp batch file. sftp doesn't honour \" inside double quotes, so a name
    /// containing " is wrapped in single quotes instead. Null if it contains both kinds (unsupported).
    /// </summary>
    internal static string? SftpQuote(string s) =>
        !s.Contains('"') ? $"\"{s.Replace("\\", "\\\\")}\"" :
        !s.Contains('\'') ? $"'{s}'" : null;
}
