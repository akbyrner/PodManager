using PodManager.Core;

namespace PodManager.Tests;

public class SanitizerTests
{
    [Theory]
    [InlineData("AC:DC/Back in Black?/01 - Hells Bells.flac", @"AC_DC\Back in Black_\01 - Hells Bells.flac")]
    [InlineData("Daft Punk/Discovery (2001)/it's a test & more.flac", @"Daft Punk\Discovery (2001)\it's a test & more.flac")]
    [InlineData("Artist./Album /a\"b*c|d<e>f.mp3", @"Artist\Album\a_b_c_d_e_f.mp3")]
    public void Matches_the_sync_script(string remote, string expected) =>
        Assert.Equal(expected, Sanitizer.LocalRelative(remote));
}

public class ScannerTests
{
    [Fact]
    public void Reads_tags_falls_back_to_paths_and_flags_itunes_songs()
    {
        using var lib = new TempLibrary();
        lib.AddSong(@"Music\Band\Album (2001)\01 - Song.mp3", title: "Real Title", artist: "Real Artist", album: "Real Album", track: 1, year: 2001);
        lib.AddSong(@"Music\Artist Two\Second Album (1999)\07 - Untagged Song.mp3");
        var itunes = lib.AddSong(@"iPod_Control\Music\F00\ABCD.mp3", title: "From iTunes", artist: "Old");
        new DirectoryInfo(Path.Combine(lib.Root, "iPod_Control")).Attributes |= FileAttributes.Hidden;
        lib.AddSong(@".rockbox\ignored.mp3", title: "Should not appear");
        File.WriteAllText(Path.Combine(lib.Root, @"Music\Band\Album (2001)\cover.txt"), "not audio");

        var tracks = LibraryScanner.Scan(lib.Root, cache: null, progress: null);

        Assert.Equal(3, tracks.Count);
        var tagged = Assert.Single(tracks, t => t.Title == "Real Title");
        Assert.Equal(("Real Artist", "Real Album", 1u, 2001u, "MP3"), (tagged.Artist, tagged.Album, tagged.TrackNumber, tagged.Year, tagged.Format));
        Assert.True(tagged.Duration > TimeSpan.FromSeconds(1.5));

        var untagged = Assert.Single(tracks, t => t.Title == "Untagged Song");
        Assert.Equal(("Artist Two", "Second Album", 7u, 1999u), (untagged.Artist, untagged.Album, untagged.TrackNumber, untagged.Year));
        Assert.False(untagged.IsITunesManaged);

        var fromItunes = Assert.Single(tracks, t => t.FullPath == itunes);
        Assert.True(fromItunes.IsITunesManaged);
        Assert.Equal("From iTunes", fromItunes.Title);
    }

    [Fact]
    public void Cache_is_reused_until_a_file_changes()
    {
        using var lib = new TempLibrary();
        var path = lib.AddSong(@"Music\A\B\01 - One.mp3", title: "One", artist: "A", album: "B");
        var cachePath = Path.Combine(lib.Root, "..", Path.GetFileName(lib.Root) + "-cache.json");
        try
        {
            LibraryScanner.Scan(lib.Root, new LibraryCache(cachePath), null);

            // Poison the cached copy; an unchanged file must come back from the cache, not the disk.
            var cache = new LibraryCache(cachePath);
            Assert.Equal(1, cache.Count);
            var info = new FileInfo(path);
            Assert.True(cache.TryGet(@"Music\A\B\01 - One.mp3", info.Length, info.LastWriteTimeUtc, out var hit));
            cache.Replace([hit with { Title = "From cache" }]);
            Assert.Equal("From cache", LibraryScanner.Scan(lib.Root, new LibraryCache(cachePath), null).Single().Title);

            // Change the file: it must be re-read.
            using (var f = TagLib.File.Create(path)) { f.Tag.Title = "Changed"; f.Save(); }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            Assert.Equal("Changed", LibraryScanner.Scan(lib.Root, new LibraryCache(cachePath), null).Single().Title);
        }
        finally { File.Delete(cachePath); }
    }
}

public class TagEditorTests
{
    [Fact]
    public void Edits_tags_and_artwork_and_leaves_null_fields_alone()
    {
        using var lib = new TempLibrary();
        var path = lib.AddSong(@"Music\A\B\01 - One.mp3", title: "One", artist: "A", album: "B", track: 1, year: 2000);
        var track = LibraryScanner.ReadTrack(lib.Root, path);

        var edited = TagEditor.Apply(lib.Root, track, new TagEdit { Artist = "New Artist", Year = 2024, Artwork = TempLibrary.FakeJpeg });

        Assert.Equal(("One", "New Artist", "B", 2024u, 1u), (edited.Title, edited.Artist, edited.Album, edited.Year, edited.TrackNumber));
        Assert.True(edited.HasEmbeddedArt);
        Assert.Equal(TempLibrary.FakeJpeg, ArtworkProvider.Load(edited));

        var cleared = TagEditor.Apply(lib.Root, edited, new TagEdit { RemoveArtwork = true, Genre = "Rock" });
        Assert.False(cleared.HasEmbeddedArt);
        Assert.Equal("Rock", cleared.Genre);
    }

    [Fact]
    public void Refuses_itunes_songs()
    {
        using var lib = new TempLibrary();
        var path = lib.AddSong(@"iPod_Control\Music\F01\WXYZ.mp3", title: "iTunes");
        var track = LibraryScanner.ReadTrack(lib.Root, path);
        Assert.Throws<InvalidOperationException>(() => TagEditor.Apply(lib.Root, track, new TagEdit { Title = "x" }));
    }

    [Fact]
    public void Artwork_falls_back_to_cover_file_in_album_folder()
    {
        using var lib = new TempLibrary();
        var path = lib.AddSong(@"Music\A\B\01 - One.mp3", title: "One");
        File.WriteAllBytes(Path.Combine(lib.Root, @"Music\A\B\cover.jpg"), TempLibrary.FakeJpeg);
        Assert.Equal(TempLibrary.FakeJpeg, ArtworkProvider.Load(LibraryScanner.ReadTrack(lib.Root, path)));
    }
}

public class DeleterTests
{
    [Fact]
    public void Deletes_songs_and_prunes_empty_album_and_artist_folders_but_keeps_Music()
    {
        using var lib = new TempLibrary();
        var a = lib.AddSong(@"Music\Artist\Album\01 - A.mp3", title: "A");
        var b = lib.AddSong(@"Music\Artist\Album\02 - B.mp3", title: "B");
        File.WriteAllBytes(Path.Combine(lib.Root, @"Music\Artist\Album\cover.jpg"), TempLibrary.FakeJpeg);
        var keep = lib.AddSong(@"Music\Other\Album\01 - Keep.mp3", title: "Keep");
        var tracks = new[] { a, b }.Select(p => LibraryScanner.ReadTrack(lib.Root, p)).ToList();

        var result = TrackDeleter.Delete(lib.Root, tracks);

        Assert.Equal(2, result.Deleted);
        Assert.Empty(result.Failures);
        Assert.Equal(2, result.FoldersRemoved); // Album, then Artist
        Assert.False(Directory.Exists(Path.Combine(lib.Root, @"Music\Artist")));
        Assert.True(Directory.Exists(Path.Combine(lib.Root, "Music")));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void Keeps_a_folder_that_still_has_songs_and_refuses_itunes_songs()
    {
        using var lib = new TempLibrary();
        var a = lib.AddSong(@"Music\Artist\Album\01 - A.mp3", title: "A");
        lib.AddSong(@"Music\Artist\Album\02 - B.mp3", title: "B");
        var itunes = lib.AddSong(@"iPod_Control\Music\F00\AAAA.mp3", title: "iTunes");

        var result = TrackDeleter.Delete(lib.Root, [LibraryScanner.ReadTrack(lib.Root, a), LibraryScanner.ReadTrack(lib.Root, itunes)]);

        Assert.Equal(1, result.Deleted);
        Assert.Single(result.Failures);
        Assert.True(File.Exists(itunes));
        Assert.True(Directory.Exists(Path.Combine(lib.Root, @"Music\Artist\Album")));
    }
}

public class ServerSyncTests
{
    [Fact]
    public void Parses_find_output()
    {
        var files = ServerSync.ParseListing("Daft Punk/Discovery/01.flac\t300000\nbad line\nA/B/x\ty\n\nAC:DC/Back?/02.flac\t150\r\n");
        Assert.Equal([new RemoteFile("Daft Punk/Discovery/01.flac", 300000), new RemoteFile("AC:DC/Back?/02.flac", 150)], files);
    }

    [Fact]
    public void Finds_missing_and_resized_files_grouped_by_album()
    {
        using var lib = new TempLibrary();
        var present = lib.AddSong(@"Music\AC_DC\Back in Black_\01 - Hells Bells.mp3");
        var size = new FileInfo(present).Length;
        var resized = lib.AddSong(@"Music\AC_DC\Back in Black_\02 - Shoot.mp3");

        var remote = new[]
        {
            new RemoteFile("AC:DC/Back in Black?/01 - Hells Bells.mp3", size),          // already there
            new RemoteFile("AC:DC/Back in Black?/02 - Shoot.mp3", new FileInfo(resized).Length + 1), // changed
            new RemoteFile("Daft Punk/Discovery (2001)/01 - One More Time.flac", 5),      // missing
            new RemoteFile("Daft Punk/Discovery (2001)/02 - Aerodynamic.flac", 6),        // missing
        };

        var albums = ServerSync.FindMissing(remote, lib.Root, "Music");

        Assert.Equal(2, albums.Count);
        Assert.Equal(("AC:DC", "Back in Black?", 1), (albums[0].Artist, albums[0].Album, albums[0].Files.Count));
        Assert.Equal(("Daft Punk", "Discovery (2001)", 2, 11L), (albums[1].Artist, albums[1].Album, albums[1].Files.Count, albums[1].TotalBytes));
    }

    [Theory]
    [InlineData("/srv/music/ipod-outbox", true, true)]
    [InlineData("/srv/music/ipod-outbox/", true, true)]
    [InlineData("/srv/music/ipod-outbox", false, false)]
    [InlineData("/srv/music/library", true, false)]   // the lossless library: never
    [InlineData("/srv/music/ipod-outbox-old", true, false)]
    public void Only_ever_deletes_from_the_outbox(string root, bool deleteSetting, bool expected) =>
        Assert.Equal(expected, new SyncSettings { RemoteRoot = root, DeleteAfterTransfer = deleteSetting }.CanDelete);

    [Fact]
    public async Task Remove_is_a_no_op_when_deleting_is_not_allowed()
    {
        var sync = new ServerSync(new SyncSettings { RemoteRoot = "/srv/music/library", Remote = "nobody@invalid.example" });
        Assert.Equal(0, await sync.RemoveFromServerAsync([new RemoteFile("a/b.flac", 1)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Escapes_for_shell_and_sftp()
    {
        Assert.Equal("'it'\\''s'", ServerSync.ShellQuote("it's"));
        Assert.Equal("a\\\"b\\\\c", ServerSync.SftpEscape("a\"b\\c"));
        Assert.Equal("\"/x/it's.mp3\"", ServerSync.SftpQuote("/x/it's.mp3"));
        Assert.Equal("'/x/a \"b\".mp3'", ServerSync.SftpQuote("/x/a \"b\".mp3"));
        Assert.Null(ServerSync.SftpQuote("/x/it's \"b\".mp3"));
    }
}

public class ExclusionListTests
{
    [Fact]
    public void Round_trips_keys_case_insensitively_and_hides_its_folder()
    {
        using var lib = new TempLibrary();
        Assert.Empty(ExclusionList.Load(lib.Root));

        ExclusionList.Add(lib.Root, [@"AC_DC\Back in Black_\01 - Hells Bells.flac", @"B\C\d.mp3"]);
        var set = ExclusionList.Load(lib.Root);
        Assert.Contains(ExclusionList.KeyFor(new RemoteFile("AC:DC/Back in Black?/01 - Hells Bells.flac", 1)), set);
        Assert.Contains(@"b\c\D.MP3", set);
        Assert.True(new DirectoryInfo(Path.Combine(lib.Root, ".podmanager")).Attributes.HasFlag(FileAttributes.Hidden));

        ExclusionList.Remove(lib.Root, [@"B\C\d.mp3"]);
        Assert.Single(ExclusionList.Load(lib.Root));
    }

    [Fact]
    public void Track_keys_are_relative_to_the_target_folder_and_skip_other_folders()
    {
        using var lib = new TempLibrary();
        var music = LibraryScanner.ReadTrack(lib.Root, lib.AddSong(@"Music\A\B\01 - x.mp3", title: "x"));
        var itunes = LibraryScanner.ReadTrack(lib.Root, lib.AddSong(@"iPod_Control\Music\F00\X.mp3", title: "y"));
        Assert.Equal(@"A\B\01 - x.mp3", ExclusionList.KeyFor(music, "Music"));
        Assert.Null(ExclusionList.KeyFor(itunes, "Music"));
    }

    [Fact]
    public void Scanner_ignores_the_podmanager_folder()
    {
        using var lib = new TempLibrary();
        lib.AddSong(@".podmanager\stray.mp3", title: "hidden");
        Assert.Empty(LibraryScanner.Scan(lib.Root, null, null));
    }
}

/// <summary>Talks to the real server in your settings.json. Run on demand: dotnet test --project tests/PodManager.Tests -- --explicit only</summary>
public class LiveServerTests
{
    [Fact(Explicit = true)]
    public async Task Lists_and_downloads_two_real_files()
    {
        using var lib = new TempLibrary();
        // Never delete real outbox files from a test.
        var sync = new ServerSync(SyncSettings.Load() with { DeleteAfterTransfer = false });
        Assert.False(sync.Settings.CanDelete);
        var remote = await sync.ListRemoteAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(remote);

        var two = remote.OrderBy(f => f.Size).Take(2).ToList();
        var albums = ServerSync.FindMissing(two, lib.Root, "Music");
        var progress = new List<SyncProgress>();
        var result = await sync.DownloadAsync(lib.Root, albums, new Progress<SyncProgress>(progress.Add), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Copied);
        Assert.Empty(result.Failed);
        foreach (var f in two)
            Assert.Equal(f.Size, new FileInfo(ServerSync.LocalPath(lib.Root, "Music", f)).Length);
        Assert.Empty(Directory.GetFiles(lib.Root, "*.part", SearchOption.AllDirectories));
        Assert.Empty(ServerSync.FindMissing(two, lib.Root, "Music"));

        Assert.All(two, f => Assert.True(File.Exists(ServerSync.LocalPath(lib.Root, "Music", f))));
    }

    /// <summary>Uses a throwaway outbox (/tmp/synctest/ipod-outbox) that the test operator fills first.</summary>
    [Fact(Explicit = true)]
    public async Task Copies_then_deletes_from_a_test_outbox_only_what_was_verified()
    {
        using var lib = new TempLibrary();
        var sync = new ServerSync(SyncSettings.Load() with { RemoteRoot = "/tmp/synctest/ipod-outbox", DeleteAfterTransfer = true });
        Assert.True(sync.Settings.CanDelete);
        var ct = TestContext.Current.CancellationToken;
        var test = (await sync.ListRemoteAsync(ct)).Where(f => f.RelativePath.StartsWith("Test Artist/")).ToList();
        // Expected contents: cover.jpg, 01 - It's fine.mp3, 02 - A "Quoted" Song.mp3, 03 - It's "both".mp3
        Assert.Equal(4, test.Count);

        var result = await sync.DownloadAsync(lib.Root, ServerSync.FindMissing(test, lib.Root, "Music"), null, ct);

        Assert.Equal(3, result.Copied);
        Assert.Equal(["Test Artist/Test Album (2020)/03 - It's \"both\".mp3"], result.Failed.Select(f => f.RelativePath));
        Assert.Contains("both ' and \"", result.Failed[0].Reason);
        var left = (await sync.ListRemoteAsync(ct)).Where(f => f.RelativePath.StartsWith("Test Artist/")).Select(f => f.RelativePath);
        Assert.Equal(["Test Artist/Test Album (2020)/03 - It's \"both\".mp3"], left); // only the unverified one stays
        Assert.True(File.Exists(Path.Combine(lib.Root, @"Music\Test Artist\Test Album (2020)\02 - A _Quoted_ Song.mp3")));
    }
}

/// <summary>
/// Full sync engine against the real server, into a throwaway "iPod" at %TEMP%\podmanager-livetest\ipod
/// (data folder %TEMP%\podmanager-livetest\data). Converts two songs of PODMANAGER_LIVE_ALBUM (a lossless album on
/// your server, e.g. "Artist/Album (2019)") on demand, copies them, and fails one made-up song.
/// Run: set PODMANAGER_LIVE_ALBUM, then dotnet test --project tests/PodManager.Tests -- --explicit only
/// </summary>
public class LiveEngineTests
{
    [Fact(Explicit = true)]
    public async Task Syncs_real_songs_and_logs_a_failure_with_its_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var album = Environment.GetEnvironmentVariable("PODMANAGER_LIVE_ALBUM");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(album), "Set PODMANAGER_LIVE_ALBUM to a lossless album on your server, e.g. Artist/Album (2019).");
        var settings = SyncSettings.Load();                 // before the data folder moves to the test one
        var baseDir = Path.Combine(Path.GetTempPath(), "podmanager-livetest");
        if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
        var ipod = Path.Combine(baseDir, "ipod");
        Directory.CreateDirectory(Path.Combine(ipod, ".knurl"));
        AppPaths.DataDirectory = Path.Combine(baseDir, "data");

        var server = new ServerSync(settings);
        var listing = await server.ListServerAsync(ct);
        var two = listing.Masters.Where(m => m.RelativePath.StartsWith(album!.TrimEnd('/') + "/", StringComparison.Ordinal))
                                 .OrderBy(m => m.RelativePath, StringComparer.Ordinal).Take(2).ToList();
        Assert.Equal(2, two.Count);
        var fake = new RemoteFile("Nobody/Nothing (2000)/Nobody - Nothing - 01 - Ghost.flac", 1);
        // Only these three: ignore anything else that happens to be waiting in the real outbox.
        var mine = two.Select(m => SyncPlanner.OutboxRelFor(m.RelativePath)).ToHashSet(StringComparer.Ordinal);
        var plan = SyncPlanner.Build(new ServerListing([.. two, fake], listing.Outbox.Where(o => mine.Contains(o.RelativePath)).ToList()),
            ipod, "Music", new HashSet<string>(), new Dictionary<string, string>());
        Assert.Equal(3, plan.Count);
        Assert.All(plan, i => Assert.True(i.NeedsSync));

        var log = new TransferLog();
        var outcome = await new SyncEngine(server, ipod, log).SyncAsync(plan, "Selected", null, ct);

        Assert.Equal(2, outcome.Run.Copied);
        var failure = Assert.Single(outcome.Run.Failures);
        Assert.Contains("Ghost", failure.Song);
        Assert.Contains("not in the library", failure.Reason);
        Assert.True(log.CurrentErrors.ContainsKey(failure.Key));
        foreach (var m in two)
            Assert.True(File.Exists(Path.Combine(ipod, "Music", Sanitizer.LocalRelative(SyncPlanner.OutboxRelFor(m.RelativePath)))));
        Assert.True(File.Exists(Path.Combine(ipod, "Music", Sanitizer.LocalRelative(album.TrimEnd('/') + "/cover.jpg"))));
        Assert.True(File.Exists(Path.Combine(AppPaths.DataDirectory, "transfer-log.json")));

        // Delivered copies are gone from the outbox; the masters are untouched.
        var after = await server.ListServerAsync(ct);
        Assert.DoesNotContain(after.Outbox, f => two.Any(m => SyncPlanner.OutboxRelFor(m.RelativePath) == f.RelativePath));
        Assert.All(two, m => Assert.Contains(after.Masters, x => x == m));

        // Re-planning shows them on the iPod and the failure as Failed.
        var replan = SyncPlanner.Build(new ServerListing([.. two, fake], []), ipod, "Music", new HashSet<string>(), log.ErrorReasons);
        Assert.Equal(2, replan.Count(i => i.Status == SyncStatus.OnIpod));
        Assert.Equal(SyncStatus.Failed, Assert.Single(replan, i => i.Title == "Ghost").Status);
    }
}

public class NamedLockTests
{
    [Fact]
    public void Second_acquire_fails_until_the_first_is_released()
    {
        var name = @"Local\podmanager-test-" + Guid.NewGuid().ToString("N");
        using (var first = NamedLock.TryAcquire(name))
        {
            Assert.NotNull(first);
            Assert.Null(NamedLock.TryAcquire(name));
        }
        using var again = NamedLock.TryAcquire(name);
        Assert.NotNull(again);
    }
}
