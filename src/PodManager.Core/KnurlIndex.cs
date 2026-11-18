using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PodManager.Core;

/// <summary>Decodes an image file (JPEG/PNG) to 24-bit RGB, ideally at about <c>maxSide</c> pixels on the long side.</summary>
public interface IImageDecoder
{
    RgbImage? Decode(byte[] data, int maxSide);
}

/// <summary>Packed RGB, 3 bytes per pixel, row-major.</summary>
public sealed record RgbImage(int Width, int Height, byte[] Rgb);

public sealed record KnurlIndexResult(int Tracks, int Albums, int Artists, int AlbumsWithArt, long LibraryBytes, long ArtBytes);

/// <summary>
/// Writes the library index the hand-rolled Knurl iPod firmware reads: <c>.knurl/library.bin</c>
/// (songs, albums, artists, pre-sorted) and <c>.knurl/art.bin</c> (covers pre-scaled and dithered to
/// RGB565 at the exact sizes the screens draw, plus a 40x30 pre-blurred "wash" per album).
/// The layout must match <c>firmware/src/library/kidx.h</c> in the ipod-firmware project.
/// </summary>
public static class KnurlIndex
{
    public const string Folder = ".knurl";
    private const uint Version = 1;
    private const int ThumbS = 24, TileS = 54, GridS = 66, CoverS = 140, WashW = 40, WashH = 30;
    private const int ArtEntrySize = 65536, ArtDataOff = 4096;
    private static readonly int[] ArtSizes = [ThumbS, TileS, GridS, CoverS];

    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions SortOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    private sealed class AlbumInfo
    {
        public required string Title, Artist;
        public required List<Track> Tracks;
        public uint Year;
        public DateTime Added;
        public int ArtistIndex;
        public int ArtEntry = 0xFFFF;
        public uint Color0, Color1;
    }

    /// <summary>Builds both files for the library at <paramref name="root"/> (the iPod's drive root).</summary>
    public static KnurlIndexResult Write(string root, IReadOnlyList<Track> allTracks, IImageDecoder? decoder,
        Func<Track, byte[]?>? loadArt = null, CancellationToken ct = default)
    {
        loadArt ??= ArtworkProvider.Load;
        // The firmware decodes MP3 only.
        var tracks = allTracks.Where(t => Path.GetExtension(t.FullPath).Equals(".mp3", StringComparison.OrdinalIgnoreCase)).ToList();
        if (tracks.Count > 65535) throw new InvalidOperationException("The Knurl index holds at most 65,535 songs.");

        // ---- group into albums (album artist + album), sort -------------------------------
        var albums = tracks
            .GroupBy(t => (Artist: t.DisplayAlbumArtist.Trim().ToUpperInvariant(), Album: t.DisplayAlbum.Trim().ToUpperInvariant()))
            .Select(g =>
            {
                var list = g.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber)
                            .ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase).ToList();
                return new AlbumInfo
                {
                    Title = list[0].DisplayAlbum.Trim(),
                    Artist = list[0].DisplayAlbumArtist.Trim(),
                    Tracks = list,
                    Year = list.Max(t => t.Year),
                    Added = list.Max(t => AddedTime(t)),
                };
            })
            .ToList();
        if (albums.Count > 65535) throw new InvalidOperationException("The Knurl index holds at most 65,535 albums.");

        var artistNames = albums.Select(a => a.Artist)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(SortKey, Comparer<string>.Create((x, y) => Compare.Compare(x, y, SortOptions)))
            .ToList();
        var artistIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < artistNames.Count; i++) artistIndex[artistNames[i]] = i;

        albums = albums
            .OrderBy(a => artistIndex[a.Artist])
            .ThenBy(a => a.Year == 0 ? uint.MaxValue : a.Year)
            .ThenBy(a => a.Title, Comparer<string>.Create((x, y) => Compare.Compare(x, y, SortOptions)))
            .ToList();
        foreach (var a in albums) a.ArtistIndex = artistIndex[a.Artist];

        // ---- artwork ---------------------------------------------------------------------------
        var dir = Path.Combine(root, Folder);
        Directory.CreateDirectory(dir);
        var artTmp = Path.Combine(dir, "art.bin.tmp");
        var artEntries = 0;
        using (var art = new FileStream(artTmp, FileMode.Create, FileAccess.Write))
        {
            art.Write(new byte[ArtDataOff]);
            foreach (var album in albums)
            {
                ct.ThrowIfCancellationRequested();
                RgbImage? img = null;
                if (decoder is not null)
                {
                    var bytes = album.Tracks.Select(t => t.HasEmbeddedArt ? loadArt(t) : null).FirstOrDefault(b => b is not null)
                                ?? loadArt(album.Tracks[0]);
                    if (bytes is not null)
                    {
                        try { img = decoder.Decode(bytes, 300); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { img = null; }
                    }
                }
                if (img is null || img.Width < 8 || img.Height < 8)
                {
                    (album.Color0, album.Color1) = NameColors(album.Title + "\n" + album.Artist);
                    continue;
                }
                var square = CropSquare(img);
                var entry = new byte[ArtEntrySize];
                var off = 0;
                foreach (var s in ArtSizes)
                {
                    var rgb = Resize(square, s, s);
                    Rgb565Dithered(rgb, s, s, entry, off);
                    off += s * s * 2;
                }
                var wash = Wash(square);
                Buffer.BlockCopy(wash, 0, entry, off, wash.Length);
                art.Write(entry);
                var tile = Resize(square, 2, 2);
                album.Color0 = (uint)(tile[0] << 16 | tile[1] << 8 | tile[2]);
                album.Color1 = (uint)(tile[9] << 16 | tile[10] << 8 | tile[11]);
                album.ArtEntry = artEntries++;
            }
            // header
            var h = new byte[64];
            Encoding.ASCII.GetBytes("KNURLART").CopyTo(h, 0);
            Put32(h, 8, Version);
            Put32(h, 12, (uint)artEntries);
            Put32(h, 16, ArtEntrySize);
            Put32(h, 20, ArtDataOff);
            var o = 0;
            for (var k = 0; k < ArtSizes.Length; k++) { Put32(h, 24 + k * 4, (uint)o); o += ArtSizes[k] * ArtSizes[k] * 2; }
            Put32(h, 24 + ArtSizes.Length * 4, (uint)o);
            art.Position = 0;
            art.Write(h);
        }

        // ---- library.bin --------------------------------------------------------------------------
        var strings = new StringTable();
        var trackList = new List<(Track T, int Album)>();
        var firstTrack = new int[albums.Count];
        for (var a = 0; a < albums.Count; a++)
        {
            firstTrack[a] = trackList.Count;
            foreach (var t in albums[a].Tracks) trackList.Add((t, a));
        }

        using var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(new byte[64]);

        var tracksOff = (uint)ms.Position;
        foreach (var (t, a) in trackList)
        {
            bw.Write(strings.Add(t.DisplayTitle));
            bw.Write(strings.Add(t.DisplayArtist));
            bw.Write(strings.Add(t.RelativePath.Replace('\\', '/')));
            bw.Write((uint)Math.Clamp(t.Duration.TotalMilliseconds, 0, uint.MaxValue));
            bw.Write((uint)Math.Clamp(t.Size, 0, uint.MaxValue));
            bw.Write((ushort)a);
            bw.Write((byte)Math.Min(t.TrackNumber, 255u));
            bw.Write((byte)Math.Min(t.DiscNumber, 255u));
        }

        var albumsOff = (uint)ms.Position;
        for (var a = 0; a < albums.Count; a++)
        {
            var al = albums[a];
            bw.Write(strings.Add(al.Title));
            bw.Write(strings.Add(al.Artist));
            bw.Write((ushort)al.ArtistIndex);
            bw.Write((ushort)Math.Min(al.Year, 65535u));
            bw.Write((uint)firstTrack[a]);
            bw.Write((ushort)al.Tracks.Count);
            bw.Write((ushort)al.ArtEntry);
            bw.Write(al.Color0);
            bw.Write(al.Color1);
            bw.Write((uint)Math.Max(0, new DateTimeOffset(al.Added, TimeSpan.Zero).ToUnixTimeSeconds()));
        }

        var artistsOff = (uint)ms.Position;
        for (var r = 0; r < artistNames.Count; r++)
        {
            var first = albums.FindIndex(a => a.ArtistIndex == r);
            var mine = albums.Where(a => a.ArtistIndex == r).ToList();
            bw.Write(strings.Add(artistNames[r]));
            bw.Write((uint)first);
            bw.Write((ushort)mine.Count);
            bw.Write((ushort)Math.Min(mine.Sum(a => a.Tracks.Count), 65535));
        }

        var byTitle = Comparer<string>.Create((x, y) => Compare.Compare(x, y, SortOptions));
        var songsOff = (uint)ms.Position;
        foreach (var i in Enumerable.Range(0, trackList.Count).OrderBy(i => SortKey(trackList[i].T.DisplayTitle), byTitle))
            bw.Write((ushort)i);
        Align(bw);
        var albumsByTitleOff = (uint)ms.Position;
        foreach (var i in Enumerable.Range(0, albums.Count).OrderBy(i => SortKey(albums[i].Title), byTitle))
            bw.Write((ushort)i);
        Align(bw);
        var albumsByAddedOff = (uint)ms.Position;
        foreach (var i in Enumerable.Range(0, albums.Count).OrderByDescending(i => albums[i].Added))
            bw.Write((ushort)i);
        Align(bw);

        var stringsOff = (uint)ms.Position;
        var blob = strings.ToArray();
        bw.Write(blob);
        Align(bw);
        bw.Flush();

        var data = ms.ToArray();
        Encoding.ASCII.GetBytes("KNURLIDX").CopyTo(data, 0);
        Put32(data, 8, Version);
        Put32(data, 12, (uint)data.Length);
        Put32(data, 16, (uint)trackList.Count);
        Put32(data, 20, (uint)albums.Count);
        Put32(data, 24, (uint)artistNames.Count);
        Put32(data, 28, tracksOff);
        Put32(data, 32, albumsOff);
        Put32(data, 36, artistsOff);
        Put32(data, 40, songsOff);
        Put32(data, 44, albumsByTitleOff);
        Put32(data, 48, albumsByAddedOff);
        Put32(data, 52, stringsOff);
        Put32(data, 56, (uint)blob.Length);
        Put32(data, 60, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var libTmp = Path.Combine(dir, "library.bin.tmp");
        File.WriteAllBytes(libTmp, data);
        // art first: a library.bin never points at art that isn't there yet
        File.Move(artTmp, Path.Combine(dir, "art.bin"), overwrite: true);
        File.Move(libTmp, Path.Combine(dir, "library.bin"), overwrite: true);
        return new KnurlIndexResult(trackList.Count, albums.Count, artistNames.Count, artEntries,
            data.Length, ArtDataOff + (long)artEntries * ArtEntrySize);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>"The Beatles" sorts as "Beatles".</summary>
    internal static string SortKey(string s)
    {
        s = s.Trim();
        return s.StartsWith("the ", StringComparison.OrdinalIgnoreCase) && s.Length > 4 ? s[4..] : s;
    }

    private static DateTime AddedTime(Track t)
    {
        try { return File.GetCreationTimeUtc(t.FullPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return t.ModifiedUtc; }
    }

    private static void Put32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);

    private static void Align(BinaryWriter bw)
    {
        while (bw.BaseStream.Position % 4 != 0) bw.Write((byte)0);
    }

    private sealed class StringTable
    {
        private readonly MemoryStream _blob = new();
        private readonly Dictionary<string, uint> _seen = new(StringComparer.Ordinal);

        public StringTable() => _blob.WriteByte(0); // offset 0 = ""

        public uint Add(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (_seen.TryGetValue(s, out var off)) return off;
            off = (uint)_blob.Length;
            var bytes = Encoding.UTF8.GetBytes(s);
            _blob.Write(bytes);
            _blob.WriteByte(0);
            _seen[s] = off;
            return off;
        }

        public byte[] ToArray() => _blob.ToArray();
    }

    /// <summary>Two pleasant colours from a name, for albums without art.</summary>
    internal static (uint, uint) NameColors(string name)
    {
        uint h = 2166136261;
        foreach (var c in name) h = (h ^ c) * 16777619;
        var hue = (h % 360) / 360.0;
        return (Hsv(hue, 0.55, 0.75), Hsv((hue + 0.08) % 1, 0.7, 0.4));
    }

    private static uint Hsv(double h, double s, double v)
    {
        var i = (int)(h * 6) % 6;
        var f = h * 6 - Math.Floor(h * 6);
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        var (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
        return (uint)((int)(r * 255) << 16 | (int)(g * 255) << 8 | (int)(b * 255));
    }

    // ---------------------------------------------------------------- image processing

    internal static RgbImage CropSquare(RgbImage img)
    {
        var s = Math.Min(img.Width, img.Height);
        int x0 = (img.Width - s) / 2, y0 = (img.Height - s) / 2;
        var outp = new byte[s * s * 3];
        for (var y = 0; y < s; y++)
            Buffer.BlockCopy(img.Rgb, ((y + y0) * img.Width + x0) * 3, outp, y * s * 3, s * 3);
        return new RgbImage(s, s, outp);
    }

    /// <summary>Area-average resize (good for shrinking; nearest-ish when growing).</summary>
    internal static byte[] Resize(RgbImage src, int w, int h)
    {
        var outp = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            int sy0 = y * src.Height / h, sy1 = Math.Max(sy0 + 1, (y + 1) * src.Height / h);
            for (var x = 0; x < w; x++)
            {
                int sx0 = x * src.Width / w, sx1 = Math.Max(sx0 + 1, (x + 1) * src.Width / w);
                long r = 0, g = 0, b = 0, n = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    var row = sy * src.Width * 3;
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var i = row + sx * 3;
                        r += src.Rgb[i]; g += src.Rgb[i + 1]; b += src.Rgb[i + 2]; n++;
                    }
                }
                var o = (y * w + x) * 3;
                outp[o] = (byte)(r / n); outp[o + 1] = (byte)(g / n); outp[o + 2] = (byte)(b / n);
            }
        }
        return outp;
    }

    private static readonly int[,] Bayer = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };

    /// <summary>RGB888 to RGB565 little-endian with 4x4 ordered dithering (same as the firmware's tools).</summary>
    internal static void Rgb565Dithered(byte[] rgb, int w, int h, byte[] dst, int off)
    {
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 3;
                var t = 2 * Bayer[y & 3, x & 3] + 1;
                var r5 = Math.Min(31, (rgb[i] * 31 * 32 + t * 255) / (255 * 32));
                var g6 = Math.Min(63, (rgb[i + 1] * 63 * 32 + t * 255) / (255 * 32));
                var b5 = Math.Min(31, (rgb[i + 2] * 31 * 32 + t * 255) / (255 * 32));
                var v = (ushort)(r5 << 11 | g6 << 5 | b5);
                dst[off + (y * w + x) * 2] = (byte)v;
                dst[off + (y * w + x) * 2 + 1] = (byte)(v >> 8);
            }
    }

    /// <summary>
    /// Knurl's cover wash (ui/Plane.kt): the cover, more saturated (x1.6) and a little lifted (+18),
    /// heavily blurred, as the 40x30 RGB888 image the firmware stretches over the top of the screen.
    /// </summary>
    internal static byte[] Wash(RgbImage square)
    {
        const int s = 48;
        var small = Resize(square, s, s);
        for (var i = 0; i < small.Length; i += 3)
        {
            double r = small[i], g = small[i + 1], b = small[i + 2];
            var l = 0.213 * r + 0.715 * g + 0.072 * b;           // Android's saturation matrix weights
            small[i] = Clamp(l + (r - l) * 1.6 + 18);
            small[i + 1] = Clamp(l + (g - l) * 1.6 + 18);
            small[i + 2] = Clamp(l + (b - l) * 1.6 + 18);
        }
        for (var pass = 0; pass < 3; pass++) small = BoxBlur(small, s, s, 5);
        // the part of the (enlarged, offset) cover Knurl shows behind the top of the screen
        var crop = new byte[32 * 24 * 3];
        for (var y = 0; y < 24; y++)
            Buffer.BlockCopy(small, ((y + 8) * s + 8) * 3, crop, y * 32 * 3, 32 * 3);
        return Resize(new RgbImage(32, 24, crop), WashW, WashH);
    }

    private static byte Clamp(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private static byte[] BoxBlur(byte[] src, int w, int h, int r)
    {
        var tmp = new byte[src.Length];
        var outp = new byte[src.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0, n = 0;
                    for (var k = -r; k <= r; k++) { var xx = Math.Clamp(x + k, 0, w - 1); sum += src[(y * w + xx) * 3 + c]; n++; }
                    tmp[(y * w + x) * 3 + c] = (byte)(sum / n);
                }
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 3; c++)
                {
                    int sum = 0, n = 0;
                    for (var k = -r; k <= r; k++) { var yy = Math.Clamp(y + k, 0, h - 1); sum += tmp[(yy * w + x) * 3 + c]; n++; }
                    outp[(y * w + x) * 3 + c] = (byte)(sum / n);
                }
        return outp;
    }
}
