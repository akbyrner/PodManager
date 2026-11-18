using System.Buffers.Binary;
using System.Text;
using PodManager.Core;

namespace PodManager.Tests;

/// <summary>Checks library.bin against the layout in ipod-firmware's firmware/src/library/kidx.h.</summary>
public class KnurlIndexTests
{
    private static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
    private static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));

    private static string Str(byte[] b, uint stringsOff, uint off)
    {
        var start = (int)(stringsOff + off);
        var end = Array.IndexOf(b, (byte)0, start);
        return Encoding.UTF8.GetString(b, start, end - start);
    }

    [Fact]
    public void Writes_a_sorted_index_the_firmware_can_read()
    {
        using var lib = new TempLibrary();
        lib.AddSong(@"Music\The Zebras\Second (2020)\02 - Beta.mp3", title: "Beta", artist: "The Zebras", album: "Second", track: 2, year: 2020);
        lib.AddSong(@"Music\The Zebras\Second (2020)\01 - Alpha.mp3", title: "Alpha", artist: "The Zebras", album: "Second", track: 1, year: 2020);
        lib.AddSong(@"Music\The Zebras\First (2018)\01 - Zulu.mp3", title: "Zulu", artist: "The Zebras", album: "First", track: 1, year: 2018);
        lib.AddSong(@"Music\Aardvark\Ünïcode (2001)\01 - Café.mp3", title: "Café", artist: "Aardvark", album: "Ünïcode", track: 1, year: 2001);
        File.WriteAllText(Path.Combine(lib.Root, @"Music\Aardvark\Ünïcode (2001)\notes.flac"), "not played by the firmware");

        var tracks = LibraryScanner.Scan(lib.Root, cache: null, progress: null);
        var r = KnurlIndex.Write(lib.Root, tracks, decoder: null);
        Assert.Equal((4, 3, 2, 0), (r.Tracks, r.Albums, r.Artists, r.AlbumsWithArt));

        var b = File.ReadAllBytes(Path.Combine(lib.Root, ".knurl", "library.bin"));
        Assert.Equal("KNURLIDX", Encoding.ASCII.GetString(b, 0, 8));
        Assert.Equal(1u, U32(b, 8));
        Assert.Equal((uint)b.Length, U32(b, 12));
        Assert.Equal((4u, 3u, 2u), (U32(b, 16), U32(b, 20), U32(b, 24)));
        uint tracksOff = U32(b, 28), albumsOff = U32(b, 32), artistsOff = U32(b, 36), songsOff = U32(b, 40),
             byTitleOff = U32(b, 44), byAddedOff = U32(b, 48), stringsOff = U32(b, 52);

        // artists sorted ignoring "The": Aardvark, The Zebras
        Assert.Equal("Aardvark", Str(b, stringsOff, U32(b, (int)artistsOff)));
        Assert.Equal("The Zebras", Str(b, stringsOff, U32(b, (int)artistsOff + 12)));
        Assert.Equal((1u, 2), (U32(b, (int)artistsOff + 12 + 4), (int)U16(b, (int)artistsOff + 12 + 8)));

        // albums by artist then year: Ünïcode, First (2018), Second (2020)
        string AlbumTitle(int i) => Str(b, stringsOff, U32(b, (int)albumsOff + i * 32));
        Assert.Equal(["Ünïcode", "First", "Second"], [AlbumTitle(0), AlbumTitle(1), AlbumTitle(2)]);
        Assert.Equal(0xFFFF, U16(b, (int)albumsOff + 2 * 32 + 18));       // no art
        Assert.Equal(2, U16(b, (int)albumsOff + 2 * 32 + 16));            // two tracks

        // tracks grouped by album in track order; paths use '/'
        string TrackTitle(int i) => Str(b, stringsOff, U32(b, (int)tracksOff + i * 24));
        Assert.Equal(["Café", "Zulu", "Alpha", "Beta"], [TrackTitle(0), TrackTitle(1), TrackTitle(2), TrackTitle(3)]);
        Assert.Equal("Music/The Zebras/Second (2020)/01 - Alpha.mp3", Str(b, stringsOff, U32(b, (int)tracksOff + 2 * 24 + 8)));
        Assert.Equal(2, U16(b, (int)tracksOff + 3 * 24 + 20));            // Beta's album index

        // songs by title: Alpha, Beta, Café, Zulu -> track indexes 2, 3, 0, 1
        Assert.Equal([2, 3, 0, 1], Enumerable.Range(0, 4).Select(i => (int)U16(b, (int)songsOff + i * 2)));
        // albums by title: First, Second, Ünïcode -> 1, 2, 0
        Assert.Equal([1, 2, 0], Enumerable.Range(0, 3).Select(i => (int)U16(b, (int)byTitleOff + i * 2)));
        Assert.All(new[] { tracksOff, albumsOff, artistsOff, songsOff, byTitleOff, byAddedOff }, o => Assert.Equal(0u, o % 2));

        var art = File.ReadAllBytes(Path.Combine(lib.Root, ".knurl", "art.bin"));
        Assert.Equal("KNURLART", Encoding.ASCII.GetString(art, 0, 8));
        Assert.Equal(0u, U32(art, 12));
    }

    [Fact]
    public void Sort_key_ignores_a_leading_the() =>
        Assert.Equal(["Beatles", "Theory", "The"], new[] { "The Beatles", "Theory", "The" }.Select(KnurlIndex.SortKey));
}
