using System.Text.Json.Serialization;

namespace PodManager.Core;

/// <summary>One audio file on the iPod, with the tags read from it.</summary>
public sealed record Track
{
    /// <summary>Absolute path on this PC (drive letter can change between plug-ins).</summary>
    public required string FullPath { get; init; }

    /// <summary>Path relative to the library root, using '\' (e.g. Music\Artist\Album\01 - Song.flac).</summary>
    public required string RelativePath { get; init; }

    public long Size { get; init; }
    public DateTime ModifiedUtc { get; init; }

    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string AlbumArtist { get; init; } = "";
    public string Album { get; init; } = "";
    public string Genre { get; init; } = "";
    public uint Year { get; init; }
    public uint TrackNumber { get; init; }
    public uint DiscNumber { get; init; }

    public TimeSpan Duration { get; init; }
    public int Bitrate { get; init; }
    public int SampleRate { get; init; }
    public int BitsPerSample { get; init; }
    public string Format { get; init; } = "";
    public bool HasEmbeddedArt { get; init; }

    /// <summary>True for songs iTunes put in iPod_Control. Those are read-only here, because
    /// editing or deleting them behind iTunes' back corrupts its database.</summary>
    public bool IsITunesManaged { get; init; }

    [JsonIgnore]
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown Artist" : Artist;

    [JsonIgnore]
    public string DisplayAlbumArtist =>
        !string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist : DisplayArtist;

    [JsonIgnore]
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "Unknown Album" : Album;

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Path.GetFileNameWithoutExtension(FullPath) : Title;

    /// <summary>"FLAC 24/96", "FLAC 16/44.1", "MP3 320k" ...</summary>
    [JsonIgnore]
    public string QualityText
    {
        get
        {
            var lossless = Format is "FLAC" or "ALAC" or "WAV" or "AIFF" or "APE" or "WV";
            if (lossless && SampleRate > 0)
            {
                var khz = SampleRate / 1000.0;
                var rate = khz % 1 == 0 ? khz.ToString("0") : khz.ToString("0.#");
                return BitsPerSample > 0 ? $"{Format} {BitsPerSample}/{rate}" : $"{Format} {rate}kHz";
            }
            return Bitrate > 0 ? $"{Format} {Bitrate}k" : Format;
        }
    }
}
