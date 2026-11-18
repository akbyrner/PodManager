using System.Text;

namespace PodManager.Core;

/// <summary>
/// Songs deleted from the iPod on purpose (or marked Never Sync), so a sync doesn't copy them straight back.
/// Stored on the iPod itself (\.podmanager\excluded.txt) so it travels with the device. Each line is a
/// path relative to the sync's target folder in local (sanitized) form, e.g. "AC_DC\Back in Black_\01.flac".
/// </summary>
public static class ExclusionList
{
    public static string FilePath(string root) => Path.Combine(root, ".podmanager", "excluded.txt");

    public static string KeyFor(RemoteFile file) => Sanitizer.LocalRelative(file.RelativePath);

    /// <summary>Key for a track inside the sync target folder; null for songs elsewhere (e.g. iTunes').</summary>
    public static string? KeyFor(Track track, string targetFolder)
    {
        var prefix = targetFolder.TrimEnd('\\', '/') + "\\";
        return track.RelativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? track.RelativePath[prefix.Length..]
            : null;
    }

    public static HashSet<string> Load(string root)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = FilePath(root);
            if (File.Exists(path))
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                    if (!string.IsNullOrWhiteSpace(line)) set.Add(line.Trim());
        }
        catch (IOException) { }
        return set;
    }

    public static void Add(string root, IEnumerable<string> keys) => Update(root, set => set.UnionWith(keys));

    public static void Remove(string root, IEnumerable<string> keys) => Update(root, set => set.ExceptWith(keys));

    private static void Update(string root, Action<HashSet<string>> change)
    {
        var set = Load(root);
        var before = set.Count;
        change(set);
        if (set.Count == before && !File.Exists(FilePath(root)) && before == 0) return;

        var dir = Path.GetDirectoryName(FilePath(root))!;
        var info = Directory.CreateDirectory(dir);
        info.Attributes |= FileAttributes.Hidden;
        File.WriteAllLines(FilePath(root), set.Order(StringComparer.OrdinalIgnoreCase), new UTF8Encoding(false));
    }
}
