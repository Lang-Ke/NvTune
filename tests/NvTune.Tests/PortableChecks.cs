using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using NvTune;

internal static class PortableChecks
{
    public static void Register(Action<string, Action> run, Action<bool> check, Action<Action<string>> withTemp)
    {
        run("Portable data follows the executable instead of the working directory", () =>
        {
            withTemp(dir =>
            {
                string exe = Path.Combine(dir, "folder with spaces & Unicode 中文", "NvTuneApp.exe");
                string expected = Path.Combine(Path.GetDirectoryName(exe)!, "data");
                check(PortableData.DataDirectory(exe) == expected);
                check(!Directory.Exists(expected));
            });
        });
        run("First portable start imports only recognized settings and keeps source files", () =>
        {
            withTemp(dir =>
            {
                string source = Path.Combine(dir, "old", "data"), target = Path.Combine(dir, "portable", "data");
                Directory.CreateDirectory(source);
                string[] names = { "persisted-tuning.json", "undervolt-ui.json", "fan-control.gpu2.json", "reference-curve.json" };
                foreach (string name in names) File.WriteAllText(Path.Combine(source, name), "{\"test\":\"" + name + "\"}");
                foreach (string name in new[] { "unknown.json", "fan-control.gpuX.json", "daemon.pid", "hardware-control.lock", "nvtune.log", "portable-state.json" })
                    File.WriteAllText(Path.Combine(source, name), "not portable settings");
                check(PortableData.Initialize(target, source) == names.Length);
                check(names.All(name => File.ReadAllText(Path.Combine(target, name)) == File.ReadAllText(Path.Combine(source, name))));
                check(!File.Exists(Path.Combine(target, "unknown.json")) && !File.Exists(Path.Combine(target, "fan-control.gpuX.json")));
                check(!File.Exists(Path.Combine(target, "daemon.pid")) && !File.Exists(Path.Combine(target, "hardware-control.lock")));
                check(!File.Exists(Path.Combine(target, "nvtune.log")));
                check(File.Exists(Path.Combine(target, "portable-state.json")));
                check(Directory.GetFiles(source).Length == names.Length + 6);
                check(Directory.GetFiles(target, "*.tmp").Length == 0);
            });
        });
        run("Existing portable settings are never mixed with installed settings", () =>
        {
            withTemp(dir =>
            {
                string source = Path.Combine(dir, "old"), target = Path.Combine(dir, "portable");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(source, "persisted-tuning.json"), "old tuning");
                File.WriteAllText(Path.Combine(source, "fan-control.json"), "old fans");
                File.WriteAllText(Path.Combine(target, "fan-control.json"), "portable fans");
                check(PortableData.Initialize(target, source) == 0);
                check(File.ReadAllText(Path.Combine(target, "fan-control.json")) == "portable fans");
                check(!File.Exists(Path.Combine(target, "persisted-tuning.json")));
                check(File.Exists(Path.Combine(target, "portable-state.json")));
            });
        });
        run("A reset portable profile never imports deleted settings again", () =>
        {
            withTemp(dir =>
            {
                string source = Path.Combine(dir, "old"), target = Path.Combine(dir, "portable");
                Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, "fan-control.json"), "saved fans");
                check(PortableData.Initialize(target, source) == 1);
                File.Delete(Path.Combine(target, "fan-control.json"));
                check(PortableData.Initialize(target, source) == 0);
                check(!File.Exists(Path.Combine(target, "fan-control.json")));
            });
        });
        run("Portable initialization needs no installed folder and accepts its own data folder", () =>
        {
            withTemp(dir =>
            {
                string source = Path.Combine(dir, "absent"), target = Path.Combine(dir, "portable");
                check(PortableData.Initialize(target, source) == 0);
                check(!Directory.Exists(source) && File.Exists(Path.Combine(target, "portable-state.json")));
                File.WriteAllText(Path.Combine(target, "fan-control.json"), "own fans");
                check(PortableData.Initialize(target, target) == 0);
                check(File.ReadAllText(Path.Combine(target, "fan-control.json")) == "own fans");
            });
        });
        run("Failed portable import leaves no partial profile and can be retried", () =>
        {
            withTemp(dir =>
            {
                string source = Path.Combine(dir, "old"), target = Path.Combine(dir, "portable");
                Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(source, "fan-control.json"), "fans");
                string blocked = Path.Combine(source, "persisted-tuning.json");
                File.WriteAllText(blocked, "tuning");
                using (var exclusive = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    ExpectFailure(() => PortableData.Initialize(target, source));
                    check(!File.Exists(Path.Combine(target, "portable-state.json")));
                    check(!File.Exists(Path.Combine(target, "fan-control.json")));
                    check(!File.Exists(Path.Combine(target, "persisted-tuning.json")));
                }
                check(PortableData.Initialize(target, source) == 2);
                check(File.ReadAllText(Path.Combine(target, "persisted-tuning.json")) == "tuning");
            });
        });
        run("Interrupted portable commits finish the staged snapshot without overwriting existing settings", () =>
        {
            withTemp(dir =>
            {
                string source = Path.Combine(dir, "old"), target = Path.Combine(dir, "portable");
                Directory.CreateDirectory(source);
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(source, "persisted-tuning.json"), "changed after import started");
                File.WriteAllText(Path.Combine(source, "undervolt-ui.json"), "must not mix a new source file");
                File.WriteAllText(Path.Combine(target, "fan-control.json"), "already committed and edited fans");
                string staging = PrepareImport(target, new[] { "fan-control.json", "persisted-tuning.json" });
                File.WriteAllText(Path.Combine(staging, "fan-control.json"), "older staged fans");
                File.WriteAllText(Path.Combine(staging, "persisted-tuning.json"), "original tuning snapshot");
                check(PortableData.Initialize(target, source) == 1);
                check(File.ReadAllText(Path.Combine(target, "fan-control.json")) == "already committed and edited fans");
                check(File.ReadAllText(Path.Combine(target, "persisted-tuning.json")) == "original tuning snapshot");
                check(!File.Exists(Path.Combine(target, "undervolt-ui.json")));
                check(File.Exists(Path.Combine(target, PortableData.MarkerName)));
                check(!File.Exists(Path.Combine(target, PortableData.JournalName)) && !Directory.Exists(staging));
                check(PortableData.Initialize(target, source) == 0);
            });
        });
        run("A portable import interrupted after its last move still publishes its final marker", () =>
        {
            withTemp(dir =>
            {
                string target = Path.Combine(dir, "portable");
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "fan-control.json"), "committed fans");
                string staging = PrepareImport(target, new[] { "fan-control.json" });
                Directory.Delete(staging);
                check(PortableData.Initialize(target, Path.Combine(dir, "absent")) == 0);
                check(File.ReadAllText(Path.Combine(target, "fan-control.json")) == "committed fans");
                check(File.Exists(Path.Combine(target, PortableData.MarkerName)));
                check(!File.Exists(Path.Combine(target, PortableData.JournalName)) && !Directory.Exists(staging));
            });
        });
        run("A completed portable marker prevents an interrupted cleanup from restoring reset settings", () =>
        {
            withTemp(dir =>
            {
                string target = Path.Combine(dir, "portable");
                Directory.CreateDirectory(target);
                string staging = PrepareImport(target, new[] { "fan-control.json" });
                File.WriteAllText(Path.Combine(staging, "fan-control.json"), "must not restore deleted fans");
                File.WriteAllText(Path.Combine(target, PortableData.MarkerName), "{\"version\":1}");
                check(PortableData.Initialize(target, Path.Combine(dir, "absent")) == 0);
                check(!File.Exists(Path.Combine(target, "fan-control.json")));
                check(!File.Exists(Path.Combine(target, PortableData.JournalName)) && !Directory.Exists(staging));
            });
        });
        run("A failed portable commit rolls back its moves and retains the snapshot for a retry", () =>
        {
            withTemp(dir =>
            {
                string target = Path.Combine(dir, "portable");
                Directory.CreateDirectory(target);
                string staging = PrepareImport(target, new[] { "fan-control.json", "persisted-tuning.json" });
                File.WriteAllText(Path.Combine(staging, "fan-control.json"), "snapshot fans");
                File.WriteAllText(Path.Combine(staging, "persisted-tuning.json"), "snapshot tuning");
                string blocked = Path.Combine(target, "persisted-tuning.json");
                Directory.CreateDirectory(blocked);
                ExpectFailure(() => PortableData.Initialize(target, Path.Combine(dir, "absent")));
                check(!File.Exists(Path.Combine(target, "fan-control.json")));
                check(!File.Exists(Path.Combine(target, PortableData.MarkerName)));
                check(File.ReadAllText(Path.Combine(staging, "fan-control.json")) == "snapshot fans");
                check(File.Exists(Path.Combine(target, PortableData.JournalName)));
                Directory.Delete(blocked);
                check(PortableData.Initialize(target, Path.Combine(dir, "absent")) == 2);
                check(File.ReadAllText(Path.Combine(target, "fan-control.json")) == "snapshot fans");
                check(File.ReadAllText(Path.Combine(target, "persisted-tuning.json")) == "snapshot tuning");
                check(!File.Exists(Path.Combine(target, PortableData.JournalName)) && !Directory.Exists(staging));
            });
        });
        run("Invalid portable journals cannot import arbitrary files or escape the data directory", () =>
        {
            withTemp(dir =>
            {
                string target = Path.Combine(dir, "portable"), outside = Path.Combine(dir, "outside");
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(outside);
                string sentinel = Path.Combine(outside, "fan-control.json");
                File.WriteAllText(sentinel, "outside data");
                string validStaging = ".import-" + Guid.NewGuid().ToString("N");
                string[] invalid =
                {
                    "{broken json",
                    JsonSerializer.Serialize(new { version = 2, staging = validStaging, files = new[] { "fan-control.json" } }),
                    JsonSerializer.Serialize(new { version = 1, staging = "../outside", files = new[] { "fan-control.json" } }),
                    JsonSerializer.Serialize(new { version = 1, staging = outside, files = new[] { "fan-control.json" } }),
                    JsonSerializer.Serialize(new { version = 1, staging = validStaging, files = new[] { "../outside/fan-control.json" } }),
                    JsonSerializer.Serialize(new { version = 1, staging = validStaging, files = new[] { "daemon.pid" } }),
                    JsonSerializer.Serialize(new { version = 1, staging = validStaging, files = new[] { "fan-control.json", "FAN-CONTROL.JSON" } }),
                };
                foreach (string json in invalid)
                {
                    File.WriteAllText(Path.Combine(target, PortableData.JournalName), json);
                    ExpectFailure(() => PortableData.Initialize(target, Path.Combine(dir, "absent")));
                    check(File.ReadAllText(sentinel) == "outside data");
                    check(File.ReadAllText(Path.Combine(target, PortableData.JournalName)) == json);
                    check(!File.Exists(Path.Combine(target, PortableData.MarkerName)));
                    check(!File.Exists(Path.Combine(target, "fan-control.json")));
                }
            });
        });
        run("Global control excludes other portable folders and releases across threads", () =>
        {
            withTemp(dir =>
            {
                string name = TestLeaseName();
                string firstDir = Path.Combine(dir, "one"), secondDir = Path.Combine(dir, "two");
                Directory.CreateDirectory(firstDir);
                Directory.CreateDirectory(secondDir);
                using var first = GlobalControlLease.TryAcquire(name, Path.Combine(firstDir, "hardware-control.lock"));
                check(first is not null && GlobalControlLease.IsHeld(name));
                using (var blocked = GlobalControlLease.TryAcquire(name, Path.Combine(secondDir, "hardware-control.lock"))) check(blocked is null);
                Task.Run(() => first!.Dispose()).GetAwaiter().GetResult();
                check(!GlobalControlLease.IsHeld(name));
                using var next = GlobalControlLease.TryAcquire(name, Path.Combine(secondDir, "hardware-control.lock"));
                check(next is not null);
            });
        });
        run("Global control bridges old file locks and releases its mutex on conflict", () =>
        {
            withTemp(dir =>
            {
                string name = TestLeaseName(), legacy = Path.Combine(dir, "hardware-control.lock");
                using (var oldOwner = new FileStream(legacy, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    using var blocked = GlobalControlLease.TryAcquire(name, legacy);
                    check(blocked is null && !GlobalControlLease.IsHeld(name));
                }
                using (var bridge = GlobalControlLease.TryAcquire(name, legacy))
                {
                    check(bridge is not null);
                    ExpectFailure(() => { using var blocked = new FileStream(legacy, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
                }
                using var reopened = new FileStream(legacy, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                check(!GlobalControlLease.IsHeld(name));
            });
        });
        run("Portable control never creates a missing legacy installation directory", () =>
        {
            withTemp(dir =>
            {
                string missing = Path.Combine(dir, "missing", "hardware-control.lock");
                using var lease = GlobalControlLease.TryAcquire(TestLeaseName(), missing);
                check(lease is not null && !Directory.Exists(Path.GetDirectoryName(missing)));
            });
        });
        run("A crashed global owner releases control to a different portable process", () =>
        {
            string name = TestLeaseName();
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardInput = true,
            };
            start.ArgumentList.Add("--hold-global-lease");
            start.ArgumentList.Add(name);
            using var child = Process.Start(start)!;
            try
            {
                var ready = child.StandardOutput.ReadLineAsync();
                check(ready.Wait(TimeSpan.FromSeconds(10)) && ready.Result == "ready");
                check(GlobalControlLease.IsHeld(name));
                using (var blocked = GlobalControlLease.TryAcquire(name)) check(blocked is null);
                child.Kill();
                check(child.WaitForExit(10000));
                using var recovered = GlobalControlLease.TryAcquire(name);
                check(recovered is not null);
            }
            finally
            {
                if (!child.HasExited) { child.Kill(); child.WaitForExit(10000); }
            }
        });
        run("Global daemon stop waits for cooperative exit without terminating it", () =>
        {
            bool running = true;
            int requests = 0, delays = 0;
            DaemonStop.StopGlobal(() => running, () => { requests++; running = false; },
                milliseconds => { check(milliseconds == 50); delays++; }, attempts: 3);
            check(!running && requests == 1 && delays == 1);
            DaemonStop.StopGlobal(() => false, () => throw new Exception("No process to stop"),
                _ => throw new Exception("Nothing to wait for"), attempts: 3);
        });
        run("Global daemon stop retries an event created after startup and rejects timeouts", () =>
        {
            bool running = true;
            int requests = 0, ticks = 0;
            DaemonStop.StopGlobal(() => running, () => { requests++; if (ticks >= 2) running = false; },
                _ => ticks++, attempts: 5);
            check(!running && requests == 3 && ticks == 3);
            requests = ticks = 0;
            ExpectFailure(() => DaemonStop.StopGlobal(() => true, () => requests++, _ => ticks++, attempts: 4));
            check(requests == 4 && ticks == 4);
        });
        run("An installed upgrade launched elsewhere stops its installed daemon without losing a live PID", () =>
        {
            withTemp(dir =>
            {
                string currentExe = Path.Combine(dir, "Downloads", "NvTuneApp.exe");
                string installedExe = Path.Combine(dir, "Program Files", "NvTune", "NvTuneApp.exe");
                string installedData = Path.Combine(Path.GetDirectoryName(installedExe)!, "data");
                Directory.CreateDirectory(installedData);
                var targets = DaemonControl.StopTargets(false, currentExe, installedExe, installedData, installedData);
                check(targets.Count == 1);
                var target = targets[0];
                check(target.ExecutablePaths.SequenceEqual(new[] { currentExe, installedExe }));
                File.WriteAllText(target.PidPath, "123");
                var daemon = new RecordedDaemonFixture(installedExe, target.ExecutablePaths) { ExitsOnWait = false };
                ExpectFailure(() => DaemonStop.Stop(target.PidPath, _ => daemon, _ => { }));
                check(daemon.StopRequests == 1 && File.ReadAllText(target.PidPath) == "123");
                daemon.ExitsOnWait = true;
                DaemonStop.Stop(target.PidPath, _ => daemon, _ => { });
                check(daemon.StopRequests == 2 && daemon.HasExited && !File.Exists(target.PidPath));
            });
        });
        run("Portable stop keeps its local process path separate from the installed migration target", () =>
        {
            withTemp(dir =>
            {
                string currentExe = Path.Combine(dir, "portable", "NvTunePortable.exe");
                string installedExe = Path.Combine(dir, "installed", "NvTuneApp.exe");
                string currentData = Path.Combine(Path.GetDirectoryName(currentExe)!, "data");
                string installedData = Path.Combine(Path.GetDirectoryName(installedExe)!, "data");
                Directory.CreateDirectory(currentData);
                Directory.CreateDirectory(installedData);
                var targets = DaemonControl.StopTargets(true, currentExe, installedExe, currentData, installedData);
                check(targets.Count == 2);
                check(targets[0].PidPath == Path.Combine(currentData, "daemon.pid"));
                check(targets[0].ExecutablePaths.SequenceEqual(new[] { currentExe }));
                check(targets[1].PidPath == Path.Combine(installedData, "daemon.pid"));
                check(targets[1].ExecutablePaths.SequenceEqual(new[] { installedExe }));
                for (int i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    File.WriteAllText(target.PidPath, "123");
                    var daemon = new RecordedDaemonFixture(i == 0 ? currentExe : installedExe, target.ExecutablePaths);
                    DaemonStop.Stop(target.PidPath, _ => daemon, _ => { });
                    check(daemon.StopRequests == 1 && daemon.HasExited && !File.Exists(target.PidPath));
                }
            });
        });
        run("Shared PID locations retain both exact known executable paths without duplicate stops", () =>
        {
            withTemp(dir =>
            {
                string currentExe = Path.Combine(dir, "NvTunePortable.exe"), installedExe = Path.Combine(dir, "NvTuneApp.exe");
                string data = Path.Combine(dir, "data");
                var targets = DaemonControl.StopTargets(true, currentExe, installedExe, data, data);
                check(targets.Count == 1 && targets[0].ExecutablePaths.SequenceEqual(new[] { currentExe, installedExe }));
                targets = DaemonControl.StopTargets(false, installedExe, installedExe, data, data);
                check(targets.Count == 1 && targets[0].ExecutablePaths.SequenceEqual(new[] { installedExe }));
                check(!targets[0].ExecutablePaths.Contains(Path.Combine(dir, "unrelated.exe"), StringComparer.OrdinalIgnoreCase));
            });
        });
        run("Portable logon tasks bind the current path and file hash with safe XML quoting", () =>
        {
            withTemp(dir =>
            {
                string exe = Path.Combine(dir, "中文 & John's folder", "NvTuneApp.exe");
                string hash = new('a', 64), user = "S-1-5-21-test";
                string xml = Scheduler.BuildXml(exe, "--daemon --silent", user, hash);
                var doc = XDocument.Parse(xml);
                XNamespace ns = doc.Root!.Name.Namespace;
                var action = doc.Root.Element(ns + "Actions")!.Element(ns + "Exec")!;
                check(action.Element(ns + "WorkingDirectory")!.Value == Path.GetDirectoryName(exe));
                check(Path.IsPathRooted(action.Element(ns + "Command")!.Value));
                string arguments = action.Element(ns + "Arguments")!.Value;
                string encoded = arguments.Split("-EncodedCommand ", StringSplitOptions.None).Last();
                string script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
                check(script == Scheduler.BuildLauncherScript(exe, "--daemon --silent", hash));
                check(script.Contains("$exe = '" + exe.Replace("'", "''") + "'"));
                check(script.Contains("[IO.FileShare]::Read") && script.Contains(hash.ToUpperInvariant()));
                check(script.Contains("ComputeHash($stream)") && script.Contains("exit 1"));
                check(!script.Contains(" -Wait") && script.IndexOf("ComputeHash($stream)", StringComparison.Ordinal)
                    < script.IndexOf("Start-Process", StringComparison.Ordinal));
                check(Scheduler.MatchesCurrentExecutable(xml, exe, hash));
                check(!Scheduler.MatchesCurrentExecutable(xml, Path.Combine(dir, "other", "NvTuneApp.exe"), hash));
                check(!Scheduler.MatchesCurrentExecutable(xml, exe, new string('b', 64)));
                check(!Scheduler.MatchesCurrentExecutable("malformed xml", exe, hash));
                check(!Scheduler.MatchesCurrentExecutable(Scheduler.BuildXml(exe, "--other", user, hash), exe, hash));
                ExpectFailure(() => Scheduler.BuildXml(exe, "--daemon --silent", user, "incomplete hash"));
            });
        });
        run("Installed logon tasks launch the protected installed executable directly", () =>
        {
            withTemp(dir =>
            {
                string exe = Path.Combine(dir, "protected & installed", "NvTuneApp.exe");
                string xml = Scheduler.BuildInstalledXml(exe, "--daemon --silent", "S-1-5-21-test");
                var doc = XDocument.Parse(xml);
                XNamespace ns = doc.Root!.Name.Namespace;
                var action = doc.Root.Element(ns + "Actions")!.Element(ns + "Exec")!;
                check(action.Element(ns + "Command")!.Value == exe);
                check(action.Element(ns + "Arguments")!.Value == "--daemon --silent");
                check(action.Element(ns + "WorkingDirectory")!.Value == Path.GetDirectoryName(exe));
            });
        });
        run("Portable PowerShell launcher starts a verified test executable from a quoted path", () =>
        {
            withTemp(dir =>
            {
                string exe = CreateProbeExecutable(dir);
                string marker = Path.Combine(dir, "probe & John's 中文.txt");
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(exe)));
                var result = RunLauncher(exe, "--portable-task-probe \"" + marker + "\"", hash, dir);
                check(result.ExitCode == 0);
                check(SpinWait.SpinUntil(() => File.Exists(marker), TimeSpan.FromSeconds(10)));
                string report = File.ReadAllText(marker);
                check(report.StartsWith("probe started:", StringComparison.Ordinal));
                int pid = int.Parse(report["probe started:".Length..]);
                try
                {
                    using var probe = Process.GetProcessById(pid);
                    if (!probe.WaitForExit(10000))
                    {
                        probe.Kill();
                        probe.WaitForExit(10000);
                        throw new Exception("Isolated launcher probe did not exit");
                    }
                }
                catch (ArgumentException) { } // The probe may already have exited.
            });
        });
        run("Portable PowerShell launcher refuses a changed executable before it can run", () =>
        {
            withTemp(dir =>
            {
                string exe = CreateProbeExecutable(dir);
                string marker = Path.Combine(dir, "must not be created.txt");
                var result = RunLauncher(exe, "--portable-task-probe \"" + marker + "\"", new string('0', 64), dir);
                check(result.ExitCode != 0 && result.Error.Contains("executable changed"));
                check(!File.Exists(marker));
            });
        });
    }

    private static string CreateProbeExecutable(string directory)
    {
        string target = Path.Combine(directory, "fixture & John's 中文");
        Directory.CreateDirectory(target);
        string source = Path.GetDirectoryName(Environment.ProcessPath!)!;
        string exeName = Path.GetFileName(Environment.ProcessPath!);
        foreach (string name in new[] { exeName, "NvTune.Tests.dll", "NvTune.Tests.deps.json",
            "NvTune.Tests.runtimeconfig.json", "NvTuneApp.dll" })
            File.Copy(Path.Combine(source, name), Path.Combine(target, name));
        return Path.Combine(target, exeName);
    }

    private static (int ExitCode, string Error) RunLauncher(string exe, string arguments, string hash, string workingDirectory)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            Arguments = Scheduler.BuildLauncherArguments(exe, arguments, hash), WorkingDirectory = workingDirectory,
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill();
            process.WaitForExit(10000);
            throw new Exception("Isolated PowerShell launcher timed out");
        }
        stdout.GetAwaiter().GetResult();
        return (process.ExitCode, stderr.GetAwaiter().GetResult());
    }

    private static string TestLeaseName() => @"Global\NvTuneTest-" + Guid.NewGuid().ToString("N");

    private static string PrepareImport(string target, string[] files)
    {
        string staging = ".import-" + Guid.NewGuid().ToString("N");
        string path = Path.Combine(target, staging);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(target, PortableData.JournalName),
            JsonSerializer.Serialize(new { version = 1, staging, files }));
        return path;
    }

    private sealed class RecordedDaemonFixture(string actualExecutable, IReadOnlyList<string> expectedExecutables) : IRecordedDaemon
    {
        public bool IsOurs => expectedExecutables.Contains(actualExecutable, StringComparer.OrdinalIgnoreCase);
        public bool HasExited { get; private set; }
        public bool IsVerifiedLegacyDaemon => false;
        public bool ExitsOnWait { get; set; } = true;
        public int StopRequests { get; private set; }
        public bool TryRequestStop() { StopRequests++; return true; }
        public bool WaitForExit(int timeoutMs) { HasExited = ExitsOnWait; return HasExited; }
        public void TerminateLegacy() => throw new Exception("Modern daemons must not be terminated");
        public void Dispose() { }
    }

    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch { return; }
        throw new Exception("Expected failure");
    }
}
