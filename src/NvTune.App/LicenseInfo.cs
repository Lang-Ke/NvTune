namespace SimpleNvidiaUndervolt;

/// <summary>The license travels inside the executable, including when distributed on its own.</summary>
internal static class LicenseInfo
{
    internal static string Text { get; } = ReadText();

    private static string ReadText()
    {
        using var stream = typeof(LicenseInfo).Assembly.GetManifestResourceStream("NvTune.LICENSE")
            ?? throw new InvalidOperationException("The embedded NvTune license is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
