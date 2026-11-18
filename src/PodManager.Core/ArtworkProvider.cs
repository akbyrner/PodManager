using TagLib;

namespace PodManager.Core;

public static class ArtworkProvider
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp" };

    private static readonly string[] CoverNames = ["cover", "folder", "front", "album", "albumart"];

    /// <summary>Album art bytes: embedded front cover first, then cover/folder/front image in the album folder.</summary>
    public static byte[]? Load(Track track)
    {
        if (track.HasEmbeddedArt)
        {
            try
            {
                using var file = TagLib.File.Create(track.FullPath, ReadStyle.None);
                var pictures = file.Tag.Pictures;
                var pic = pictures.FirstOrDefault(p => p.Type == PictureType.FrontCover) ?? pictures.FirstOrDefault();
                if (pic?.Data?.Count > 0)
                    return pic.Data.Data;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        var dir = Path.GetDirectoryName(track.FullPath);
        if (dir is null || track.IsITunesManaged)
            return null;
        try
        {
            var candidates = Directory.EnumerateFiles(dir)
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f)))
                .Select(f => (Path: f, Name: Path.GetFileNameWithoutExtension(f).ToLowerInvariant()))
                .ToList();
            var best = candidates.FirstOrDefault(c => CoverNames.Contains(c.Name)).Path
                       ?? candidates.FirstOrDefault(c => c.Name.StartsWith("albumart", StringComparison.Ordinal)).Path;
            return best is null ? null : System.IO.File.ReadAllBytes(best);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
