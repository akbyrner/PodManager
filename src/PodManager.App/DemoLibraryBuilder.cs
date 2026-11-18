using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PodManager.Core;

namespace PodManager.App;

/// <summary>Builds a fake iPod (made-up artists, silent songs, generated covers) for testing the UI. --make-demo &lt;folder&gt;</summary>
public static class DemoLibraryBuilder
{
    private sealed record DemoAlbum(string Artist, string Album, uint Year, string Genre, int Tracks, Color A, Color B, bool Art = true);

    private static readonly DemoAlbum[] Albums =
    [
        new("The Harbor Lights", "Low Tide", 2019, "Indie", 9, Color.FromRgb(0x1F, 0x4E, 0x79), Color.FromRgb(0x5D, 0xA9, 0xC9)),
        new("The Harbor Lights", "Undertow", 2022, "Indie", 7, Color.FromRgb(0x0E, 0x2A, 0x3B), Color.FromRgb(0x2E, 0x86, 0x8C)),
        new("Mira Sol", "Glass Garden", 2021, "Pop", 10, Color.FromRgb(0xE0, 0x6C, 0x9F), Color.FromRgb(0xF7, 0xC5, 0x9F)),
        new("Northbound", "Pines", 2017, "Folk", 8, Color.FromRgb(0x2F, 0x4F, 0x2F), Color.FromRgb(0x9C, 0xB3, 0x80)),
        new("Velvet Static", "Night Drive", 2023, "Electronic", 6, Color.FromRgb(0x2B, 0x10, 0x55), Color.FromRgb(0xD6, 0x3A, 0xF9)),
        new("Velvet Static", "Afterglow", 2020, "Electronic", 5, Color.FromRgb(0xF2, 0x6B, 0x38), Color.FromRgb(0x5B, 0x1A, 0x47)),
        new("June & The Kites", "Paper Planes", 2018, "Indie Pop", 8, Color.FromRgb(0xF4, 0xD3, 0x5E), Color.FromRgb(0xEE, 0x96, 0x4B)),
        new("Odd Weather", "Barometer", 2024, "Rock", 7, Colors.Gray, Colors.Gray, Art: false),
        new("Kestrel", "Field Recordings", 2016, "Ambient", 6, Color.FromRgb(0x6B, 0x5B, 0x4B), Color.FromRgb(0xD9, 0xC5, 0xA0)),
        new("Ada Lune", "Small Hours", 2025, "Jazz", 7, Color.FromRgb(0x14, 0x14, 0x2B), Color.FromRgb(0xC8, 0xA2, 0x4B)),
    ];

    private static readonly string[] Words =
    [
        "Morning", "Static", "Lanterns", "Coastline", "Paper", "Echo", "Velvet", "Hollow", "Satellite", "Ember",
        "Northern", "Window", "Silver", "Driftwood", "Signal", "Harbor", "Glass", "Midnight", "River", "Orbit",
        "Golden", "Quiet", "Wildfire", "Polaroid", "Undertow", "Neon", "Summer", "Daylight", "Wires", "Atlas",
    ];

    public static void Build(string folder)
    {
        var root = Path.GetFullPath(folder);
        Directory.CreateDirectory(Path.Combine(root, ".knurl"));
        var rng = new Random(42);
        foreach (var album in Albums)
        {
            var dir = Path.Combine(root, "Music", Sanitizer.Segment(album.Artist), $"{album.Album} ({album.Year})");
            Directory.CreateDirectory(dir);
            var art = album.Art ? MakeCover(album.Album, album.Artist, album.A, album.B) : null;
            for (var n = 1; n <= album.Tracks; n++)
            {
                var title = $"{Words[rng.Next(Words.Length)]} {Words[rng.Next(Words.Length)]}";
                if (rng.Next(4) == 0) title = $"{Words[rng.Next(Words.Length)]}";
                var path = Path.Combine(dir, $"{n:00} - {title}.mp3");
                SilentMp3.Write(path, 20 + rng.Next(40));
                using var file = TagLib.File.Create(path);
                file.Tag.Title = title;
                file.Tag.Performers = [album.Artist];
                file.Tag.AlbumArtists = [album.Artist];
                file.Tag.Album = album.Album;
                file.Tag.Year = album.Year;
                file.Tag.Genres = [album.Genre];
                file.Tag.Track = (uint)n;
                file.Tag.TrackCount = (uint)album.Tracks;
                if (art is not null)
                    file.Tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(art)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" }];
                file.Save();
            }
        }

        // One song "added by iTunes", to show the read-only handling.
        var itunesDir = Path.Combine(root, "iPod_Control", "Music", "F12");
        Directory.CreateDirectory(itunesDir);
        var itunesSong = Path.Combine(itunesDir, "KQZP.mp3");
        SilentMp3.Write(itunesSong, 45);
        using (var f = TagLib.File.Create(itunesSong))
        {
            f.Tag.Title = "Old iTunes Favorite";
            f.Tag.Performers = ["Various"];
            f.Tag.Album = "iTunes Purchases";
            f.Save();
        }
        new DirectoryInfo(Path.Combine(root, "iPod_Control")).Attributes |= FileAttributes.Hidden;
    }

    private static byte[] MakeCover(string album, string artist, Color a, Color b)
    {
        const int size = 400;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(a, b, 45), null, new Rect(0, 0, size, size));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null, new Point(size * 0.72, size * 0.3), size * 0.32, size * 0.32);
            var title = new FormattedText(album.ToUpperInvariant(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 38, Brushes.White, 1.0)
            { MaxTextWidth = size - 60 };
            var by = new FormattedText(artist, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 20, new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.0)
            { MaxTextWidth = size - 60 };
            dc.DrawText(title, new Point(30, size - 60 - title.Height - by.Height));
            dc.DrawText(by, new Point(32, size - 50 - by.Height));
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
