using System.Reflection;
using System.Diagnostics;
using SimpleNvidiaUndervolt;

internal static class Program
{
    private static readonly FanState.FanSettings Curve = new(2, null, new[] { new[] { 20, 0 }, new[] { 100, 100 } });
    private static readonly FanState.FanSettings Fixed = new(1, 30, null);
    private static int _passed, _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--render-ui", var renderDir]) return UiChecks.Render(renderDir);
        if (args is ["--fan-diagnostics"])
        {
            // Read-only driver queries, explicitly separate from simulated regression checks.
            FanApi.Initialize();
            try
            {
                for (int i = 0; i < FanApi.GetDeviceCount(); i++)
                {
                    var device = FanApi.GetDevice((uint)i);
                    Console.WriteLine($"GPU {i}: {FanApi.GetDeviceName(device)}; allowed={FanApi.GetFanRange(device)}");
                    for (uint fan = 0; fan < FanApi.GetFanCount(device); fan++)
                        Console.WriteLine($"Fan {fan}: reported={FanApi.GetFanPercent(device, fan)}%; target={FanApi.GetTargetFanPercent(device, fan)}%; RPM={FanApi.GetFanRpm(device, fan)}; policy={FanApi.TryGetFanPolicy(device, fan)}");
                }
            }
            finally { FanApi.Shutdown(); }
            return 0;
        }
        if (args is ["--hold-lease", var leasePath])
        {
            using var lease = HardwareControlLease.TryAcquire(leasePath);
            Console.WriteLine(lease is null ? "failed" : "ready");
            Console.Out.Flush();
            Console.ReadLine();
            return lease is null ? 1 : 0;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        UiChecks.Register(Run, Check);
        Run("Fan readout preserves a target below the reported percentage", () =>
        {
            var sample = new FanTelemetry(0, 45, 22, 2151, FanApi.PolicyAuto);
            Check(sample.Value == "2151" && sample.Unit == "RPM");
            Check(sample.Summary.Contains("读数 45%") && sample.Summary.Contains("目标 22%") && sample.Summary.Contains("自动"));
        });
        Run("Unavailable fan telemetry is not fabricated as zero", () =>
        {
            var missing = new FanTelemetry(0, null, null, null, null);
            Check(missing.Value == "—" && missing.Summary.Contains("RPM 不可用"));
            Check(new FanTelemetry(0, 0, 0, 0, FanApi.PolicyManual).Value == "0");
            Check(new FanTelemetry(0, 45, 22, null, null).Value == "45");
        });
        Run("Fan duty uses the documented range instead of guessing a 45 percent floor", () =>
        {
            Check(FanTelemetry.EffectivePercent(30, (22, 100)) == 30);
            Check(FanTelemetry.EffectivePercent(10, (22, 100)) == 22);
            Check(FanTelemetry.EffectivePercent(0, (22, 100)) == 22);
            Check(FanTelemetry.EffectivePercent(0, (0, 100)) == 0);
            Check(FanTelemetry.EffectivePercent(150, null) == 100);
        });
        Run("Fan writes compare accepted target rather than lagging readout", () =>
        {
            FanTelemetry.RequireAccepted(22, 22);
            FanTelemetry.RequireAccepted(22, null);
            FanTelemetry.RequireAccepted(30, 31);
            Throws(() => FanTelemetry.RequireAccepted(30, 45));
        });
        Run("Fan request tracking clears after handback or write failure", () =>
        {
            var fake = new FakeFan();
            var controller = Controller(fake);
            controller.Apply(0, Fixed);
            Check(controller.RequestedPercent(0) == 30);
            controller.Apply(0, null);
            Check(controller.RequestedPercent(0) is null);
            fake.BadWrite = 0;
            Throws(() => controller.Apply(0, Fixed));
            Check(controller.RequestedPercent(0) is null);
        });
        Run("Stale daemon PID is removed without blocking startup", () =>
        {
            WithTemp(dir =>
            {
                string path = Path.Combine(dir, "daemon.pid");
                File.WriteAllText(path, "123");
                DaemonStop.Stop(path, _ => null, _ => { });
                Check(!File.Exists(path));
            });
        });
        Run("Reused PID belonging to another app is never stopped", () =>
        {
            WithTemp(dir =>
            {
                string path = Path.Combine(dir, "daemon.pid");
                File.WriteAllText(path, "123");
                var process = new FakeDaemon { IsOurs = false };
                DaemonStop.Stop(path, _ => process, _ => { });
                Check(!process.Killed && !process.StopRequested && !File.Exists(path));
            });
        });
        Run("Verified legacy daemon is migrated without waiting for unsupported IPC", () =>
        {
            WithTemp(dir =>
            {
                string path = Path.Combine(dir, "daemon.pid");
                File.WriteAllText(path, "123");
                var process = new FakeDaemon { IsVerifiedLegacyDaemon = true, SupportsStop = false };
                DaemonStop.Stop(path, _ => process, _ => { });
                Check(process.Killed && !process.StopRequested && !File.Exists(path));
            });
        });
        Run("Modern daemon uses cooperative stop", () =>
        {
            WithTemp(dir =>
            {
                string path = Path.Combine(dir, "daemon.pid");
                File.WriteAllText(path, "123");
                var process = new FakeDaemon();
                DaemonStop.Stop(path, _ => process, _ => { });
                Check(process.StopRequested && !process.Killed && !File.Exists(path));
            });
        });
        Run("Modern stop timeout never becomes a forced termination", () =>
        {
            WithTemp(dir =>
            {
                string path = Path.Combine(dir, "daemon.pid");
                File.WriteAllText(path, "123");
                var process = new FakeDaemon { ExitsOnWait = false };
                Throws(() => DaemonStop.Stop(path, _ => process, _ => { }));
                Check(process.StopRequested && !process.Killed && File.ReadAllText(path) == "123");
            });
        });
        Run("Unverified legacy process is not terminated", () =>
        {
            WithTemp(dir =>
            {
                string path = Path.Combine(dir, "daemon.pid");
                File.WriteAllText(path, "123");
                var process = new FakeDaemon { SupportsStop = false };
                Throws(() => DaemonStop.Stop(path, _ => process, _ => { }));
                Check(!process.Killed && File.Exists(path));
            });
        });
        Run("Stopping an old daemon preserves a replacement PID record", () =>
        {
            WithTemp(dir =>
            {
                string path = Path.Combine(dir, "daemon.pid");
                File.WriteAllText(path, "123");
                var process = new FakeDaemon { OnWait = () => File.WriteAllText(path, "456") };
                DaemonStop.Stop(path, _ => process, _ => { });
                Check(File.ReadAllText(path) == "456");
            });
        });
        Run("Legacy detection checks version, window and exact daemon argument", () =>
        {
            Check(DaemonStop.IsLegacy(new Version(1, 0, 1, 0), false, "\"C:\\Program Files\\NvTune\\NvTuneApp.exe\" --daemon --silent"));
            Check(!DaemonStop.IsLegacy(new Version(1, 0, 2, 0), false, "app.exe --daemon"));
            Check(!DaemonStop.IsLegacy(new Version(1, 0, 1, 0), true, "app.exe --daemon"));
            Check(!DaemonStop.IsLegacy(new Version(1, 0, 1, 0), false, "app.exe --daemon-other"));
            Check(!DaemonStop.IsLegacy(new Version(1, 0, 1, 0), false, "app.exe"));
        });
        Run("Temporary timer has a Tick handler", () =>
        {
            // No Show/Load: this does not initialize GPU interfaces or start the daemon.
            using var form = new MainForm(Array.Empty<string>());
            var timer = (System.Windows.Forms.Timer)typeof(MainForm).GetField("_fanTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            var handler = (Delegate?)typeof(System.Windows.Forms.Timer).GetField("_onTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(timer);
            Check(handler?.GetInvocationList().Length > 0);
        });
        Run("Unequal-spacing curve stays monotonic and within endpoints", () =>
        {
            var curve = new FanCurve(new[] { (20, 0), (21, 90), (100, 100) });
            int previous = -1;
            foreach (int t in Enumerable.Range(20, 81))
            {
                int value = curve.FanPercentAt(t);
                Check(value >= previous && value <= 100);
                previous = value;
            }
            Check(curve.FanPercentAt(50) < 100);
        });
        Run("Generated monotonic curves never reverse or overshoot", () =>
        {
            var random = new Random(4381);
            for (int sample = 0; sample < 1000; sample++)
            {
                int[] temps = Enumerable.Range(20, 81).OrderBy(_ => random.Next()).Take(8).Order().ToArray();
                int[] speeds = Enumerable.Range(0, 8).Select(_ => random.Next(101)).Order().ToArray();
                var curve = new FanCurve(temps.Zip(speeds).Select(p => (p.First, p.Second)).ToArray());
                int previous = -1;
                foreach (int t in Enumerable.Range(0, 151))
                {
                    int value = curve.FanPercentAt(t);
                    Check(value >= previous && value >= speeds[0] && value <= speeds[^1]);
                    previous = value;
                }
                for (int i = 0; i < temps.Length; i++) Check(curve.FanPercentAt(temps[i]) == speeds[i]);
            }
        });
        Run("Curve handles plateaus, duplicate temperatures and empty input", () =>
        {
            Check(new FanCurve(Array.Empty<(int, int)>()).FanPercentAt(50) == 100);
            var curve = new FanCurve(new[] { (30, 20), (30, 50), (80, 50) });
            foreach (int t in Enumerable.Range(0, 151)) Check(curve.FanPercentAt(t) == 50);
        });
        Run("Sensor failure restores auto without writing the cold endpoint", () =>
        {
            var fake = new FakeFan { BadTemperature = 0 };
            var loop = Controller(fake);
            Check(!loop.Tick(new[] { 0 }, _ => Curve));
            Check(fake.Calls.SequenceEqual(new[] { "temp:0", "auto:0" }));
        });
        Run("Failed auto recovery requests emergency maximum", () =>
        {
            var fake = new FakeFan { BadTemperature = 0, BadAuto = 0 };
            Check(!Controller(fake).Tick(new[] { 0 }, _ => Curve));
            Check(fake.Calls.SequenceEqual(new[] { "temp:0", "auto:0", "set:0:100" }));
        });
        Run("One failing GPU does not starve another GPU", () =>
        {
            var fake = new FakeFan { BadTemperature = 0 };
            Check(!Controller(fake).Tick(new[] { 0, 1 }, _ => Curve));
            Check(fake.Calls.Contains("set:1:50"));
        });
        Run("Failed curves resume on the next successful poll", () =>
        {
            var fake = new FakeFan { BadTemperature = 0 };
            var loop = Controller(fake);
            Check(!loop.Tick(new[] { 0 }, _ => Curve));
            fake.BadTemperature = -1;
            Check(loop.Tick(new[] { 0 }, _ => Curve));
            Check(fake.Calls.Last() == "set:0:50");
        });
        Run("Every curve is updated regardless of selected UI GPU", () =>
        {
            var fake = new FakeFan();
            Check(Controller(fake).Tick(new[] { 0, 1, 2 }, _ => Curve));
            Check(fake.Calls.Count(c => c.StartsWith("set:")) == 3);
        });
        Run("Restoring absent saved state never replays a temporary speed", () =>
        {
            var fake = new FakeFan();
            var loop = Controller(fake);
            loop.Apply(0, Fixed);
            fake.Calls.Clear();
            Check(loop.RestoreSaved(new[] { 0 }, _ => null));
            Check(fake.Calls.SequenceEqual(new[] { "auto:0" }));
        });
        Run("Invalid saved data hands back control and isolates the failure", () =>
        {
            var fake = new FakeFan();
            Check(!Controller(fake).Tick(new[] { 0, 1 }, i => i == 0 ? throw new InvalidDataException("broken JSON") : Fixed));
            Check(fake.Calls.SequenceEqual(new[] { "auto:0", "set:1:30" }));
        });
        Run("Partial manual write failure restores auto", () =>
        {
            var fake = new FakeFan { BadWrite = 0 };
            Check(!Controller(fake).Tick(new[] { 0 }, _ => Fixed));
            Check(fake.Calls.SequenceEqual(new[] { "set:0:30", "auto:0" }));
        });
        Run("All recovery failures remain failures and are reported", () =>
        {
            var fake = new FakeFan { BadTemperature = 0, BadAuto = 0, BadWrite = 0 };
            var logs = new List<string>();
            Check(!new FanController(fake, logs.Add).Tick(new[] { 0 }, _ => Curve));
            Check(logs.Single().Contains("均失败"));
        });
        Run("Physical mapping survives reversed API enumeration", () =>
        {
            var map = GpuBusMapping.Match(new[] { (0, 1u), (1, 5u) }, new[] { (0, 5u), (1, 1u) });
            Check(map[0] == 1 && map[1] == 0);
        });
        Run("Ambiguous and missing buses are never guessed", () =>
        {
            Check(GpuBusMapping.Match(new[] { (0, 1u) }, new[] { (0, 1u), (1, 1u) }).Count == 0);
            Check(GpuBusMapping.Match(new[] { (0, 1u), (1, 1u) }, new[] { (0, 1u) }).Count == 0);
            Check(GpuBusMapping.Match(new[] { (0, 2u) }, new[] { (0, 1u) }).Count == 0);
        });
        Run("Saved identity rejects replacement cards and legacy guesses", () =>
        {
            GpuBinding.Require("GPU-A", "gpu-a");
            Throws(() => GpuBinding.Require("GPU-A", "GPU-B"));
            Throws(() => GpuBinding.Require(null, "GPU-B"));
        });
        Run("Malformed fan files are rejected before use", () =>
        {
            foreach (string json in new[] { "{", "{\"mode\":2,\"curve\":[[30],[80,90]]}", "{\"mode\":2,\"curve\":[[30,20],[30,90]]}", "{\"mode\":1,\"fixedPercent\":101}", "{\"mode\":9}" })
                Throws(() => FanState.Parse(json));
            Check(FanState.Parse("{\"mode\":1,\"fixedPercent\":30,\"gpuUuid\":\"GPU-A\"}").GpuUuid == "GPU-A");
        });
        Run("Control lease excludes a second owner and releases on disposal", () =>
        {
            WithTemp(dir =>
            {
                string path = Path.Combine(dir, "owner.lock");
                using (var first = HardwareControlLease.TryAcquire(path))
                {
                    Check(first is not null);
                    using var second = HardwareControlLease.TryAcquire(path);
                    Check(second is null);
                }
                using var next = HardwareControlLease.TryAcquire(path);
                Check(next is not null);
            });
        });
        Run("Concurrent saves use independent temporary files", () =>
        {
            WithTemp(dir =>
            {
                string path = Path.Combine(dir, "config.json");
                Parallel.For(0, 25, i => TuningDocuments.WriteFile(path, $"{{\"mode\":1,\"fixedPercent\":{i}}}", "test"));
                FanState.Parse(File.ReadAllText(path));
                Check(Directory.GetFiles(dir, "*.tmp").Length == 0);
            });
        });
        Run("Single-file installation removes only obsolete program companions", () =>
        {
            WithTemp(dir =>
            {
                string source = Path.Combine(dir, "source"), target = Path.Combine(dir, "installed");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(Path.Combine(source, "data"));
                Directory.CreateDirectory(Path.Combine(target, "data"));
                string[] companions = { "NvTuneApp.dll", "NvTuneApp.deps.json", "NvTuneApp.runtimeconfig.json", "NvTuneApp.pdb" };
                File.WriteAllText(Path.Combine(source, "NvTuneApp.exe"), "new bundled program");
                File.WriteAllText(Path.Combine(target, "NvTuneApp.exe"), "old program");
                foreach (string name in companions)
                {
                    // Simulate extracting the bundle into an older portable program folder.
                    File.WriteAllText(Path.Combine(source, name), "stray portable companion");
                    File.WriteAllText(Path.Combine(target, name), "old installed companion");
                }
                File.WriteAllText(Path.Combine(source, "NvTuneApp.extra.json"), "not an installation file");
                File.WriteAllText(Path.Combine(target, "NvTuneApp.custom.json"), "preserve unknown file");
                File.WriteAllText(Path.Combine(target, "LICENSE"), "preserve license");
                File.WriteAllText(Path.Combine(source, "data", "fan.json"), "portable old settings");
                File.WriteAllText(Path.Combine(source, "data", "fan.gpu1.json"), "portable new settings");
                File.WriteAllText(Path.Combine(target, "data", "fan.json"), "installed settings");
                File.WriteAllText(Path.Combine(target, "data", "daemon.pid"), "123");

                ProgramInstallation.Install(source, target, singleFile: true);

                Check(File.ReadAllText(Path.Combine(target, "NvTuneApp.exe")) == "new bundled program");
                Check(companions.All(name => !File.Exists(Path.Combine(target, name))));
                Check(companions.All(name => File.Exists(Path.Combine(source, name))));
                Check(!File.Exists(Path.Combine(target, "NvTuneApp.extra.json")));
                Check(File.ReadAllText(Path.Combine(target, "NvTuneApp.custom.json")) == "preserve unknown file");
                Check(File.ReadAllText(Path.Combine(target, "LICENSE")) == "preserve license");
                Check(File.ReadAllText(Path.Combine(target, "data", "fan.json")) == "installed settings");
                Check(File.ReadAllText(Path.Combine(target, "data", "fan.gpu1.json")) == "portable new settings");
                Check(File.ReadAllText(Path.Combine(target, "data", "daemon.pid")) == "123");
            });
        });
        Run("Multi-file installation copies every required runtime file", () =>
        {
            WithTemp(dir =>
            {
                string source = Path.Combine(dir, "source"), target = Path.Combine(dir, "installed");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(target);
                string[] required = { "NvTuneApp.exe", "NvTuneApp.dll", "NvTuneApp.deps.json", "NvTuneApp.runtimeconfig.json" };
                foreach (string name in required) File.WriteAllText(Path.Combine(source, name), "new " + name);
                File.WriteAllText(Path.Combine(target, "NvTuneApp.pdb"), "old debug symbols");
                File.WriteAllText(Path.Combine(source, "NvTuneApp.extra.dll"), "unrelated file");
                var copied = new List<string>();

                ProgramInstallation.Install(source, target, singleFile: false, copied.Add);

                Check(required.All(name => File.ReadAllText(Path.Combine(target, name)) == "new " + name));
                Check(copied.Select(Path.GetFileName).SequenceEqual(required));
                Check(!File.Exists(Path.Combine(target, "NvTuneApp.pdb")));
                Check(!File.Exists(Path.Combine(target, "NvTuneApp.extra.dll")));
            });
        });
        Run("Incomplete installation sources leave existing programs and data intact", () =>
        {
            WithTemp(dir =>
            {
                string[] required = { "NvTuneApp.exe", "NvTuneApp.dll", "NvTuneApp.deps.json", "NvTuneApp.runtimeconfig.json" };
                foreach (string missing in required)
                {
                    string caseDir = Path.Combine(dir, missing);
                    string source = Path.Combine(caseDir, "source"), target = Path.Combine(caseDir, "installed");
                    Directory.CreateDirectory(source);
                    Directory.CreateDirectory(Path.Combine(target, "data"));
                    foreach (string name in required)
                    {
                        if (name != missing) File.WriteAllText(Path.Combine(source, name), "new " + name);
                        File.WriteAllText(Path.Combine(target, name), "installed " + name);
                    }
                    File.WriteAllText(Path.Combine(target, "NvTuneApp.pdb"), "keep debug symbols on failure");
                    File.WriteAllText(Path.Combine(target, "data", "fan.json"), "keep configuration");

                    Throws(() => ProgramInstallation.Install(source, target, singleFile: false));
                    Check(required.All(name => File.ReadAllText(Path.Combine(target, name)) == "installed " + name));
                    Check(File.ReadAllText(Path.Combine(target, "NvTuneApp.pdb")) == "keep debug symbols on failure");
                    Check(File.ReadAllText(Path.Combine(target, "data", "fan.json")) == "keep configuration");
                    if (missing == "NvTuneApp.exe")
                    {
                        Throws(() => ProgramInstallation.Install(source, target, singleFile: true));
                        Check(required.All(name => File.ReadAllText(Path.Combine(target, name)) == "installed " + name));
                        string absentTarget = Path.Combine(caseDir, "absent");
                        Throws(() => ProgramInstallation.Install(source, absentTarget, singleFile: true));
                        Check(!Directory.Exists(absentTarget));
                    }
                }
            });
        });
        Run("A failed program copy never triggers obsolete-file cleanup", () =>
        {
            WithTemp(dir =>
            {
                string source = Path.Combine(dir, "source"), target = Path.Combine(dir, "installed");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(source, "NvTuneApp.exe"), "new bundled program");
                File.WriteAllText(Path.Combine(target, "NvTuneApp.exe"), "old program");
                File.WriteAllText(Path.Combine(target, "NvTuneApp.dll"), "old runtime file");
                using var locked = new FileStream(Path.Combine(target, "NvTuneApp.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);

                Throws(() => ProgramInstallation.Install(source, target, singleFile: true));
                Check(File.ReadAllText(Path.Combine(target, "NvTuneApp.dll")) == "old runtime file");
            });
        });
        Run("A crashed owner releases control to the next process", () =>
        {
            WithTemp(dir =>
            {
                string path = Path.Combine(dir, "crash-owner.lock");
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                };
                start.ArgumentList.Add("--hold-lease");
                start.ArgumentList.Add(path);
                using var child = Process.Start(start)!;
                try
                {
                    var ready = child.StandardOutput.ReadLineAsync();
                    Check(ready.Wait(TimeSpan.FromSeconds(10)) && ready.Result == "ready");
                    using (var blocked = HardwareControlLease.TryAcquire(path)) Check(blocked is null);
                    child.Kill(); // Kill only this test child to simulate unexpected termination.
                    Check(child.WaitForExit(10000));
                    using var recovered = HardwareControlLease.TryAcquire(path);
                    Check(recovered is not null);
                }
                finally
                {
                    if (!child.HasExited) { child.Kill(); child.WaitForExit(10000); }
                }
            });
        });
        Run("Tuning documents preserve physical UUID on roundtrip", () =>
        {
            var doc = new TuningDoc { GpuUuid = "GPU-A", MemoryOffset = 100, Curve = Array.Empty<CurveEntryDoc>() };
            Check(TuningDocuments.ParseTuningDoc(TuningDocuments.Render(doc), "test").GpuUuid == "GPU-A");
        });
        Console.WriteLine($"{_passed} passed, {_failed} failed. No GPU APIs called.");
        return _failed == 0 ? 0 : 1;
    }

    private static FanController Controller(FakeFan fake) => new(fake, _ => { });
    private static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Throws(Action action)
    {
        try { action(); } catch { return; }
        throw new Exception("Expected failure");
    }
    private static void Run(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
    }
    private static void WithTemp(Action<string> test)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nvtune-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try { test(dir); }
        finally
        {
            string target = Path.GetFullPath(dir);
            string prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "nvtune-tests-");
            if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid cleanup target");
            Directory.Delete(target, true);
        }
    }

    private sealed class FakeDaemon : IRecordedDaemon
    {
        public bool IsOurs { get; set; } = true;
        public bool HasExited { get; set; }
        public bool IsVerifiedLegacyDaemon { get; set; }
        public bool SupportsStop = true, ExitsOnWait = true, Killed, StopRequested;
        public Action? OnWait;
        public bool TryRequestStop() { StopRequested = true; return SupportsStop; }
        public bool WaitForExit(int timeoutMs) { OnWait?.Invoke(); HasExited = ExitsOnWait; return HasExited; }
        public void TerminateLegacy() { Killed = true; }
        public void Dispose() { }
    }

    private sealed class FakeFan : IFanBackend
    {
        public int BadTemperature = -1, BadAuto = -1, BadWrite = -1;
        public readonly List<string> Calls = new();
        public int TemperatureC(int index)
        {
            Calls.Add($"temp:{index}");
            if (index == BadTemperature) throw new IOException("sensor unavailable");
            return 60;
        }
        public void SetPercent(int index, int percent)
        {
            Calls.Add($"set:{index}:{percent}");
            if (index == BadWrite) throw new IOException("fan write refused");
        }
        public void RestoreAuto(int index)
        {
            Calls.Add($"auto:{index}");
            if (index == BadAuto) throw new IOException("auto refused");
        }
    }
}
