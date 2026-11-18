using System.Text.RegularExpressions;

namespace PodManager.Core;

/// <summary>
/// Maps server (Linux) relative paths to names FAT32/Windows can hold. Changing these rules makes
/// songs already on the iPod look missing (they'd sync again under their new names).
/// </summary>
public static partial class Sanitizer
{
    [GeneratedRegex(@"[<>:""\\|?*\x00-\x1F]")]
    private static partial Regex Illegal();

    public static string Segment(string name) => Illegal().Replace(name, "_").TrimEnd(' ', '.');

    /// <summary>"AC:DC/Back in Black?/01.flac" -> "AC_DC\Back in Black_\01.flac"</summary>
    public static string LocalRelative(string remoteRelative) =>
        string.Join('\\', remoteRelative.Split('/').Select(Segment));
}
