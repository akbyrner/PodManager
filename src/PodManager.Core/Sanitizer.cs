using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PodManager.Core;

/// <summary>
/// Maps server (Linux) relative paths to names FAT32/Windows can hold. Changing these rules makes
/// songs already on the iPod look missing (they'd sync again under their new names).
/// </summary>
public static partial class Sanitizer
{
    /// <summary>
    /// Longest path, relative to the iPod's music folder, that's kept as it is. Windows (and the sftp
    /// copier) can't write paths of 260+ characters: "D:\Music\" (9) + 241 = 250, plus ".part" while copying.
    /// </summary>
    public const int MaxRelativeLength = 241;

    /// <summary>Folder names in a too-long path are cut to this length.</summary>
    private const int MaxFolderLength = 64;

    /// <summary>A shortened file name is never cut below this.</summary>
    private const int MinFileLength = 40;

    [GeneratedRegex(@"[<>:""\\|?*\x00-\x1F]")]
    private static partial Regex Illegal();

    /// <summary>Where a track number starts ("01 - "); the last one in a name begins its track-and-title tail.</summary>
    [GeneratedRegex(@"(?<=^|\s)\d{1,3} - ")]
    private static partial Regex TrackNumber();

    public static string Segment(string name) => Illegal().Replace(name, "_").TrimEnd(' ', '.');

    /// <summary>
    /// "AC:DC/Back in Black?/01.flac" -> "AC_DC\Back in Black_\01.flac". Paths longer than
    /// <see cref="MaxRelativeLength"/> get shortened names (see <see cref="Shorten"/>); shorter ones never change.
    /// </summary>
    public static string LocalRelative(string remoteRelative)
    {
        var parts = remoteRelative.Split('/').Select(Segment).ToArray();
        var joined = string.Join('\\', parts);
        return joined.Length <= MaxRelativeLength ? joined : string.Join('\\', Shorten(parts));
    }

    /// <summary>
    /// Cuts long folder names to <see cref="MaxFolderLength"/>, then the file name to whatever room is left.
    /// Every cut name ends in "~" plus 6 characters of a hash of the full name, so two albums or songs that
    /// start the same never collide, and the same input always gives the same name. A file keeps its track
    /// number and title ("... Planet E~3f9a1c 01 - Motor Spirit.mp3").
    /// </summary>
    private static string[] Shorten(string[] parts)
    {
        for (var i = 0; i < parts.Length - 1; i++)
            if (parts[i].Length > MaxFolderLength) parts[i] = Cut(parts[i], MaxFolderLength);
        var folders = parts[..^1].Sum(p => p.Length + 1);
        var room = Math.Max(MaxRelativeLength - folders, MinFileLength);
        if (parts[^1].Length > room) parts[^1] = CutFileName(parts[^1], room);
        return parts;
    }

    private static string Cut(string name, int max, string? hashOf = null) =>
        name[..(max - 7)].TrimEnd(' ', '.', '-', ',', ';', '_') + "~" + Hash(hashOf ?? name);

    private static string CutFileName(string name, int max)
    {
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        var marks = TrackNumber().Matches(stem);
        var tail = marks.Count > 0 ? stem[marks[^1].Index..] : "";      // "01 - Motor Spirit"
        var head = max - ext.Length - 8 - tail.Length;                  // 8 = "~" + hash + " "
        if (tail.Length == 0 || head < 12)
            return Cut(stem, max - ext.Length, name) + ext;
        return stem[..head].TrimEnd(' ', '.', '-', ',', ';', '_') + "~" + Hash(name) + " " + tail + ext;
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..6].ToLowerInvariant();
}
