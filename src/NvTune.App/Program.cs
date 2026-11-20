using SimpleNvidiaUndervolt;

if (args is ["--license"])
{
    Console.Write(LicenseInfo.Text);
    return 0;
}

if (args is ["--version"])
{
    Console.WriteLine($"NvTune {Product.Version}");
    return 0;
}

if (args is ["--verify-package"])
{
    // Exercise the deployed WinForms assembly without starting GPU control, installing
    // files, or registering a logon task. Also works with no NVIDIA driver installed.
    ApplicationConfiguration.Initialize();
    using var preview = new MainForm(Array.Empty<string>(), preview: true);
    using var bitmap = new System.Drawing.Bitmap(preview.Width, preview.Height);
    preview.DrawToBitmap(bitmap, preview.ClientRectangle);
    Console.WriteLine($"NvTune {Product.Version}");
    Console.WriteLine($"Deployment: {(Persistence.IsSingleFile ? "single-file" : "multi-file")}");
    Console.WriteLine("Package check passed. No GPU APIs called.");
    return 0;
}

if (args.Contains("--daemon"))
{
    using var cts = new CancellationTokenSource();
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
    return await Daemon.Run(cts.Token, args.Contains("--recover-fans"));
}

ApplicationConfiguration.Initialize();
Application.Run(new MainForm(args));
return 0;
