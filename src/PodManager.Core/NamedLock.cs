namespace PodManager.Core;

/// <summary>
/// Holds a named system mutex on a dedicated thread (mutexes are thread-affine, async code isn't).
/// The automatic sync script takes the same mutex, so the app and the script never copy at once.
/// </summary>
public sealed class NamedLock : IDisposable
{
    public const string IpodSyncName = @"Global\ipod-music-sync";

    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _thread;

    private NamedLock(Thread thread) => _thread = thread;

    public static NamedLock? TryAcquire(string name)
    {
        var ready = new ManualResetEventSlim();
        var acquired = false;
        NamedLock? holder = null;
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, name);
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; } // previous holder died; it's ours now
            ready.Set();
            if (!acquired) return;
            holder!._release.Wait();
            mutex.ReleaseMutex();
        }) { IsBackground = true, Name = "NamedLock " + name };

        holder = new NamedLock(thread);
        thread.Start();
        ready.Wait();
        if (acquired) return holder;
        thread.Join();
        return null;
    }

    public void Dispose()
    {
        _release.Set();
        _thread.Join();
    }
}
