namespace PodManager.Core;

public sealed record DeleteFailure(Track Track, string Reason);

public sealed record DeleteResult(int Deleted, long BytesFreed, int FoldersRemoved, IReadOnlyList<DeleteFailure> Failures);

public static class TrackDeleter
{
    /// <summary>Files that don't keep an album folder alive once its songs are gone.</summary>
    private static readonly HashSet<string> Leftovers = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".txt", ".log", ".cue", ".nfo",
        ".m3u", ".m3u8", ".sfv", ".accurip", ".db", ".ini",
    };

    /// <summary>
    /// Deletes the files (permanently: FAT32 has no recycle bin), then removes album/artist folders
    /// left with nothing but cover art and text files. Top-level folders such as Music are kept.
    /// </summary>
    public static DeleteResult Delete(string root, IEnumerable<Track> tracks)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var failures = new List<DeleteFailure>();
        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int deleted = 0;
        long freed = 0;

        foreach (var track in tracks)
        {
            if (track.IsITunesManaged)
            {
                failures.Add(new(track, "Added by iTunes; remove it with iTunes instead."));
                continue;
            }
            var full = Path.GetFullPath(track.FullPath);
            if (!IsInside(full, root))
            {
                failures.Add(new(track, "Not inside the library."));
                continue;
            }
            try
            {
                var size = new FileInfo(full).Length;
                File.SetAttributes(full, FileAttributes.Normal);
                File.Delete(full);
                deleted++;
                freed += size;
                touched.Add(Path.GetDirectoryName(full)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new(track, ex.Message));
            }
        }

        var folders = 0;
        foreach (var dir in touched.OrderByDescending(d => d.Length))
            folders += PruneEmpty(dir, root);

        return new DeleteResult(deleted, freed, folders, failures);
    }

    private static int PruneEmpty(string dir, string root)
    {
        var removed = 0;
        try
        {
            while (true)
            {
                var parent = Path.GetDirectoryName(dir);
                // Never remove the root or a folder directly under it (Music, iPod_Control, ...).
                if (parent is null || PathEquals(dir, root) || PathEquals(parent, root) || !IsInside(dir, root))
                    break;
                if (Directory.Exists(dir))
                {
                    if (Directory.EnumerateDirectories(dir).Any())
                        break;
                    var files = Directory.GetFiles(dir);
                    if (files.Any(f => !Leftovers.Contains(Path.GetExtension(f))))
                        break;
                    foreach (var f in files)
                    {
                        File.SetAttributes(f, FileAttributes.Normal);
                        File.Delete(f);
                    }
                    Directory.Delete(dir);
                    removed++;
                }
                dir = parent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return removed;
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string path, string root) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
