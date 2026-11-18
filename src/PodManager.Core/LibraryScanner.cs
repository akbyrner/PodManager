using System.Text.RegularExpressions;
using TagLib;

namespace PodManager.Core;

public sealed record ScanProgress(int Done, int Total);

public static partial class LibraryScanner
{
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".aac", ".alac", ".ogg", ".oga", ".opus", ".wav",
        ".aif", ".aiff", ".wma", ".ape", ".wv", ".mpc", ".mp2",
    };

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".knurl", ".rockbox", "System Volume Information", "$RECYCLE.BIN",
    };

    // Hidden must NOT be skipped: iTunes keeps its songs in the hidden iPod_Control folder.
    private static readonly EnumerationOptions Shallow = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    public static IEnumerable<string> EnumerateAudioFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            List<string> subdirs, files;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir, "*", Shallow).ToList();
                files = Directory.EnumerateFiles(dir, "*", Shallow).ToList();
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (SkipDirectories.Contains(name) || name.StartsWith('.'))
                    continue;
                pending.Push(sub);
            }
            foreach (var file in files)
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("._", StringComparison.Ordinal) || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (AudioExtensions.Contains(Path.GetExtension(file)))
                    yield return file;
            }
        }
    }

    /// <summary>Reads every audio file under <paramref name="root"/>, reusing cached tags for unchanged files.</summary>
    public static List<Track> Scan(string root, LibraryCache? cache, IProgress<ScanProgress>? progress, CancellationToken ct = default)
    {
        var files = EnumerateAudioFiles(root).ToList();
        var tracks = new List<Track>(files.Count);
        progress?.Report(new ScanProgress(0, files.Count));
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var path = files[i];
            FileInfo info;
            try { info = new FileInfo(path); _ = info.Length; }
            catch (IOException) { continue; }

            var rel = Path.GetRelativePath(root, path);
            if (cache != null && cache.TryGet(rel, info.Length, info.LastWriteTimeUtc, out var cached))
                tracks.Add(cached with { FullPath = path });
            else
                tracks.Add(ReadTrack(root, path));

            if (i % 25 == 24 || i == files.Count - 1)
                progress?.Report(new ScanProgress(i + 1, files.Count));
        }
        cache?.Replace(tracks);
        return tracks;
    }

    /// <summary>Reads one file's tags; falls back to what the folder and file names say.</summary>
    public static Track ReadTrack(string root, string path)
    {
        var info = new FileInfo(path);
        var rel = Path.GetRelativePath(root, path);
        var fallback = FromPath(rel) with
        {
            FullPath = path,
            Size = info.Length,
            ModifiedUtc = info.LastWriteTimeUtc,
            Format = FormatFromExtension(path),
        };

        try
        {
            using var file = TagLib.File.Create(path, ReadStyle.Average);
            var tag = file.Tag;
            var props = file.Properties;
            return fallback with
            {
                Title = FirstNonEmpty(tag.Title, fallback.Title),
                Artist = FirstNonEmpty(tag.FirstPerformer, tag.FirstAlbumArtist, fallback.Artist),
                AlbumArtist = tag.FirstAlbumArtist ?? "",
                Album = FirstNonEmpty(tag.Album, fallback.Album),
                Genre = tag.FirstGenre ?? "",
                Year = tag.Year != 0 ? tag.Year : fallback.Year,
                TrackNumber = tag.Track != 0 ? tag.Track : fallback.TrackNumber,
                DiscNumber = tag.Disc,
                Duration = props?.Duration ?? TimeSpan.Zero,
                Bitrate = props?.AudioBitrate ?? 0,
                SampleRate = props?.AudioSampleRate ?? 0,
                BitsPerSample = props?.BitsPerSample ?? 0,
                Format = FormatName(path, props),
                HasEmbeddedArt = tag.Pictures is { Length: > 0 },
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return fallback; // unreadable or untagged: names from the path are better than nothing
        }
    }

    [GeneratedRegex(@"^(\d{1,3})(?:[\s.\-_]+)(.+)$")]
    private static partial Regex LeadingTrackNumber();

    [GeneratedRegex(@"^(.*?)\s*[\(\[](\d{4})[\)\]]\s*$")]
    private static partial Regex TrailingYear();

    /// <summary>Music\Artist\Album (2001)\01 - Title.flac -> artist, album, year, track, title.</summary>
    internal static Track FromPath(string rel)
    {
        var parts = rel.Split('\\', '/');
        var itunes = parts.Length > 1 && parts[0].Equals("iPod_Control", StringComparison.OrdinalIgnoreCase);
        var title = Path.GetFileNameWithoutExtension(parts[^1]);
        uint trackNo = 0;
        var m = LeadingTrackNumber().Match(title);
        if (m.Success && !itunes)
        {
            trackNo = uint.Parse(m.Groups[1].Value);
            title = m.Groups[2].Value;
        }

        string album = "", artist = "";
        uint year = 0;
        if (!itunes)
        {
            // parts: [Music, Artist, Album, file] -> album = parts[^2], artist = parts[^3]
            if (parts.Length >= 3) album = parts[^2];
            if (parts.Length >= 4) artist = parts[^3];
            var y = TrailingYear().Match(album);
            if (y.Success)
            {
                album = y.Groups[1].Value;
                year = uint.Parse(y.Groups[2].Value);
            }
        }

        return new Track
        {
            FullPath = rel,
            RelativePath = rel,
            Title = title,
            Artist = artist,
            Album = album,
            Year = year,
            TrackNumber = trackNo,
            IsITunesManaged = itunes,
        };
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static string FormatFromExtension(string path) =>
        Path.GetExtension(path).TrimStart('.').ToUpperInvariant() switch
        {
            "AIF" => "AIFF",
            "OGA" => "OGG",
            var e => e,
        };

    private static string FormatName(string path, Properties? props)
    {
        var ext = FormatFromExtension(path);
        if (ext is "M4A" or "ALAC" or "AAC")
        {
            var desc = props?.Description ?? "";
            return desc.Contains("lossless", StringComparison.OrdinalIgnoreCase) ||
                   desc.Contains("alac", StringComparison.OrdinalIgnoreCase) ? "ALAC" : "AAC";
        }
        return ext;
    }
}
