using System.Runtime.ExceptionServices;

namespace NvTune;

/// <summary>Coordinates portable copies across directories and Windows sessions. A dedicated
/// thread owns each mutex so callers may release the lease after an asynchronous continuation.</summary>
internal static class GlobalControlLease
{
    public const string GuiName = @"Global\NvTuneGuiInstance";
    public const string HardwareName = @"Global\NvTuneHardwareControl";
    public const string DaemonName = @"Global\NvTuneDaemonInstance";

    /// <summary>Acquires the global owner and, when an old data directory exists, its compatible
    /// file lock. Missing legacy directories are never created. Contention returns null; failures
    /// such as denied permissions are reported rather than allowing a second hardware owner.</summary>
    public static IDisposable? TryAcquire(string name, string? legacyPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var lease = new Lease(name, legacyPath is null ? null : Path.GetFullPath(legacyPath));
        lease.WaitUntilReady();
        if (lease.Acquired) return lease;
        lease.Dispose();
        return null;
    }

    /// <summary>A snapshot of whether another thread owns an existing named mutex. This probe
    /// never creates one and does not replace acquiring a lease before touching hardware.</summary>
    public static bool IsHeld(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
        using (mutex)
        {
            if (!TryEnter(mutex)) return true;
            mutex.ReleaseMutex();
            return false;
        }
    }

    private static bool TryEnter(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    private static bool LegacyParentExists(string path)
    {
        string parent = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The legacy lock must name a file.", nameof(path));
        try { return (File.GetAttributes(parent) & FileAttributes.Directory) != 0; }
        catch (DirectoryNotFoundException) { return false; }
        catch (FileNotFoundException) { return false; }
        // Unlike Directory.Exists, this must not hide a permission error and skip the old lock.
    }

    private sealed class Lease : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private readonly Thread _owner;
        private ExceptionDispatchInfo? _failure;
        private int _disposed;

        internal bool Acquired { get; private set; }

        internal Lease(string name, string? legacyPath)
        {
            _owner = new Thread(() => Own(name, legacyPath))
            {
                IsBackground = true,
                Name = "NvTune control lease",
            };
            try { _owner.Start(); }
            catch
            {
                _ready.Dispose();
                _release.Dispose();
                throw;
            }
        }

        internal void WaitUntilReady()
        {
            _ready.Wait();
            if (_failure is not null) Dispose();
        }

        private void Own(string name, string? legacyPath)
        {
            try
            {
                using var mutex = new Mutex(false, name);
                if (!TryEnter(mutex)) return;
                try
                {
                    FileStream? legacy = null;
                    try
                    {
                        if (legacyPath is not null && LegacyParentExists(legacyPath))
                        {
                            try
                            {
                                legacy = new FileStream(legacyPath, FileMode.OpenOrCreate,
                                    FileAccess.ReadWrite, FileShare.None);
                            }
                            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
                            {
                                return;
                            }
                        }

                        Acquired = true;
                        _ready.Set();
                        _release.Wait();
                    }
                    finally { legacy?.Dispose(); }
                }
                finally { mutex.ReleaseMutex(); }
            }
            catch (Exception ex) { _failure = ExceptionDispatchInfo.Capture(ex); }
            finally { _ready.Set(); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _release.Set();
            _owner.Join();
            _ready.Dispose();
            _release.Dispose();
            _failure?.Throw();
        }
    }
}
