using NvTune;

if (args is ["--version"])
{
    Console.WriteLine($"NvTune {Product.Version} ({Persistence.Edition})");
    return 0;
}

if (args is ["--verify-package"])
{
    // Redirected output must preserve Unicode executable/data paths on every Windows locale.
    Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    // Exercise the deployed WinForms assembly without starting GPU control, installing
    // files, or registering a logon task. Also works with no NVIDIA driver installed.
    ApplicationConfiguration.Initialize();
    using var preview = new MainForm(Array.Empty<string>(), preview: true);
    using var bitmap = new System.Drawing.Bitmap(preview.Width, preview.Height);
    preview.DrawToBitmap(bitmap, preview.ClientRectangle);
    Console.WriteLine($"NvTune {Product.Version}");
    Console.WriteLine($"Deployment: {(Persistence.IsSingleFile ? "single-file" : "multi-file")}");
    Console.WriteLine($"Edition: {Persistence.Edition}");
    Console.WriteLine($"Data directory: {Persistence.ActiveDataDir}");
    Console.WriteLine("Package check passed. No GPU APIs called.");
    return 0;
}

if (args is ["--stop-background"])
{
    try
    {
        using var windowLease = GlobalControlLease.TryAcquire(GlobalControlLease.GuiName,
            Persistence.LegacyLockPath("gui-instance.lock"))
            ?? throw new CliError("请先关闭 NvTune 窗口，再停止后台。");
        DaemonControl.Stop();
        Console.WriteLine("NvTune background stopped. Saved settings and logon task are unchanged.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        MessageBox.Show(ex.Message, "停止 NvTune 后台失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return 1;
    }
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
