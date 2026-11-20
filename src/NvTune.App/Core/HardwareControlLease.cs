namespace NvTune;

/// <summary>Legacy file-lock compatibility checks. Production control also holds the global
/// mutex through <see cref="GlobalControlLease"/> so editions and portable folders share one owner.</summary>
internal static class HardwareControlLease
{
    public static FileStream? TryAcquire(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return null; }
    }
}
