namespace PodManager.Core;

/// <summary>Where the library lives: the iPod's drive, or (for testing) any folder.</summary>
public sealed record LibraryLocation(string Root, string Label, bool IsDevice);

public static class IpodLocator
{
    /// <summary>Finds the iPod: a ready, non-system drive with a .knurl folder (the Knurl firmware; an older
    /// Rockbox install has .rockbox instead).</summary>
    public static LibraryLocation? FindIpod()
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Removable or DriveType.Fixed))
                    continue;
                var root = drive.RootDirectory.FullName;
                if (string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsIpodRoot(root))
                    continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "iPod" : drive.VolumeLabel;
                return new LibraryLocation(root, label, IsDevice: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    public static bool IsIpodRoot(string root) =>
        Directory.Exists(Path.Combine(root, KnurlIndex.Folder)) || Directory.Exists(Path.Combine(root, ".rockbox"));

    public static LibraryLocation FromFolder(string folder)
    {
        var full = Path.GetFullPath(folder);
        return new LibraryLocation(full, new DirectoryInfo(full).Name, IsDevice: false);
    }

    public static (long Total, long Free) GetSpace(string root)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
        return (drive.TotalSize, drive.AvailableFreeSpace);
    }
}
