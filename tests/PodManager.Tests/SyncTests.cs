using PodManager.Core;

namespace PodManager.Tests;

public class SyncPlannerTests
{
    private const string Alpha = "Northbound/Compass - North (2015)/Northbound - Compass - North - 01 - First Light.flac";
    private const string Omega = "Northbound/Compass - South (2015)/Northbound - Compass - South - 01 - Reprise.flac";
    private const string Glitch = "Velvet Static/Songs for the Static in My Head, Vol. 01 (2008)/Velvet Static - Songs for the Static in My Head, Vol. 01 - 12 - Life’s a Loop.flac";
    private const string AcDc = "AC:DC/Back in Black? (1980)/AC:DC - Back in Black? - 01 - Hells Bells.flac";
    private const string Never = "Odd/Album (2020)/Odd - Album - 01 - Skip Me.flac";
    private const string Broken = "Odd/Album (2020)/Odd - Album - 02 - Broke.flac";

    [Fact]
    public void Parses_lidarr_style_names()
    {
        var n = SyncPlanner.ParseName(Glitch);
        Assert.Equal(("Velvet Static", "Songs for the Static in My Head, Vol. 01", "Life’s a Loop", 12u, 2008u), n);
        Assert.Equal(("Mira Sol", "Glass Garden", "Paper Moon", 3u, 2019u), SyncPlanner.ParseName("Mira Sol/Glass Garden (2019)/03. Paper Moon.flac"));
        Assert.Equal("Loose", SyncPlanner.ParseName("Loose.flac").Title);
    }

    [Fact]
    public void Works_out_each_songs_state()
    {
        using var lib = new TempLibrary();
        string Key(string master) => Sanitizer.LocalRelative(SyncPlanner.OutboxRelFor(master));
        // On the iPod already: Alpha (matching size), Omega (outbox has a different size = update ready)
        lib.AddSong(@"Music\" + Key(Alpha));
        var omegaLocal = lib.AddSong(@"Music\" + Key(Omega));
        lib.AddSong(@"Music\Stray\Old (1999)\01 - Old Song.mp3");                     // only on the iPod
        var listing = new ServerListing(
            Masters: [new(Alpha, 30_000_000), new(Omega, 40_000_000), new(Glitch, 35_000_000), new(AcDc, 33_000_000), new(Never, 1), new(Broken, 1)],
            Outbox: [new(SyncPlanner.OutboxRelFor(Glitch), 8_000_000), new(SyncPlanner.OutboxRelFor(Omega), new FileInfo(omegaLocal).Length + 5),
                     new("Gone/Album (2000)/Gone - Album - 01 - Orphan.mp3", 5_000_000), new("Velvet Static/x/cover.jpg", 1)]);
        var never = new HashSet<string>([Key(Never)], StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, string> { [Key(Broken)] = "Couldn't convert on the server: boom" };

        var items = SyncPlanner.Build(listing, lib.Root, "Music", never, errors).ToDictionary(i => i.Key);

        Assert.Equal(SyncStatus.OnIpod, items[Key(Alpha)].Status);
        Assert.Equal(SyncStatus.UpdateReady, items[Key(Omega)].Status);
        Assert.Equal(SyncStatus.Ready, items[Key(Glitch)].Status);
        Assert.Equal(8_000_000, items[Key(Glitch)].EstimatedBytes);
        Assert.Equal(SyncStatus.NeedsConversion, items[Key(AcDc)].Status);
        Assert.Equal(@"AC_DC\Back in Black_ (1980)\AC_DC - Back in Black_ - 01 - Hells Bells.mp3", Key(AcDc));
        Assert.Equal(SyncStatus.NeverSync, items[Key(Never)].Status);
        Assert.Equal(SyncStatus.Failed, items[Key(Broken)].Status);
        Assert.Contains("boom", items[Key(Broken)].Error);
        Assert.Equal(SyncStatus.Ready, items[@"Gone\Album (2000)\Gone - Album - 01 - Orphan.mp3"].Status); // outbox copy without master
        Assert.Equal(SyncStatus.OnlyOnIpod, items[@"Stray\Old (1999)\01 - Old Song.mp3"].Status);
        Assert.DoesNotContain(items.Keys, k => k.EndsWith("cover.jpg"));
        Assert.Equal(5, items.Values.Count(i => i.NeedsSync)); // update, ready, needs conversion, failed, orphan
    }
}

public class ServerParsingTests
{
    [Fact]
    public void Parses_combined_listing()
    {
        var l = ServerSync.ParseServerListing("M\tA/B/01.flac\t100\nO\tA/B/01.mp3\t25\ngarbage\nO\tA/B/cover.jpg\t9\r\n");
        Assert.Equal([new RemoteFile("A/B/01.flac", 100)], l.Masters);
        Assert.Equal([new RemoteFile("A/B/01.mp3", 25), new RemoteFile("A/B/cover.jpg", 9)], l.Outbox);
    }

    [Fact]
    public void Parses_converter_output()
    {
        var r = ServerSync.ParseConvertOutput(
            "{\"rel\": \"A/x.flac\", \"ok\": true, \"out\": \"A/x.mp3\", \"size\": 42}\nnoise\n{\"rel\": \"A/y.flac\", \"ok\": false, \"error\": \"missing\"}\n");
        Assert.Equal(new ConvertResult("A/x.flac", true, "A/x.mp3", 42, null), r["A/x.flac"]);
        Assert.Equal("missing", r["A/y.flac"].Error);
        Assert.Null(ServerSync.ParseConvertLine("noise"));
        Assert.Null(ServerSync.ParseConvertLine("{\"half\": "));
        Assert.Equal(new ConvertResult("A/x.flac", true, "A/x.mp3", 42, null),
            ServerSync.ParseConvertLine("{\"rel\": \"A/x.flac\", \"ok\": true, \"out\": \"A/x.mp3\", \"size\": 42}"));
    }

    [Fact]
    public async Task Hands_over_each_output_line_as_it_arrives_and_still_returns_it_all()
    {
        var lines = new List<string>();
        var r = await ProcessRunner.RunAsync("cmd.exe", ["/c", "echo one& echo two& echo three"], onStdOutLine: lines.Add);
        Assert.Equal(["one", "two", "three"], lines.Select(l => l.Trim()));
        Assert.Equal(3, r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Equal(0, r.ExitCode);
    }

    [Theory]
    [InlineData("remote open(\"/o/A/01 - Song.mp3\"): No such file or directory", null, "Not on the server")]
    [InlineData("remote open(\"/o/A/01 - Song.mp3\"): Permission denied", null, "Permission denied")]
    [InlineData("Couldn't open local file \"D:/Music/A/01 - Song.mp3.part\" for writing: No such file or directory", null, "Couldn't write it to the iPod")]
    [InlineData("Connection closed by 203.0.113.7", 1000L, "Lost the connection")]
    [InlineData("", 1000L, "Incomplete copy")]
    [InlineData("", null, "Nothing arrived")]
    public void Explains_failures(string stderr, long? got, string expected)
    {
        var exit = stderr.StartsWith("Connection") ? 255 : 0;
        Assert.Contains(expected, ServerSync.ExplainFailure("A/01 - Song.mp3", got, 5000, new ProcessResult(exit, "", stderr)));
    }
}

public class TransferLogTests
{
    [Fact]
    public void Keeps_each_songs_latest_error_until_it_succeeds_and_survives_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tl-{Guid.NewGuid():N}.json");
        try
        {
            var log = new TransferLog();
            log.Record(new TransferRun { Kind = "Full sync", Copied = 1, Failures = [new("a", "A", "boom", DateTime.Now), new("b", "B", "bang", DateTime.Now)] }, ["c"]);
            log.Save(path);
            var loaded = TransferLog.Load(path);
            Assert.Single(loaded.Runs);
            Assert.Equal(2, loaded.Runs[0].Failed);
            Assert.Equal("boom", loaded.ErrorReasons["A".ToLowerInvariant()]);

            loaded.Record(new TransferRun { Kind = "Selected", Copied = 1 }, ["a"]);
            Assert.False(loaded.CurrentErrors.ContainsKey("a"));
            Assert.True(loaded.CurrentErrors.ContainsKey("B")); // case-insensitive keys
            Assert.Equal("Selected", loaded.Runs[0].Kind);
        }
        finally { File.Delete(path); }
    }
}

public class SettingsTests
{
    [Theory]
    [InlineData("SAM'S IPOD", null)]
    [InlineData("Sam iPod", null)]
    [InlineData("", "Give the iPod a name")]
    [InlineData("Way Too Long Name", "at most 11")]
    [InlineData("My:Pod", "aren't allowed")]
    [InlineData("Pod.1", "aren't allowed")]
    [InlineData("Café", "accents")]
    public void Validates_ipod_names(string name, string? problem)
    {
        var p = IpodName.Problem(name);
        if (problem is null) Assert.Null(p);
        else Assert.Contains(problem, p);
    }

    [Fact]
    public void Validates_server_settings()
    {
        Assert.Empty(new SyncSettings().Validate());
        Assert.Contains(new SyncSettings { Remote = "user @ host" }.Validate(), p => p.Contains("user@host"));
        Assert.Contains(new SyncSettings { RemoteRoot = "/srv/music/library" }.Validate(), p => p.Contains("can't be the lossless library"));
        Assert.Contains(new SyncSettings { LibraryRoot = "tank/music" }.Validate(), p => p.Contains("absolute"));
        Assert.Contains(new SyncSettings { TargetFolder = "Mu/sic" }.Validate(), p => p.Contains("plain folder"));
        Assert.Contains(new SyncSettings { TranscoderContainer = "rm -rf" }.Validate(), p => p.Contains("container name"));
    }

    [Fact]
    public void Saves_and_loads_settings_with_defaults_for_missing_fields()
    {
        var old = AppPaths.DataDirectory;
        AppPaths.DataDirectory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllText(SyncSettings.FilePath, "{\"Remote\": \"bob@box\"}"); // an older settings file
            var loaded = SyncSettings.Load();
            Assert.Equal("bob@box", loaded.Remote);
            Assert.True(loaded.CheckSyncOnConnect);
            Assert.False(loaded.EjectAfterSync);

            (loaded with { EjectAfterSync = true, TargetFolder = "Tunes" }).Save();
            var again = SyncSettings.Load();
            Assert.True(again.EjectAfterSync);
            Assert.Equal("Tunes", again.TargetFolder);
        }
        finally
        {
            Directory.Delete(AppPaths.DataDirectory, true);
            AppPaths.DataDirectory = old;
        }
    }
}

public class KnurlDeviceTests
{
    [Fact]
    public void Creates_a_zeroed_battery_log_once_and_never_overwrites_it()
    {
        using var lib = new TempLibrary();
        var log = Path.Combine(lib.Root, ".knurl", "battery.log");
        Assert.True(KnurlDevice.EnsureBatteryLog(lib.Root));
        var bytes = File.ReadAllBytes(log);
        Assert.Equal(KnurlDevice.BatteryLogBytes, bytes.Length);
        Assert.All(bytes, b => Assert.Equal(0, b));
        File.WriteAllBytes(log, [0xB0, 1, 2, 3]);                     // the firmware has written to it
        Assert.False(KnurlDevice.EnsureBatteryLog(lib.Root));
        Assert.Equal(4, new FileInfo(log).Length);
    }

    [Fact]
    public void Creates_zeroed_settings_once_and_never_overwrites_them()
    {
        using var lib = new TempLibrary();
        var file = Path.Combine(lib.Root, ".knurl", "settings.bin");
        Assert.True(KnurlDevice.EnsureSettings(lib.Root));
        var bytes = File.ReadAllBytes(file);
        Assert.Equal(KnurlDevice.SettingsBytes, bytes.Length);
        Assert.All(bytes, b => Assert.Equal(0, b));
        bytes[0] = (byte)'K';                                          // the iPod has saved its settings
        File.WriteAllBytes(file, bytes);
        Assert.False(KnurlDevice.EnsureSettings(lib.Root));
        Assert.Equal((byte)'K', File.ReadAllBytes(file)[0]);
    }

    [Fact]
    public void Describes_the_firmware_and_finds_the_ipod_by_its_knurl_folder()
    {
        using var lib = new TempLibrary();
        Assert.Null(KnurlDevice.Describe(lib.Root));
        File.WriteAllBytes(KnurlDevice.FirmwarePath(lib.Root), [1, 2, 3]);
        Assert.StartsWith("Knurl (built ", KnurlDevice.Describe(lib.Root));
        Assert.True(IpodLocator.IsIpodRoot(lib.Root));
        var plain = Directory.CreateTempSubdirectory().FullName;
        try { Assert.False(IpodLocator.IsIpodRoot(plain)); Assert.False(KnurlDevice.EnsureBatteryLog(plain)); }
        finally { Directory.Delete(plain, true); }
    }
}
