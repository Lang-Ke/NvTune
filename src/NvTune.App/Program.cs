using SimpleNvidiaUndervolt;

if (args.Contains("--daemon"))
{
    using var cts = new CancellationTokenSource();
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
    return await Daemon.Run(cts.Token);
}

ApplicationConfiguration.Initialize();
Application.Run(new MainForm(args));
return 0;
