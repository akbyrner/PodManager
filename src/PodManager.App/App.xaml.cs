using System.Windows;
using System.Windows.Threading;
using PodManager.Core;

namespace PodManager.App;

public partial class App : Application
{
    public static StartupOptions Options { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        Options = StartupOptions.Parse(e.Args);

        if (Options.MakeDemo is { } demoFolder)
        {
            DemoLibraryBuilder.Build(demoFolder);
            Shutdown(0);
            return;
        }

        if (Options.BuildIndex is { } indexRoot)
        {
            var code = 0;
            try
            {
                var tracks = LibraryScanner.Scan(indexRoot, cache: null, progress: null);
                var r = KnurlIndex.Write(indexRoot, tracks, new WpfImageDecoder());
                File.WriteAllText(Path.Combine(indexRoot, KnurlIndex.Folder, "build.txt"),
                    $"{DateTime.Now:u}: {r.Tracks} songs, {r.Albums} albums ({r.AlbumsWithArt} with art), {r.Artists} artists; " +
                    $"library.bin {r.LibraryBytes:N0} bytes, art.bin {r.ArtBytes:N0} bytes\n");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "podmanager-build-index-error.txt"), ex.ToString());
                code = 1;
            }
            Shutdown(code);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private bool _showingError;
    private DateTime _lastErrorShown;

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var log = Path.Combine(AppPaths.DataDirectory, "error.log");
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.AppendAllText(log, $"{DateTime.Now:u}  {e.Exception}\n\n");
        }
        catch (IOException) { }

        e.Handled = true;
        if (Options.Snapshot is not null)
        {
            Shutdown(1);
            return;
        }
        // The message box pumps messages, which runs layout again. If layout is what threw, a second
        // error arrives while the first box is open, and showing a box for each one recursed until the
        // stack overflowed (a crash in dwrite.dll). Show one box at a time, at most every few seconds.
        if (_showingError || DateTime.UtcNow - _lastErrorShown < TimeSpan.FromSeconds(5)) return;
        _showingError = true;
        try
        {
            MessageBox.Show(MainWindow, $"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were written to {log}.",
                "PodManager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
            _lastErrorShown = DateTime.UtcNow;
        }
    }
}

/// <summary>
/// Command-line options. Normal use needs none.
///   --library &lt;folder&gt;   use a folder instead of the iPod (for testing)
///   --snapshot &lt;png&gt;     render the window to a PNG after loading, then exit (for testing)
///   --view &lt;name&gt;        songs | albums | album | artists | sync | sync-missing | log | settings | edit (with --snapshot)
///   --make-demo &lt;folder&gt; create a fake iPod library with demo songs and art, then exit
///   --build-index &lt;root&gt; write the Knurl firmware's library index (.knurl\) for a library, then exit
/// </summary>
public sealed record StartupOptions
{
    public string? Library { get; init; }
    public string? Snapshot { get; init; }
    public string View { get; init; } = "albums";
    public string? MakeDemo { get; init; }
    public string? BuildIndex { get; init; }

    public static StartupOptions Parse(string[] args)
    {
        var o = new StartupOptions();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            o = args[i].ToLowerInvariant() switch
            {
                "--library" => o with { Library = Next() },
                "--snapshot" => o with { Snapshot = Next() },
                "--view" => o with { View = Next().ToLowerInvariant() },
                "--make-demo" => o with { MakeDemo = Next() },
                "--build-index" => o with { BuildIndex = Next() },
                _ => o,
            };
        }
        return o;
    }
}
