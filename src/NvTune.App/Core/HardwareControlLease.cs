namespace SimpleNvidiaUndervolt;

/// <summary>A process-wide hardware owner, automatically released by Windows on a crash.
/// File sharing locks also work across Windows sessions and do not depend on async thread affinity.</summary>
internal static class HardwareControlLease
{
    public static FileStream? TryAcquire(string? path = null)
    {
        path ??= Path.Combine(Persistence.DataDir(), "hardware-control.lock");
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return null; }
    }
}
