using PodManager.Core;

namespace PodManager.Tests;

/// <summary>A throwaway fake iPod: a temp folder with .knurl, Music\ and optionally iPod_Control\.</summary>
public sealed class TempLibrary : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "podmanager-test-" + Guid.NewGuid().ToString("N")[..8]);

    public TempLibrary()
    {
        Directory.CreateDirectory(Path.Combine(Root, ".knurl"));
        Directory.CreateDirectory(Path.Combine(Root, "Music"));
    }

    public static readonly byte[] FakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 0xFF, 0xD9];

    /// <summary>Writes a silent MP3 at Root\relativePath, tagged if any tag is given.</summary>
    public string AddSong(string relativePath, string? title = null, string? artist = null, string? album = null,
        uint track = 0, uint year = 0, byte[]? art = null)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SilentMp3.Write(path, 2);
        if (title is not null || artist is not null || album is not null || track != 0 || year != 0 || art is not null)
        {
            using var f = TagLib.File.Create(path);
            if (title is not null) f.Tag.Title = title;
            if (artist is not null) f.Tag.Performers = [artist];
            if (album is not null) f.Tag.Album = album;
            f.Tag.Track = track;
            f.Tag.Year = year;
            if (art is not null)
                f.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(art)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" }];
            f.Save();
        }
        return path;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
    }
}
