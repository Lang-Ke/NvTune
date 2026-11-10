using System.Diagnostics;
using System.Text;

namespace SimpleNvidiaUndervolt;

/// <summary>Tiny rotating log beside the app so a background failure can be diagnosed after the
/// fact. The daemon and the logon task have no console, so this is their only voice.</summary>
internal static class Diag
{
    private const long MaxBytes = 256 * 1024;

    public static void Log(string message)
    {
        try
        {
            string path = Path.Combine(Persistence.DataDir(), "nvtune.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                File.Delete(path);
            }

            File.AppendAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // logging must never break the caller
        }
    }
}