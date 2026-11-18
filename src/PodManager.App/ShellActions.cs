using System.Diagnostics;

namespace PodManager.App;

public static class ShellActions
{
    /// <summary>Same as "Eject" in Explorer. Must be called on an STA thread (the UI thread is).</summary>
    public static void Eject(string driveRoot)
    {
        var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application isn't available.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic? item = shell.Namespace(17)?.ParseName(driveRoot); // 17 = This PC
        if (item is null) throw new InvalidOperationException($"{driveRoot} isn't listed under This PC.");
        item.InvokeVerb("Eject");
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool SetVolumeLabelW(string rootPathName, string volumeName);

    /// <summary>Renames a drive (sets its volume label). Works without admin rights for the iPod.</summary>
    public static void SetVolumeLabel(string driveRoot, string label)
    {
        if (!SetVolumeLabelW(driveRoot, label))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }

    public static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    public static void ShowInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
}
