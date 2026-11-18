using TagLib;

namespace PodManager.Core;

/// <summary>
/// Changes to apply. null = leave as is. For text, "" clears the field; for numbers, 0 clears it.
/// </summary>
public sealed record TagEdit
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public uint? Year { get; init; }
    public uint? TrackNumber { get; init; }
    public uint? DiscNumber { get; init; }
    public byte[]? Artwork { get; init; }
    public bool RemoveArtwork { get; init; }

    public bool IsEmpty =>
        Title is null && Artist is null && AlbumArtist is null && Album is null && Genre is null &&
        Year is null && TrackNumber is null && DiscNumber is null && Artwork is null && !RemoveArtwork;
}

public static class TagEditor
{
    /// <summary>Writes the edit into the file and returns the re-read track.</summary>
    public static Track Apply(string root, Track track, TagEdit edit)
    {
        if (track.IsITunesManaged)
            throw new InvalidOperationException($"\"{track.DisplayTitle}\" was added by iTunes and can't be edited here.");

        using (var file = TagLib.File.Create(track.FullPath))
        {
            var tag = file.Tag;
            if (edit.Title is not null) tag.Title = Text(edit.Title);
            if (edit.Artist is not null) tag.Performers = List(edit.Artist);
            if (edit.AlbumArtist is not null) tag.AlbumArtists = List(edit.AlbumArtist);
            if (edit.Album is not null) tag.Album = Text(edit.Album);
            if (edit.Genre is not null) tag.Genres = List(edit.Genre);
            if (edit.Year is not null) tag.Year = edit.Year.Value;
            if (edit.TrackNumber is not null) tag.Track = edit.TrackNumber.Value;
            if (edit.DiscNumber is not null) tag.Disc = edit.DiscNumber.Value;
            if (edit.RemoveArtwork)
            {
                tag.Pictures = [];
            }
            else if (edit.Artwork is { Length: > 0 } art)
            {
                tag.Pictures =
                [
                    new Picture(new ByteVector(art))
                    {
                        Type = PictureType.FrontCover,
                        MimeType = MimeType(art),
                        Description = "Cover",
                    },
                ];
            }
            file.Save();
        }
        return LibraryScanner.ReadTrack(root, track.FullPath);
    }

    public static string MimeType(byte[] image) =>
        image.Length > 3 && image[0] == 0x89 && image[1] == 0x50 && image[2] == 0x4E ? "image/png" : "image/jpeg";

    private static string? Text(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string[] List(string s) => string.IsNullOrWhiteSpace(s) ? [] : [s.Trim()];
}
