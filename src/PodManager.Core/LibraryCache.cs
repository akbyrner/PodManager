using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PodManager.Core;

public static class AppPaths
{
    /// <summary>Settings, caches and the transfer log. PODMANAGER_DATA overrides it (used by tests).</summary>
    public static string DataDirectory { get; set; } =
        Environment.GetEnvironmentVariable("PODMANAGER_DATA") is { Length: > 0 } custom ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PodManager");
}

/// <summary>
/// Remembers each file's tags by relative path + size + modified time, so a re-scan only opens
/// files that changed. Reading tags over USB is the slow part of loading a full iPod.
/// </summary>
public sealed class LibraryCache
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _path;
    private Dictionary<string, Track> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public LibraryCache(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<Track>>(File.ReadAllText(path), Json) ?? [];
                _byPath = list.ToDictionary(t => t.RelativePath, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            _byPath = new(StringComparer.OrdinalIgnoreCase); // corrupt cache: just rescan
        }
    }

    /// <summary>Cache file for a library, e.g. key "IPOD|255800115200" for a device.</summary>
    public static string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return Path.Combine(AppPaths.DataDirectory, $"cache-{hash}.json");
    }

    public int Count => _byPath.Count;

    public bool TryGet(string relativePath, long size, DateTime modifiedUtc, out Track track)
    {
        if (_byPath.TryGetValue(relativePath, out var t) && t.Size == size && t.ModifiedUtc == modifiedUtc)
        {
            track = t;
            return true;
        }
        track = null!;
        return false;
    }

    public void Replace(IEnumerable<Track> tracks)
    {
        _byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tracks) _byPath[t.RelativePath] = t;
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_byPath.Values.ToList(), Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (IOException) { } // a cache that fails to save only costs a slower next scan
        catch (UnauthorizedAccessException) { }
    }
}
