// wslim tests. Plain C# 5, no test framework: built by build.cmd into bin\wslim-tests.exe from this file plus
// src\Core.cs and src\Compact.cs. Exit code = number of failed tests.
//
//   wslim-tests.exe                      unit tests. They use a fake host and temp files only: no DiskPart, no
//                                        wsl.exe, no PowerShell, no elevation. Safe to run anywhere.
//   wslim-tests.exe --integration <dir>  ONLY for CI or a disposable machine, as administrator. Creates throw-away
//                                        VHDX files in <dir> and runs the real DiskPart on them. It never calls
//                                        wsl.exe and never touches a WSL distro. Takes a few minutes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Wslim
{
    class FakeDisk
    {
        public string Name, Path, Registered;
        public long Size = 10L << 30;
        public bool Exists = true, Sparse, Reparse, Held, Attached, AttachedUnknown;
    }

    // An in-memory machine. Records every call in order, so tests can assert what ran and what did not.
    class FakeHost : IHost
    {
        public const int EInvalidArg = unchecked((int)0x80070057);
        public string Dir;
        public List<string> Calls = new List<string>(), Logs = new List<string>(), Hist = new List<string>();
        public List<FakeDisk> Disks = new List<FakeDisk>();
        public HashSet<string> Running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool RunningOk = true, RunningOkAfterShutdown = true, ShutdownStops = true, Elevated = true, WorkerMissing;
        public int ShutdownRc = 0, StartRc = 0, AttachRc = 0, DetachRc = 0;
        public bool DetachWorks = true, DismountWorks = true, HeldAfterCompact;
        public WorkerLaunch Launch = WorkerLaunch.Exited;
        public bool WorkerLeavesNoResult;
        public Func<string, int> CompactRc = p => 0;
        public long ShrinkBy = 1L << 30;
        public List<string> Scripts = new List<string>();
        public static HashSet<string> Locks = new HashSet<string>();
        bool shutdownRan;

        public FakeHost()
        {
            Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wslim-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Running.Add("Ubuntu");
        }
        public FakeDisk Add(string name, string path)
        {
            var d = new FakeDisk { Name = name, Path = path, Registered = path };
            Disks.Add(d); return d;
        }
        FakeDisk Find(string path) { return Disks.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase)); }
        public int Count(string prefix) { return Calls.Count(c => c.StartsWith(prefix)); }
        public int IndexOf(string prefix) { return Calls.FindIndex(c => c.StartsWith(prefix)); }
        public int LastIndexOf(string prefix) { return Calls.FindLastIndex(c => c.StartsWith(prefix)); }
        public string AllText { get { return string.Join("\n", Logs) + "\n" + string.Join("\n", Hist); } }

        public string DataDir { get { return Dir; } }
        public void Write(string text) { Logs.Add(text); }
        public void History(string text) { Hist.Add(text); }
        public void Sleep(int ms) { Calls.Add("sleep " + ms); }

        public int Run(string exe, string args, int timeoutMs, out string output)
        {
            Calls.Add("run " + exe + " " + args);
            output = "";
            if (args == "--shutdown")
            {
                shutdownRan = true;
                if (ShutdownRc == 0 && ShutdownStops) Running.Clear();
                if (ShutdownRc != 0) output = "shutdown said no";
                return ShutdownRc;
            }
            if (args.StartsWith("-d "))
            {
                if (StartRc != 0) { output = "Wsl/Service/CreateInstance/MountVhd/HCS/ERROR_SHARING_VIOLATION"; return StartRc; }
                Running.Add(args.Split(' ')[1]);
                return 0;
            }
            return 0;
        }

        public int RunDiskpart(string script, int timeoutMs, out string output)
        {
            Calls.Add("diskpart");
            Scripts.Add(script);
            output = "";
            var lines = script.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            var path = lines[0].Substring(lines[0].IndexOf('"') + 1).TrimEnd('"');
            var d = Find(path);
            if (d == null) return 1;
            for (int i = 1; i < lines.Length; i++)
            {
                var l = lines[i];
                if (l == "attach vdisk readonly")
                {
                    if (AttachRc != 0) return AttachRc;
                    d.Attached = true;
                }
                else if (l == "compact vdisk")
                {
                    int rc = CompactRc(path);
                    if (rc == Compact.TimedOut) Calls.Add("kill diskpart");
                    if (rc != 0) return rc;
                    d.Size -= ShrinkBy;
                    if (HeldAfterCompact) d.Held = true;
                }
                else if (l == "detach vdisk")
                {
                    if (DetachRc != 0) return DetachRc;
                    if (DetachWorks) d.Attached = false;
                }
                else { output = "The arguments specified for this command are not valid."; return EInvalidArg; }   // what real DiskPart does
            }
            return 0;
        }

        public int Dismount(string path, out string output)
        {
            Calls.Add("dismount " + path);
            output = "";
            var d = Find(path);
            if (d != null && DismountWorks) { d.Attached = false; return 0; }
            return 1;
        }
        public bool? IsAttached(string path)
        {
            Calls.Add("isattached " + path);
            var d = Find(path);
            if (d == null || d.AttachedUnknown) return null;
            return d.Attached;
        }
        public bool CanOpenExclusive(string path)
        {
            Calls.Add("probe " + path);
            var d = Find(path);
            return d != null && d.Exists && !d.Attached && !d.Held;
        }
        public bool FileExists(string path) { var d = Find(path); return d != null && d.Exists; }
        public long FileLength(string path) { return Find(path).Size; }
        public bool IsReparsePoint(string path) { return Find(path).Reparse; }
        public bool IsSparse(string path) { return Find(path).Sparse; }
        public string ScriptPath(string path, out string error) { error = null; return path; }
        public HashSet<string> RunningDistros(out bool ok)
        {
            Calls.Add("running?");
            ok = shutdownRan ? RunningOkAfterShutdown : RunningOk;
            return ok ? new HashSet<string>(Running, StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
        }
        public string RegisteredVhdPath(string name)
        {
            var d = Disks.FirstOrDefault(x => x.Name == name);
            return d == null ? null : d.Registered;
        }
        public List<string> AllDiskPaths() { return Disks.Select(d => d.Path).ToList(); }
        public bool IsElevated() { return Elevated; }
        public string WorkerExe() { return WorkerMissing ? null : @"C:\fake\wslim-cli.exe"; }
        public WorkerLaunch RunWorker(string exe, string jobFile, int timeoutMs, out int exitCode, out string error)
        {
            Calls.Add("worker");
            exitCode = -1; error = null;
            if (Launch != WorkerLaunch.Exited) { error = "start failed"; return Launch; }
            if (WorkerLeavesNoResult) { exitCode = -532462766; return WorkerLaunch.Exited; }   // as if it crashed
            exitCode = Compact.WorkerMain(this, jobFile);   // the real worker logic, in this process, on the fake machine
            return WorkerLaunch.Exited;
        }
        sealed class Held : IDisposable
        {
            readonly string n; public Held(string name) { n = name; }
            public void Dispose() { lock (Locks) Locks.Remove(n); }
        }
        public IDisposable TryLock(string name)
        {
            var key = Dir + "|" + name;
            lock (Locks) { if (!Locks.Add(key)) return null; }
            return new Held(key);
        }
    }

    static class Tests
    {
        static int failures, checks;
        static string current;
        const string A = @"C:\WSL\Ubuntu\ext4.vhdx";
        const string B = @"C:\WSL\Debian\ext4.vhdx";

        static void Check(bool ok, string what)
        {
            checks++;
            if (!ok) throw new Exception("check failed: " + what);
        }
        static void Eq(object expected, object actual, string what)
        {
            checks++;
            if (!object.Equals(expected, actual)) throw new Exception(what + ": expected <" + expected + "> but was <" + actual + ">");
        }
        static void Test(string name, Action body)
        {
            current = name;
            try { body(); Console.WriteLine("PASS  " + name); }
            catch (Exception ex) { failures++; Console.WriteLine("FAIL  " + name + "\n      " + ex.Message); }
        }

        static FakeHost OneDisk()
        {
            var h = new FakeHost();
            h.Add("Ubuntu", A);
            return h;
        }
        static List<DiskSpec> Specs(FakeHost h)
        {
            return h.Disks.Select(d => new DiskSpec { Name = d.Name, Path = d.Path, Size = d.Size }).ToList();
        }
        static List<Distro> Distros(FakeHost h)
        {
            return h.Disks.Select(d => new Distro { Name = d.Name, VhdPath = d.Path, FileSize = d.Size, Version = 2 }).ToList();
        }
        static OpResult Shrink(FakeHost h, CompactMode mode)
        {
            return Operation.Execute(h, new List<Item>(), false, true, Distros(h), mode, NoClean);
        }
        static int NoClean(Distro d, List<Item> picked, bool preview, Action<long> onFreed) { throw new Exception("clean must not be called"); }
        static bool NoGoodNews(string text)
        {
            return text.IndexOf("Done", StringComparison.Ordinal) < 0 && text.IndexOf("success", StringComparison.OrdinalIgnoreCase) < 0
                && text.IndexOf("finished", StringComparison.OrdinalIgnoreCase) < 0;
        }
        static bool JournalExists(FakeHost h) { return File.Exists(Journal.FileIn(h.Dir)); }

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length > 0 && args[0] == "--integration")
            {
                if (args.Length != 2) { Console.WriteLine("usage: wslim-tests.exe --integration <empty-dir>"); return 99; }
                return Integration.Run(args[1]);
            }
            if (args.Length > 0) { Console.WriteLine("usage: wslim-tests.exe [--integration <dir>]"); return 99; }

            Test("script: Detached is exactly 'select vdisk' + 'compact vdisk', no noerr, no attach (acceptance 1, 2)", () =>
            {
                var s = Compact.BuildCompactScript(A, CompactMode.Detached);
                Eq(1, s.Length, "number of scripts");
                Eq("select vdisk file=\"" + A + "\"\r\ncompact vdisk\r\n", s[0], "script text");
                var lines = s[0].Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                Eq(2, lines.Length, "line count");
                Check(s[0].IndexOf("noerr", StringComparison.OrdinalIgnoreCase) < 0, "no noerr");
                Check(s[0].IndexOf("attach", StringComparison.OrdinalIgnoreCase) < 0, "no attach");
                Check(s[0].IndexOf("detach", StringComparison.OrdinalIgnoreCase) < 0, "no detach");
                Eq(CompactMode.Detached, Compact.DefaultMode, "default mode");
            });

            Test("script: AttachReadOnly is three separate scripts, each with its own select, compact never has noerr", () =>
            {
                var s = Compact.BuildCompactScript(A, CompactMode.AttachReadOnly);
                Eq(3, s.Length, "number of scripts");
                Eq("select vdisk file=\"" + A + "\"\r\nattach vdisk readonly\r\n", s[0], "attach script");
                Eq("select vdisk file=\"" + A + "\"\r\ncompact vdisk\r\n", s[1], "compact script");
                Eq("select vdisk file=\"" + A + "\"\r\ndetach vdisk\r\n", s[2], "detach script");
                foreach (var x in s) Check(x.IndexOf("noerr", StringComparison.OrdinalIgnoreCase) < 0, "no noerr anywhere");
            });

            Test("script: a path that could break out of the script is rejected", () =>
            {
                foreach (var bad in new[] { "C:\\a\"\r\nclean\r\n.vhdx", "C:\\a\nb.vhdx", "C:\\a\"b.vhdx", "", @"\\server\share\a.vhdx", "a.vhdx" })
                {
                    bool threw = false;
                    try { Compact.BuildCompactScript(bad, CompactMode.Detached); } catch (ArgumentException) { threw = true; }
                    Check(threw, "rejected: " + bad.Replace("\r", "\\r").Replace("\n", "\\n"));
                }
            });

            Test("timeout: 10 min + 1 min per GB, at least 15 min", () =>
            {
                Eq(15 * 60000, Compact.CompactTimeoutMs(1L << 30), "1 GB");
                Eq(15 * 60000, Compact.CompactTimeoutMs(0), "0");
                Eq(55 * 60000, Compact.CompactTimeoutMs(45L << 30), "45 GB");
                Eq(24 * 60 * 60000, Compact.CompactTimeoutMs(5000L << 30), "5 TB is capped");
            });

            Test("happy path: one diskpart run, verified, journal removed, WSL started again", () =>
            {
                var h = OneDisk();
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Success, r.Outcome, "outcome");
                Eq(1, h.Count("diskpart"), "diskpart runs");
                Eq(1, h.Count("worker"), "one elevation");
                Check(h.IndexOf("run wsl.exe --shutdown") < h.IndexOf("worker"), "shutdown before elevation");
                Check(h.LastIndexOf("isattached") > h.IndexOf("diskpart"), "postcondition after diskpart");
                Check(!JournalExists(h), "journal removed");
                Eq(1, h.Count("run wsl.exe -d Ubuntu -- true"), "distro started again");
                Check(r.Compact.WslRestarted && r.Detail.Contains("WSL is running again"), "says WSL is running again");
                Check(r.Detail.Contains("returned to Windows: 1.0 GB"), "returned to Windows: " + r.Detail);
                Eq(1L << 30, r.Returned, "returned bytes");
            });

            Test("row 2: the incident script (invalid arguments) -> Failed, disk verified not attached", () =>
            {
                // What v1.0.0-v1.1.3 generated. The fake DiskPart rejects it like the real one (E_INVALIDARG).
                var h = OneDisk();
                string output;
                int rc = h.RunDiskpart("select vdisk file=\"" + A + "\"\r\ncompact vdisk noerr\r\n", 1000, out output);
                Eq(FakeHost.EInvalidArg, rc, "fake diskpart rejects 'compact vdisk noerr'");
                // The same exit code from the real flow: Failed, and the disk is checked afterwards.
                h = OneDisk();
                h.CompactRc = p => FakeHost.EInvalidArg;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Failed, r.Outcome, "outcome");
                Check(h.Scripts.All(s => s.IndexOf("noerr", StringComparison.OrdinalIgnoreCase) < 0), "no generated script has noerr");
                Check(h.Scripts.All(s => s.IndexOf("attach", StringComparison.OrdinalIgnoreCase) < 0), "no generated script attaches");
                Check(!h.Disks[0].Attached, "not attached afterwards");
                Eq(false, r.Compact.Disks[0].Attached, "verified not attached");
                Check(r.Compact.Disks[0].Exclusive, "verified openable");
                Check(!JournalExists(h), "verified safe, so the journal is removed");
            });

            Test("row 3: non-zero DiskPart exit code -> Failed, postcondition runs, no good-news words", () =>
            {
                var h = OneDisk();
                h.CompactRc = p => 5;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Failed, r.Outcome, "outcome");
                Check(h.LastIndexOf("isattached") > h.IndexOf("diskpart") && h.LastIndexOf("probe") > h.IndexOf("diskpart"), "state checked after diskpart");
                Check(NoGoodNews(h.AllText + r.Banner), "no Done/Success/finished in: " + h.AllText);
                Check(r.Returned < 0 && !r.Detail.Contains("returned to Windows"), "no 'returned to Windows' for a failure");
                Eq(1, r.ExitCode, "exit code");
            });

            Test("row 4: fallback mode, attach works, compact fails -> independent detach runs, verified, Failed", () =>
            {
                var h = OneDisk();
                h.CompactRc = p => FakeHost.EInvalidArg;
                var r = Shrink(h, CompactMode.AttachReadOnly);
                Eq(Outcome.Failed, r.Outcome, "outcome");
                Eq(3, h.Count("diskpart"), "attach, compact, detach = three separate runs");
                Check(h.Scripts[2].Contains("detach vdisk") && h.Scripts[2].StartsWith("select vdisk"), "detach is its own script with its own select");
                Check(!h.Disks[0].Attached, "detached");
                Eq(false, r.Compact.Disks[0].Attached, "verified detached");
                Eq(2, h.Count("sleep 15000"), "15 s between DiskPart scripts");
                Check(!JournalExists(h), "journal removed after verification");
            });

            Test("row 4b: fallback mode success: attach, compact, detach, verified", () =>
            {
                var h = OneDisk();
                var r = Shrink(h, CompactMode.AttachReadOnly);
                Eq(Outcome.Success, r.Outcome, "outcome");
                Eq(3, h.Count("diskpart"), "three runs");
                Check(!h.Disks[0].Attached && !JournalExists(h), "detached and journal removed");
            });

            Test("row 5: detach fails, Dismount-DiskImage retried once, still attached -> Recovery required, blocked", () =>
            {
                var h = OneDisk();
                h.DetachRc = 1; h.DismountWorks = false;
                var r = Shrink(h, CompactMode.AttachReadOnly);
                Eq(Outcome.RecoveryRequired, r.Outcome, "outcome");
                Eq(1, h.Count("dismount"), "one Dismount-DiskImage retry");
                Check(JournalExists(h), "journal stays");
                var j = Journal.Load(h.Dir);
                Check(j.Disks[0].AttachedByWslim, "journal says wslim attached it");
                Eq(0, h.Count("run wsl.exe -d"), "WSL is NOT started");
                var text = string.Join("\n", r.RecoveryLines);
                Check(text.Contains("wsl --shutdown") && text.Contains("Get-DiskImage -ImagePath \"" + A + "\" | Format-List Attached,DevicePath")
                    && text.Contains("Dismount-DiskImage -ImagePath \"" + A + "\""), "exact commands shown: " + text);
                Eq(3, r.ExitCode, "exit code");
                // Further operations are blocked.
                int before = h.Calls.Count;
                var again = Shrink(h, CompactMode.AttachReadOnly);
                Eq(Outcome.RecoveryRequired, again.Outcome, "second attempt is blocked");
                Eq(before, h.Calls.Count, "the blocked attempt ran nothing at all");
            });

            Test("row 5b: detach fails but Dismount-DiskImage works -> verified detached, not Recovery required", () =>
            {
                var h = OneDisk();
                h.DetachRc = 1;
                var r = Shrink(h, CompactMode.AttachReadOnly);
                Eq(Outcome.Success, r.Outcome, "outcome");
                Eq(1, h.Count("dismount"), "dismount used");
                Check(!h.Disks[0].Attached && !JournalExists(h), "detached, journal removed");
            });

            Test("row 6: DiskPart timeout -> child killed first, then state checked, Failed, no hang", () =>
            {
                var h = OneDisk();
                h.CompactRc = p => Compact.TimedOut;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Failed, r.Outcome, "outcome");
                int kill = h.IndexOf("kill diskpart");
                Check(kill > h.IndexOf("diskpart") && h.LastIndexOf("isattached") > kill, "killed first, then checked");
                Check(!JournalExists(h), "verified, journal removed");
            });

            Test("row 6b: DiskPart cannot be stopped -> Recovery required, nothing else is run, WSL not started", () =>
            {
                var h = OneDisk(); h.Add("Debian", B);
                h.CompactRc = p => Compact.StillRunning;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.RecoveryRequired, r.Outcome, "outcome");
                Eq(1, h.Count("diskpart"), "the second disk is not attempted");
                Check(JournalExists(h), "journal stays");
                Eq(0, h.Count("run wsl.exe -d"), "WSL is NOT started");
            });

            Test("row 6c: the elevated worker does not end in time, or leaves no result -> Recovery required", () =>
            {
                var h = OneDisk(); h.Launch = WorkerLaunch.TimedOut;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.RecoveryRequired, r.Outcome, "timeout outcome");
                Check(JournalExists(h), "journal stays (timeout)");
                Eq(0, h.Count("run wsl.exe -d"), "WSL not started (timeout)");
                h = OneDisk(); h.WorkerLeavesNoResult = true;
                r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.RecoveryRequired, r.Outcome, "no-result outcome");
                Check(JournalExists(h), "journal stays (no result)");
            });

            Test("row 7: user declines UAC -> Cancelled, says WSL was already shut down, no disk command, WSL started again", () =>
            {
                var h = OneDisk(); h.Launch = WorkerLaunch.UacDeclined;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Cancelled, r.Outcome, "outcome");
                Eq(0, h.Count("diskpart"), "no disk command");
                Eq(0, h.Count("dismount"), "no dismount");
                Check(r.Compact.WslStopped && r.Detail.Contains("WSL had already been shut down"), "reports the shutdown: " + r.Detail);
                Check(!JournalExists(h), "no journal left: the worker never ran");
                Eq(1, h.Count("run wsl.exe -d Ubuntu -- true"), "distro started again");
                Eq(1, r.ExitCode, "exit code");
                Check(NoGoodNews(h.AllText + r.Banner), "no good-news words");
            });

            Test("row 8: journal left by a killed run -> start is blocked, recovery steps shown, nothing runs", () =>
            {
                var h = OneDisk();
                var j = new Journal { Id = "abc", Pid = 4242, Started = "2026-11-27T01:45:10", Phase = "working", Mode = "Detached" };
                j.Disks.Add(new JournalDisk { Name = "Ubuntu", Path = A, Size = 5 });
                j.Save(h.Dir);
                var p = Journal.Load(h.Dir);
                Check(p != null && !p.Corrupt && p.Disks.Count == 1 && p.Disks[0].Path == A && p.Pid == 4242 && p.Phase == "working", "journal read back");
                bool cleanCalled = false;
                var d = new Distro { Name = "Ubuntu", VhdPath = A };
                var items = new List<Item> { new Item { Id = "x", Title = "x", Tier = "safe", Paths = new string[0], Distro = d } };
                var r = Operation.Execute(h, items, false, true, Distros(h), CompactMode.Detached, (a, b, c, e) => { cleanCalled = true; return 0; });
                Eq(Outcome.RecoveryRequired, r.Outcome, "clean+shrink blocked");
                r = Operation.Execute(h, items, true, false, Distros(h), CompactMode.Detached, (a, b, c, e) => { cleanCalled = true; return 0; });
                Eq(Outcome.RecoveryRequired, r.Outcome, "preview blocked");
                Check(!cleanCalled, "no clean ran");
                Eq(0, h.Calls.Count, "no host call at all");
                Check(string.Join("\n", r.RecoveryLines).Contains("Dismount-DiskImage -ImagePath \"" + A + "\""), "recovery commands shown");
                Eq(Outcome.RecoveryRequired, Compact.Run(h, Specs(h), CompactMode.Detached).Outcome, "Compact.Run blocked too");
                // A journal that cannot be read still blocks.
                File.WriteAllText(Journal.FileIn(h.Dir), "{ half written");
                p = Journal.Load(h.Dir);
                Check(p != null && p.Corrupt, "a corrupt journal is still a journal");
            });

            Test("row 8b: recover check clears the journal only when the disk is verified not attached", () =>
            {
                var h = OneDisk();
                var j = new Journal { Id = "abc", Phase = "working", Mode = "Detached" };
                j.Disks.Add(new JournalDisk { Name = "Ubuntu", Path = A });
                j.Save(h.Dir);
                h.Disks[0].Attached = true;
                var lines = new List<string>();
                Check(!Compact.RecoverCheck(h, lines) && JournalExists(h), "attached: stays blocked");
                h.Disks[0].Attached = false; h.Disks[0].AttachedUnknown = true;
                Check(!Compact.RecoverCheck(h, lines) && JournalExists(h), "unknown: stays blocked");
                h.Disks[0].AttachedUnknown = false;
                Check(Compact.RecoverCheck(h, lines) && !JournalExists(h), "not attached: cleared");
                Eq(0, h.Count("diskpart") + h.Count("dismount") + h.Count("run "), "the check is read-only");
            });

            Test("row 9 (unit): disk already attached by something else -> Recovery required, never detached", () =>
            {
                foreach (var mode in new[] { CompactMode.Detached, CompactMode.AttachReadOnly })
                {
                    var h = OneDisk(); h.Disks[0].Attached = true;
                    var r = Shrink(h, mode);
                    Eq(Outcome.RecoveryRequired, r.Outcome, "outcome " + mode);
                    Eq(0, h.Count("diskpart"), "no diskpart " + mode);
                    Eq(0, h.Count("dismount"), "never dismounted " + mode);
                    Check(h.Disks[0].Attached, "still attached " + mode);
                    Eq(0, h.Count("run wsl.exe -d"), "WSL not started " + mode);
                    Check(JournalExists(h), "journal stays " + mode);
                }
            });

            Test("preflight: disk in use by another program -> Failed, nothing changed, WSL not started", () =>
            {
                var h = OneDisk(); h.Disks[0].Held = true;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Failed, r.Outcome, "outcome");
                Eq(0, h.Count("diskpart"), "no diskpart");
                Check(!JournalExists(h), "nothing was touched: journal removed");
                Eq(0, h.Count("run wsl.exe -d"), "WSL not started while the disk is held");
                Check(r.Detail.Contains("in use by another program"), r.Detail);
            });

            Test("preflight: attachment state cannot be read -> Failed, no disk command", () =>
            {
                var h = OneDisk(); h.Disks[0].AttachedUnknown = true;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Failed, r.Outcome, "outcome");
                Eq(0, h.Count("diskpart"), "no diskpart");
                Eq(0, h.Count("run wsl.exe -d"), "WSL not started: state not verified");
            });

            Test("postcondition: disk not openable after compaction -> Recovery required", () =>
            {
                var h = OneDisk(); h.HeldAfterCompact = true;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.RecoveryRequired, r.Outcome, "outcome");
                Check(JournalExists(h), "journal stays");
                Eq(0, h.Count("run wsl.exe -d"), "WSL not started");
            });

            Test("row 10: wsl --shutdown fails, distros keep running, or WSL cannot be asked -> Failed before any disk command", () =>
            {
                var h = OneDisk(); h.ShutdownRc = 1;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Failed, r.Outcome, "shutdown failed");
                Eq(0, h.Count("worker") + h.Count("diskpart"), "no elevation, no disk command (shutdown failed)");
                Check(!JournalExists(h), "no journal (shutdown failed)");

                h = OneDisk(); h.ShutdownStops = false;
                r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Failed, r.Outcome, "still running");
                Eq(0, h.Count("worker") + h.Count("diskpart"), "no elevation, no disk command (still running)");
                Check(r.Detail.Contains("still running: Ubuntu"), r.Detail);

                h = OneDisk(); h.RunningOkAfterShutdown = false;   // G7: a failed query is "unknown", not "nothing running"
                r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Failed, r.Outcome, "query failed after shutdown");
                Eq(0, h.Count("worker") + h.Count("diskpart"), "no elevation, no disk command (query failed)");

                h = OneDisk(); h.RunningOk = false;
                r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Failed, r.Outcome, "query failed before shutdown");
                Eq(0, h.Count("run wsl.exe --shutdown") + h.Count("worker"), "WSL not even stopped");
            });

            Test("row 11: second concurrent request is refused, the first is unaffected", () =>
            {
                var h = OneDisk();
                using (var first = h.TryLock(Compact.LockName))
                {
                    Check(first != null, "first holds the lock");
                    var r = Shrink(h, CompactMode.Detached);
                    Eq(Outcome.Failed, r.Outcome, "second refused");
                    Check(r.Detail.Contains("refused"), r.Detail);
                    Eq(0, h.Calls.Count, "the refused request ran nothing");
                    Check(h.TryLock(Compact.LockName) == null, "the first still holds the lock");
                }
                Eq(Outcome.Success, Shrink(h, CompactMode.Detached).Outcome, "works again after the first released");
            });

            Test("row 11b: the real named mutex refuses a second holder and frees on release", () =>
            {
                var name = "wslim-test-" + Guid.NewGuid().ToString("N");
                using (var first = ProcessLock.TryAcquire(name))
                {
                    Check(first != null, "first acquired");
                    ProcessLock second = null;
                    var t = new Thread(() => { second = ProcessLock.TryAcquire(name); });
                    t.Start(); t.Join();
                    Check(second == null, "second refused");
                }
                ProcessLock third = null;
                var t2 = new Thread(() => { third = ProcessLock.TryAcquire(name); if (third != null) third.Dispose(); });
                t2.Start(); t2.Join();
                Check(third != null, "free again after release");
            });

            Test("row 12: several disks, one fails -> Partial success, per-disk outcomes, 15 s between DiskPart runs", () =>
            {
                var h = OneDisk(); h.Add("Debian", B);
                h.CompactRc = p => p == B ? 7 : 0;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.PartialSuccess, r.Outcome, "overall");
                Eq(Outcome.Success, r.Compact.Disks[0].Outcome, "Ubuntu");
                Eq(Outcome.Failed, r.Compact.Disks[1].Outcome, "Debian");
                Eq(2, h.Count("diskpart"), "one DiskPart process per disk");
                Eq(1, h.Count("sleep 15000"), "pause between the two scripts");
                Check(h.IndexOf("sleep 15000") > h.IndexOf("diskpart") && h.IndexOf("sleep 15000") < h.LastIndexOf("diskpart"), "the pause is between them");
                Check(r.Detail.Contains("returned to Windows: 1.0 GB") && r.Detail.Contains("Debian: "), r.Detail);
                Eq(1, r.ExitCode, "exit code");
                // The first one failing must not be hidden by the last one succeeding (G9).
                h = OneDisk(); h.Add("Debian", B);
                h.CompactRc = p => p == A ? 7 : 0;
                Eq(Outcome.PartialSuccess, Shrink(h, CompactMode.Detached).Outcome, "first fails, last succeeds");
            });

            Test("row 13: compaction succeeds with zero bytes reclaimed -> No space reclaimed, distinct from Failed", () =>
            {
                var h = OneDisk(); h.ShrinkBy = 0;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.NoSpaceReclaimed, r.Outcome, "outcome");
                Eq("No space reclaimed", r.Word, "word");
                Eq(0, r.ExitCode, "exit code");
                Check(r.Detail.Contains("returned to Windows: 0"), r.Detail);
            });

            Test("row 14: preview when the distro cannot start -> 'Preview failed', no shrink line, no shutdown, no elevation", () =>
            {
                var h = OneDisk();
                var d = new Distro { Name = "Ubuntu", VhdPath = A };
                var items = new List<Item> { new Item { Id = "apt", Title = "apt cache", Tier = "safe", Paths = new string[0], Distro = d } };
                var r = Operation.Execute(h, items, true, true, Distros(h), CompactMode.Detached, (dd, picked, preview, onFreed) =>
                {
                    Check(preview, "clean is called in preview mode");
                    dd.Error = "could not start Ubuntu: Wsl/Service/CreateInstance/MountVhd/HCS/ERROR_SHARING_VIOLATION";
                    return -1;
                });
                Eq(Outcome.Failed, r.Outcome, "outcome");
                Check(r.Detail.StartsWith("Preview failed for Ubuntu"), r.Detail);
                Check(h.Logs.Any(l => l.StartsWith("Preview failed for Ubuntu") && l.Contains("ERROR_SHARING_VIOLATION")), "log names the distro and the WSL error");
                Check(!h.AllText.Contains("shrink") && !h.AllText.Contains("Would then"), "no 'would then shrink' line");
                Check(h.AllText.IndexOf("finished", StringComparison.OrdinalIgnoreCase) < 0 && !r.Detail.Contains("finished"), "never claims finished");
                Eq(0, h.Calls.Count, "no shutdown, no worker, no elevation, no disk command");
                Eq(0, h.Hist.Count, "a preview writes no history");
                Eq(1, r.ExitCode, "exit code");
                // A working preview shows the shrink line and still runs nothing.
                h = OneDisk();
                r = Operation.Execute(h, items, true, true, Distros(h), CompactMode.Detached, (dd, picked, preview, onFreed) => 0);
                Eq(Outcome.Success, r.Outcome, "working preview");
                Check(h.Logs.Any(l => l.StartsWith("Would then shut WSL down")), "shrink line shown for a working preview");
                Eq(0, h.Calls.Count, "a working preview runs nothing either");
            });

            Test("row 16: banner, log line, history.tsv and exit code agree for all six outcomes", () =>
            {
                var seen = new HashSet<string>();
                var cases = new List<KeyValuePair<Outcome, Func<OpResult>>>();
                FakeHost h = null;
                cases.Add(new KeyValuePair<Outcome, Func<OpResult>>(Outcome.Success, () => { h = OneDisk(); return Shrink(h, CompactMode.Detached); }));
                cases.Add(new KeyValuePair<Outcome, Func<OpResult>>(Outcome.NoSpaceReclaimed, () => { h = OneDisk(); h.ShrinkBy = 0; return Shrink(h, CompactMode.Detached); }));
                cases.Add(new KeyValuePair<Outcome, Func<OpResult>>(Outcome.PartialSuccess, () => { h = OneDisk(); h.Add("Debian", B); h.CompactRc = p => p == B ? 7 : 0; return Shrink(h, CompactMode.Detached); }));
                cases.Add(new KeyValuePair<Outcome, Func<OpResult>>(Outcome.Failed, () => { h = OneDisk(); h.CompactRc = p => 7; return Shrink(h, CompactMode.Detached); }));
                cases.Add(new KeyValuePair<Outcome, Func<OpResult>>(Outcome.Cancelled, () => { h = OneDisk(); h.Launch = WorkerLaunch.UacDeclined; return Shrink(h, CompactMode.Detached); }));
                cases.Add(new KeyValuePair<Outcome, Func<OpResult>>(Outcome.RecoveryRequired, () => { h = OneDisk(); h.Disks[0].Attached = true; return Shrink(h, CompactMode.Detached); }));
                foreach (var c in cases)
                {
                    var r = c.Value();
                    var word = Report.Word(c.Key);
                    Eq(c.Key, r.Outcome, "outcome");
                    Check(seen.Add(word), "the word is distinct: " + word);
                    Check(r.Banner.StartsWith(word + " · "), "banner starts with the word: " + r.Banner);
                    Check(h.Logs.Contains(word + ": " + r.Detail), "log has '" + word + ": ...'");
                    Eq(1, h.Hist.Count, "one history line for " + word);
                    Eq(word + ": " + r.Detail, h.Hist[0], "history line");
                    bool good = c.Key == Outcome.Success || c.Key == Outcome.NoSpaceReclaimed;
                    Eq(good ? 0 : (c.Key == Outcome.RecoveryRequired ? 3 : 1), r.ExitCode, "exit code for " + word);
                    Check(!(h.AllText + r.Banner).Contains("Done"), "'Done' is never written (" + word + ")");
                    if (c.Key == Outcome.Failed || c.Key == Outcome.Cancelled || c.Key == Outcome.RecoveryRequired)
                        Check(NoGoodNews(h.AllText + r.Banner), "no Done/Success/finished for " + word + ": " + h.AllText);
                    if (c.Key == Outcome.RecoveryRequired)
                        Check(h.Logs.Any(l => l.Contains("Dismount-DiskImage -ImagePath \"" + A + "\"")), "recovery commands are in the log");
                }
                Eq(6, seen.Count, "six distinct outcome words");
            });

            Test("reporting: 'freed inside Linux' and 'returned to Windows' are separate; cleanup ok + compaction failed = Partial success", () =>
            {
                var h = OneDisk();
                var d = new Distro { Name = "Ubuntu", VhdPath = A, FileSize = 10L << 30 };
                var items = new List<Item> { new Item { Id = "apt", Title = "apt cache", Tier = "safe", Paths = new string[0], Distro = d } };
                CleanFn clean = (dd, picked, preview, onFreed) => { onFreed(698L << 20); return 0; };
                var r = Operation.Execute(h, items, false, true, Distros(h), CompactMode.Detached, clean);
                Eq(Outcome.Success, r.Outcome, "outcome");
                Check(r.Detail.Contains("freed inside Linux: 698 MB") && r.Detail.Contains("returned to Windows: 1.0 GB"), r.Detail);
                Eq(698L << 20, r.FreedInside, "freed inside");

                h = OneDisk(); h.CompactRc = p => 7;
                r = Operation.Execute(h, items, false, true, Distros(h), CompactMode.Detached, clean);
                Eq(Outcome.PartialSuccess, r.Outcome, "cleanup worked, compaction did not");
                Check(r.Detail.Contains("freed inside Linux: 698 MB") && !r.Detail.Contains("returned to Windows"), r.Detail);

                // Cleanup failed (distro did not start): the disks are not shrunk and WSL is not stopped.
                h = OneDisk();
                r = Operation.Execute(h, items, false, true, Distros(h), CompactMode.Detached, (dd, picked, preview, onFreed) => { dd.Error = "could not start Ubuntu: boom"; return -1; });
                Eq(Outcome.Failed, r.Outcome, "cleanup failed");
                Eq(0, h.Calls.Count, "no shutdown and no worker after a failed cleanup");
                Check(r.Detail.Contains("could not start Ubuntu: boom") && NoGoodNews(h.AllText + r.Banner), r.Detail);
            });

            Test("row 17: VHDX path changed between scan and shrink -> Failed at preflight, WSL not stopped", () =>
            {
                var h = OneDisk(); h.Disks[0].Registered = @"D:\moved\ext4.vhdx";
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Failed, r.Outcome, "outcome");
                Check(r.Detail.Contains("path changed since the scan"), r.Detail);
                Eq(0, h.Count("run ") + h.Count("worker") + h.Count("diskpart"), "nothing ran");
                // Also: missing file, not a .vhdx, reparse point.
                h = OneDisk(); h.Disks[0].Exists = false;
                Eq(Outcome.Failed, Shrink(h, CompactMode.Detached).Outcome, "missing file");
                Eq(0, h.Count("run ") + h.Count("worker"), "nothing ran (missing)");
                h = OneDisk(); h.Disks[0].Reparse = true;
                Eq(Outcome.Failed, Shrink(h, CompactMode.Detached).Outcome, "reparse point");
                h = new FakeHost(); h.Add("Ubuntu", @"C:\WSL\Ubuntu\ext4.vhd");
                Eq(Outcome.Failed, Shrink(h, CompactMode.Detached).Outcome, "not .vhdx");
                Eq(0, h.Count("run ") + h.Count("worker"), "nothing ran (not vhdx)");
            });

            Test("row 17b: the worker re-checks the registry itself and refuses a job for an unregistered path", () =>
            {
                var h = OneDisk();
                var j = new Journal { Id = "id1", Phase = "worker-starting", Mode = "Detached" };
                j.Disks.Add(new JournalDisk { Name = "Ubuntu", Path = @"C:\Windows\other.vhdx" });
                j.Save(h.Dir);
                var job = Path.Combine(h.Dir, Compact.JobFileName);
                File.WriteAllText(job, "{\"wslim\":\"compact-job\",\"id\":\"id1\",\"mode\":\"Detached\",\"disks\":[{\"name\":\"Ubuntu\",\"path\":\"C:\\\\Windows\\\\other.vhdx\",\"size\":1}]}");
                Eq(Compact.WorkerRefused, Compact.WorkerMain(h, job), "unregistered path refused");
                Eq(0, h.Count("diskpart"), "no disk command");
            });

            Test("worker: refuses when not elevated, for a job outside the data folder, a bad job, or no matching journal", () =>
            {
                var h = OneDisk();
                var job = Path.Combine(h.Dir, Compact.JobFileName);
                var good = "{\"wslim\":\"compact-job\",\"id\":\"id1\",\"mode\":\"Detached\",\"disks\":[{\"name\":\"Ubuntu\",\"path\":\"C:\\\\WSL\\\\Ubuntu\\\\ext4.vhdx\",\"size\":1}]}";
                File.WriteAllText(job, good);
                Eq(Compact.WorkerRefused, Compact.WorkerMain(h, job), "no journal");
                var j = new Journal { Id = "id1", Phase = "worker-starting", Mode = "Detached" };
                j.Disks.Add(new JournalDisk { Name = "Ubuntu", Path = A });
                j.Save(h.Dir);
                h.Elevated = false;
                Eq(Compact.WorkerRefused, Compact.WorkerMain(h, job), "not elevated");
                h.Elevated = true;
                var outside = Path.Combine(Path.GetTempPath(), "wslim-tests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(outside);
                File.WriteAllText(Path.Combine(outside, Compact.JobFileName), good);
                Eq(Compact.WorkerRefused, Compact.WorkerMain(h, Path.Combine(outside, Compact.JobFileName)), "job outside the data folder");
                var other = Path.Combine(h.Dir, "other.json"); File.WriteAllText(other, good);
                Eq(Compact.WorkerRefused, Compact.WorkerMain(h, other), "wrong file name");
                File.WriteAllText(job, "not json");
                Eq(Compact.WorkerRefused, Compact.WorkerMain(h, job), "unparsable");
                File.WriteAllText(job, good.Replace("id1", "id2"));
                Eq(Compact.WorkerRefused, Compact.WorkerMain(h, job), "id does not match the journal");
                Eq(0, h.Count("diskpart") + h.Count("isattached"), "a refusing worker looks at no disk");
                File.WriteAllText(job, good);
                Eq(Compact.WorkerOk, Compact.WorkerMain(h, job), "the genuine job is accepted");
                Eq(1, h.Count("diskpart"), "and compacts");
            });

            Test("row 18: sparse disk -> skipped with an explanation, no disk command, WSL not stopped", () =>
            {
                var h = OneDisk(); h.Disks[0].Sparse = true;
                var r = Shrink(h, CompactMode.Detached);
                Check(r.Compact.Disks[0].Skipped, "skipped");
                Check(h.Logs.Any(l => l.Contains("sparse file, which Windows shrinks by itself")), "explained");
                Eq(0, h.Count("run ") + h.Count("worker") + h.Count("diskpart"), "nothing ran");
                Check(r.Outcome != Outcome.Failed && r.Outcome != Outcome.RecoveryRequired, "not a failure: " + r.Outcome);
            });

            Test("missing wslim-cli.exe -> Failed with a clear message, WSL not stopped", () =>
            {
                var h = OneDisk(); h.WorkerMissing = true;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Failed, r.Outcome, "outcome");
                Check(r.Detail.Contains("wslim-cli.exe was not found"), r.Detail);
                Eq(0, h.Count("run ") + h.Count("worker"), "nothing ran");
            });

            Test("restart: a distro that will not start again is reported with the exact WSL error", () =>
            {
                var h = OneDisk(); h.StartRc = -1;
                var r = Shrink(h, CompactMode.Detached);
                Eq(Outcome.Success, r.Outcome, "the compaction itself worked");
                Check(!r.Compact.WslRestarted && r.Detail.Contains("could not start Ubuntu") && r.Detail.Contains("ERROR_SHARING_VIOLATION"), r.Detail);
                Check(!r.Detail.Contains("WSL is running again"), "does not claim WSL is running");
            });

            Test("consent text: says wsl --shutdown stops ALL distros, names them, and that wslim starts them again", () =>
            {
                var running = new HashSet<string> { "Ubuntu", "Debian" };
                var t = string.Join("\n", Compact.ConsentLines(running, true));
                Check(t.Contains("wsl --shutdown") && t.Contains("ALL"), t);
                Check(t.Contains("Debian, Ubuntu"), "names every running distro");
                Check(t.Contains("starts the same distros again"), "promises the restart");
                t = string.Join("\n", Compact.ConsentLines(new HashSet<string>(), false));
                Check(t.Contains("could not ask WSL"), "says when the list is unknown");
            });

            Test("journal and JSON: round trip, including non-ASCII paths", () =>
            {
                var h = new FakeHost();
                var path = "C:\\Users\\J\u00f6s\u00e9 \u4e2d\\disk \u00fc \"x\".vhdx";
                var j = new Journal { Id = "i", Pid = 7, Started = "s", Phase = "p", Mode = "AttachReadOnly" };
                j.Running.Add("Ubuntu"); j.Running.Add("D\u00e9bian");
                j.Disks.Add(new JournalDisk { Name = "n", Path = path, Size = 123456789012L, AttachedByWslim = true, Phase = "attaching" });
                j.Save(h.Dir);
                var k = Journal.Load(h.Dir);
                Check(!k.Corrupt, "parsed");
                Eq(path, k.Disks[0].Path, "path");
                Eq(123456789012L, k.Disks[0].Size, "size");
                Check(k.Disks[0].AttachedByWslim, "attachedByWslim");
                Eq("attaching", k.Disks[0].Phase, "phase");
                Eq("D\u00e9bian", k.Running[1], "running");
                Journal.Clear(h.Dir);
                Check(Journal.Load(h.Dir) == null, "cleared");
                bool threw = false;
                try { Json.Parse("{\"a\": [1, 2"); } catch (FormatException) { threw = true; }
                Check(threw, "bad JSON throws");
            });

            Test("real host: path for DiskPart never creates a file (G14); exclusive-open probe works on temp files", () =>
            {
                var host = new RealHost();
                var dir = Path.Combine(Path.GetTempPath(), "wslim-tests", Guid.NewGuid().ToString("N"), "J\u00f6s\u00e9 test");
                Directory.CreateDirectory(dir);
                try
                {
                    var missing = Path.Combine(dir, "missing \u00fc.vhdx");
                    string err;
                    host.ScriptPath(missing, out err);
                    Check(!File.Exists(missing), "no file was created for a path that does not exist");
                    var file = Path.Combine(dir, "disk \u00fc.vhdx");
                    File.WriteAllText(file, "x");
                    var sp = host.ScriptPath(file, out err);
                    Check(sp != null || err != null, "a path or a clear error");
                    if (sp != null && sp != file)
                    {
                        Check(sp.All(ch => ch < 128), "the short path is plain ASCII: " + sp);
                        Eq("x", File.ReadAllText(sp), "the short path is the same file");
                    }
                    Console.WriteLine("      (path for DiskPart on this PC: " + (sp ?? "none: " + err) + ")");
                    Eq(@"C:\plain\ext4.vhdx", host.ScriptPath(@"C:\plain\ext4.vhdx", out err), "an ASCII path is used as it is");
                    Check(host.CanOpenExclusive(file), "free file can be opened exclusively");
                    using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        Check(!host.CanOpenExclusive(file), "a file somebody holds open cannot");
                    Check(!host.IsSparse(file) && !host.IsReparsePoint(file), "plain file attributes");
                }
                finally { try { Directory.Delete(Path.GetDirectoryName(dir), true); } catch { } }
            });

            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "wslim-tests"), true); } catch { }
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "All tests passed (" + checks + " checks)." : failures + " test(s) FAILED.");
            return failures;
        }
    }

    // ------------------------------------------------------------------ integration (CI only, administrator)
    // The real RealHost for everything about disks; WSL is replaced, so wsl.exe is never run and no distro is touched.
    class ItHost : RealHost
    {
        public string Dir;
        public Dictionary<string, string> Registered = new Dictionary<string, string>();
        public override string DataDir { get { return Dir; } }
        public override void Write(string text) { Console.WriteLine("      | " + text); }
        public override void History(string text) { }
        public override int Run(string exe, string args, int timeoutMs, out string output) { output = ""; return 0; }   // never wsl.exe
        public override HashSet<string> RunningDistros(out bool ok) { ok = true; return new HashSet<string>(); }
        public override string RegisteredVhdPath(string name) { string p; return Registered.TryGetValue(name, out p) ? p : null; }
        public override List<string> AllDiskPaths() { return Registered.Values.ToList(); }
        public override string WorkerExe() { return "in-process"; }
        public override WorkerLaunch RunWorker(string exe, string jobFile, int timeoutMs, out int exitCode, out string error)
        {
            error = null;
            exitCode = Compact.WorkerMain(this, jobFile);   // this process is already elevated
            return WorkerLaunch.Exited;
        }
    }

    static class Integration
    {
        static int failures;
        static readonly List<string> Created = new List<string>();

        static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
            if (!ok) failures++;
        }

        static ItHost NewHost(string root, string name, string vhd)
        {
            var h = new ItHost { Dir = Path.Combine(root, "data-" + Guid.NewGuid().ToString("N")) };
            Directory.CreateDirectory(h.Dir);
            h.Registered[name] = vhd;
            return h;
        }

        static int Diskpart(RealHost h, string script)
        {
            string output;
            int rc = h.RunDiskpart(script, 300000, out output);
            foreach (var l in output.Split('\n')) if (l.Trim().Length > 0 && !l.StartsWith("Microsoft") && !l.StartsWith("Copyright") && !l.StartsWith("On computer")) Console.WriteLine("      > " + l.Trim());
            Thread.Sleep(Compact.ScriptPauseMs);
            return rc;
        }

        // A throw-away expandable VHDX. With data: it gets an NTFS volume, a 64 MB file is written and deleted
        // again, so there is something to reclaim. Data preparation is best effort.
        static bool MakeDisk(RealHost h, string path, bool withData, string mountDir)
        {
            Created.Add(path);
            int rc = Diskpart(h, "create vdisk file=\"" + path + "\" maximum=200 type=expandable\r\n");
            if (rc != 0 || !File.Exists(path)) { Console.WriteLine("  could not create " + path + " (exit code " + rc + ")"); return false; }
            if (!withData) return true;
            try
            {
                Directory.CreateDirectory(mountDir);
                rc = Diskpart(h, "select vdisk file=\"" + path + "\"\r\nattach vdisk\r\nconvert mbr\r\ncreate partition primary\r\nformat fs=ntfs quick\r\nassign mount=\"" + mountDir + "\"\r\n");
                if (rc == 0)
                {
                    var junk = Path.Combine(mountDir, "junk.bin");
                    var buf = new byte[1 << 20]; var rnd = new Random(1);
                    using (var fs = File.Create(junk)) for (int i = 0; i < 64; i++) { rnd.NextBytes(buf); fs.Write(buf, 0, buf.Length); }
                    File.Delete(junk);
                    Console.WriteLine("  data prepared in " + path);
                }
                else Console.WriteLine("  NOTE: could not prepare data in the test disk (exit code " + rc + "); continuing with an empty disk");
            }
            catch (Exception ex) { Console.WriteLine("  NOTE: could not prepare data: " + ex.Message); }
            if (h.IsAttached(path) != false) Diskpart(h, "select vdisk file=\"" + path + "\"\r\ndetach vdisk\r\n");
            if (h.IsAttached(path) != false) { string o; h.Dismount(path, out o); }
            return h.IsAttached(path) == false;
        }

        static CompactResult RunOne(ItHost h, string name, string vhd, CompactMode mode)
        {
            var specs = new List<DiskSpec> { new DiskSpec { Name = name, Path = vhd, Size = new FileInfo(vhd).Length } };
            return Compact.Run(h, specs, mode);
        }

        public static int Run(string root)
        {
            var real = new RealHost();
            if (!real.IsElevated()) { Console.WriteLine("The integration tests need an administrator prompt (they run DiskPart on throw-away disks)."); return 98; }
            root = Path.GetFullPath(root);
            Directory.CreateDirectory(root);
            Console.WriteLine("Integration tests in " + root + " (throw-away VHDX files only; wsl.exe is never run)");
            try
            {
                // ---- row 1: successful compaction, in both modes (this is also the measurement for report question Q1)
                foreach (var mode in new[] { CompactMode.Detached, CompactMode.AttachReadOnly })
                {
                    Console.WriteLine("row 1 (" + mode + "): successful compaction");
                    var vhd = Path.Combine(root, "row1-" + mode + ".vhdx");
                    if (!MakeDisk(real, vhd, true, Path.Combine(root, "mnt-" + mode))) { Check(false, "test disk created and detached"); continue; }
                    var h = NewHost(root, "it-row1", vhd);
                    long before = new FileInfo(vhd).Length;
                    var r = RunOne(h, "it-row1", vhd, mode);
                    long after = new FileInfo(vhd).Length;
                    Console.WriteLine("  RECLAIM mode=" + mode + " before=" + before + " after=" + after + " outcome=" + r.Outcome + (r.Disks[0].Message != null ? " (" + r.Disks[0].Message + ")" : ""));
                    Check(r.Outcome == Outcome.Success || r.Outcome == Outcome.NoSpaceReclaimed, "outcome is Success or No space reclaimed: " + r.Outcome);
                    Check(after <= before, "the file did not grow");
                    Check(real.IsAttached(vhd) == false, "not attached afterwards");
                    Check(real.CanOpenExclusive(vhd), "can be opened exclusively afterwards");
                    Check(!File.Exists(Journal.FileIn(h.Dir)), "journal removed");
                }

                // ---- row 9: the disk is already attached by something else
                {
                    Console.WriteLine("row 9: disk already attached by something else");
                    var vhd = Path.Combine(root, "row9.vhdx");
                    if (!MakeDisk(real, vhd, false, null)) Check(false, "test disk created");
                    else
                    {
                        int rc = Diskpart(real, "select vdisk file=\"" + vhd + "\"\r\nattach vdisk readonly\r\n");
                        Check(rc == 0 && real.IsAttached(vhd) == true, "pre-attached by the test (exit code " + rc + ")");
                        var h = NewHost(root, "it-row9", vhd);
                        var r = RunOne(h, "it-row9", vhd, CompactMode.Detached);
                        Check(r.Outcome == Outcome.RecoveryRequired, "outcome is Recovery required: " + r.Outcome);
                        Check(real.IsAttached(vhd) == true, "still attached: wslim did not detach it");
                        Check(File.Exists(Journal.FileIn(h.Dir)), "journal stays, further operations are blocked");
                        var lines = new List<string>();
                        Check(!Compact.RecoverCheck(h, lines), "recover check stays blocked while attached");
                        Diskpart(real, "select vdisk file=\"" + vhd + "\"\r\ndetach vdisk\r\n");
                        Check(real.IsAttached(vhd) == false, "detached by the test afterwards");
                        Check(Compact.RecoverCheck(h, lines) && !File.Exists(Journal.FileIn(h.Dir)), "recover check unblocks once detached");
                    }
                }

                // ---- row 15: a path with spaces and non-ASCII characters
                {
                    Console.WriteLine("row 15: path with spaces and non-ASCII characters");
                    var plain = Path.Combine(root, "row15.vhdx");
                    var dir = Path.Combine(root, "J\u00f6s\u00e9 test");
                    var vhd = Path.Combine(dir, "disk \u00fc.vhdx");
                    var decoy = Path.Combine(root, "row15-decoy.vhdx");
                    if (!MakeDisk(real, plain, false, null) || !MakeDisk(real, decoy, false, null)) Check(false, "test disks created");
                    else
                    {
                        Directory.CreateDirectory(dir);
                        File.Move(plain, vhd); Created.Add(vhd);
                        var decoyStamp = File.GetLastWriteTimeUtc(decoy); long decoySize = new FileInfo(decoy).Length;
                        string err;
                        Console.WriteLine("  path for DiskPart: " + (real.ScriptPath(vhd, out err) ?? "none: " + err));
                        var h = NewHost(root, "it-row15", vhd);
                        var r = RunOne(h, "it-row15", vhd, CompactMode.Detached);
                        Console.WriteLine("  outcome=" + r.Outcome + (r.Disks[0].Message != null ? " (" + r.Disks[0].Message + ")" : ""));
                        Check(r.Outcome == Outcome.Success || r.Outcome == Outcome.NoSpaceReclaimed, "outcome is Success or No space reclaimed: " + r.Outcome);
                        Check(File.Exists(vhd) && real.IsAttached(vhd) == false && real.CanOpenExclusive(vhd), "the right file is there, not attached, openable");
                        Check(File.GetLastWriteTimeUtc(decoy) == decoyStamp && new FileInfo(decoy).Length == decoySize, "the other disk was not touched");
                        Check(!File.Exists(Journal.FileIn(h.Dir)), "journal removed");
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine("  FAIL  unexpected: " + ex); failures++; }
            finally
            {
                // Leave nothing attached on the machine: these are the test's own throw-away disks.
                foreach (var p in Created)
                {
                    try { if (File.Exists(p) && real.IsAttached(p) == true) { string o; real.Dismount(p, out o); Console.WriteLine("  cleanup: dismounted " + p); } }
                    catch { }
                }
            }
            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "Integration tests passed." : failures + " integration check(s) FAILED.");
            return failures;
        }
    }
}
