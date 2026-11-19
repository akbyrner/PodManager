namespace PodManager.Core;

/// <summary>
/// The Knurl firmware's side of the iPod (<c>.knurl\</c>; the firmware lives in the knurl-ipod repository).
/// Knurl never writes the FAT file system: it only overwrites sectors inside files that already exist at
/// their full size (firmware/device/store.c), so PodManager creates those files zero-filled.
/// </summary>
public static class KnurlDevice
{
    /// <summary>The battery log: 8-byte records, one a minute, so 1 MB is about three months.</summary>
    public const int BatteryLogBytes = 1 << 20;

    public static string FirmwarePath(string root) => Path.Combine(root, KnurlIndex.Folder, "knurl.ipod");

    /// <summary>"Knurl (built 2026-11-18 16:39)", or null when there's no Knurl firmware on the drive.</summary>
    public static string? Describe(string root)
    {
        var f = new FileInfo(FirmwarePath(root));
        return f.Exists ? $"Knurl (built {f.LastWriteTime:yyyy-MM-dd HH:mm})" : null;
    }

    /// <summary>Creates <c>.knurl\battery.log</c> if it's missing (written as zeros: the firmware treats a zero
    /// record as "not written yet"). Never touches an existing log. True if it was created.</summary>
    public static bool EnsureBatteryLog(string root) => EnsureZeroFile(root, "battery.log", BatteryLogBytes);

    /// <summary>The iPod's settings (shuffle, repeat, volume, backlight, auto-off): one 512-byte sector.</summary>
    public const int SettingsBytes = 512;

    /// <summary>Creates <c>.knurl\settings.bin</c> if it's missing, zero-filled (the firmware reads zeros as "the
    /// defaults"). Never touches existing settings. True if it was created.</summary>
    public static bool EnsureSettings(string root) => EnsureZeroFile(root, "settings.bin", SettingsBytes);

    private static bool EnsureZeroFile(string root, string name, int bytes)
    {
        var dir = Path.Combine(root, KnurlIndex.Folder);
        if (!Directory.Exists(dir)) return false;
        var path = Path.Combine(dir, name);
        if (File.Exists(path)) return false;
        var zeros = new byte[Math.Min(bytes, 64 * 1024)];
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            for (var done = 0; done < bytes; done += zeros.Length)
                fs.Write(zeros, 0, Math.Min(zeros.Length, bytes - done));
        return true;
    }
}
