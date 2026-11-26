// wslim core: everything that is not UI. Shared by wslim.exe (App.cs) and wslim-cli.exe (Cli.cs).
// Shrinking the disks (compaction) lives in Compact.cs.
// .NET Framework 4.8, C# 5 so the compiler that ships with Windows can build it. No WPF here.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("wslim")]
[assembly: AssemblyProduct("wslim")]
[assembly: AssemblyDescription("Clean WSL safely and shrink its disks")]
[assembly: AssemblyVersion("1.2.2.0")]
[assembly: AssemblyFileVersion("1.2.2.0")]

namespace Wslim
{
    // ------------------------------------------------------------------ model
    class Distro
    {
        public string Name, BasePath, VhdPath, Os;
        public string Error;   // why the last scan, preview or clean of this distro failed (null when it worked)
        public int Version;
        public bool Running, Managed, Sparse, Scanned;
        public long FileSize = -1, Used = -1;
        public List<Item> Items = new List<Item>();
        public List<string> Notes = new List<string>();
        public bool HasDisk { get { return VhdPath != null && File.Exists(VhdPath); } }
    }

    class Item
    {
        public string Id, Tier, Title, Why;
        public long Bytes;
        public string[] Paths;
        public Distro Distro;
#pragma warning disable 649   // set by the window only
        public object Box;      // the window's CheckBox for this item (unused by the console version)
        public bool? Checked;
#pragma warning restore 649
    }

    // ------------------------------------------------------------------ WSL access
    static class Wsl
    {
        public static bool IsManagedName(string name)
        {
            return name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("rancher-desktop", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("podman-machine", StringComparison.OrdinalIgnoreCase);
        }

        public const string DockerDataName = "Docker Desktop data";

        // What the registry says is registered for this user. Reads the registry and the file system only; never runs wsl.exe.
        public static List<Distro> Registered()
        {
            var list = new List<Distro>();
            using (var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
            {
                if (root == null) return list;
                foreach (var id in root.GetSubKeyNames())
                {
                    using (var k = root.OpenSubKey(id))
                    {
                        if (k == null) continue;
                        var name = k.GetValue("DistributionName") as string;
                        if (string.IsNullOrEmpty(name)) continue;
                        var basePath = (k.GetValue("BasePath") as string ?? "").Replace(@"\\?\", "");
                        var vhd = k.GetValue("VhdFileName") as string ?? "ext4.vhdx";
                        int ver = 2; object v = k.GetValue("Version"); if (v is int) ver = (int)v;
                        var d = new Distro { Name = name, BasePath = basePath, Version = ver };
                        d.Managed = IsManagedName(name);
                        if (ver == 2 && basePath.Length > 0) d.VhdPath = Path.Combine(basePath, vhd);
                        list.Add(d);
                    }
                }
            }
            // Docker Desktop keeps its data disk outside the registered folder on newer versions.
            var dd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Docker\wsl\disk\docker_data.vhdx");
            if (File.Exists(dd) && !list.Any(x => string.Equals(x.VhdPath, dd, StringComparison.OrdinalIgnoreCase)))
                list.Add(new Distro { Name = DockerDataName, VhdPath = dd, Version = 2, Managed = true });
            return list;
        }

        public static List<Distro> List()
        {
            var list = Registered();
            Refresh(list);
            return list.OrderBy(x => x.Managed).ThenBy(x => x.Name).ToList();
        }

        public static void Refresh(List<Distro> list)
        {
            bool ok;
            var running = RunningNames(out ok);
            foreach (var d in list)
            {
                d.Running = running.Contains(d.Name);
                if (d.HasDisk)
                {
                    var fi = new FileInfo(d.VhdPath);
                    d.FileSize = fi.Length;
                    d.Sparse = (fi.Attributes & FileAttributes.SparseFile) != 0;
                }
            }
        }

        // The names of the running distros. ok is false when wsl.exe could not be asked (failed or timed out):
        // the returned set is then empty and means "unknown", never "nothing is running".
        public static HashSet<string> RunningNames(out bool ok)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            int rc;
            try { rc = Run("wsl.exe", "--list --running --quiet", null, l => sb.AppendLine(l), 15000); }
            catch { rc = -1; }
            ok = rc == 0;
            if (!ok) return set;
            foreach (var l in sb.ToString().Split('\n'))
            {
                var t = l.Replace("\0", "").Replace("\uFEFF", "").Trim();
                if (t.StartsWith("ERR\t")) continue;
                if (t.Length > 0 && !t.Contains(" ")) set.Add(t);
            }
            return set;
        }

        public static int Run(string exe, string args, string stdin, Action<string> onLine, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            psi.EnvironmentVariables["WSL_UTF8"] = "1";
            using (var p = new Process { StartInfo = psi })
            {
                var gate = new object();
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) lock (gate) onLine("ERR\t" + e.Data.Replace("\0", "")); };
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (gate) onLine(e.Data.Replace("\0", "")); };
                p.Start();
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                if (stdin != null)
                {
                    try
                    {
                        var bytes = new UTF8Encoding(false).GetBytes(stdin.Replace("\r\n", "\n"));
                        p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                        p.StandardInput.Close();
                    }
                    catch (IOException) { /* process ended early; its error output is still read below */ }
                }
                if (!p.WaitForExit(timeoutMs))
                {
                    onLine("ERR\ttimed out after " + (timeoutMs / 1000) + " s");
                    try { using (var k = Process.Start(new ProcessStartInfo("taskkill.exe", "/T /F /PID " + p.Id) { CreateNoWindow = true, UseShellExecute = false })) k.WaitForExit(5000); } catch { }
                    return -1;
                }
                p.WaitForExit();   // flushes the async readers
                return p.ExitCode;
            }
        }

        // The agent script with the user's exact selection in front of it (id<TAB>path lines, base64).
        public static string Agent(IEnumerable<Item> picked)
        {
            var sb = new StringBuilder();
            foreach (var i in picked) foreach (var p in i.Paths) sb.Append(i.Id).Append('\t').Append(p).Append('\n');
            return "WSLIM_SEL='" + Convert.ToBase64String(new UTF8Encoding(false).GetBytes(sb.ToString())) + "'\n" + Agent();
        }

        // agent.sh is embedded gzip-compressed (build.cmd) to keep the executables small.
        public static string Agent()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("agent.sh.gz"))
            using (var z = new GZipStream(s, CompressionMode.Decompress))
            using (var r = new StreamReader(z, Encoding.UTF8)) return r.ReadToEnd().Replace("\r\n", "\n");
        }
    }

    // ------------------------------------------------------------------ logging
    static class Log
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wslim");
        public static readonly string LogDir = Path.Combine(Dir, "logs");
#pragma warning disable 649   // not set by the tests
        public static Action<string> Sink;   // receives "HH:mm:ss  text"
#pragma warning restore 649
        static readonly object Gate = new object();

        public static void Write(string text)
        {
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(LogDir);
                    File.AppendAllText(Path.Combine(LogDir, "wslim-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                        DateTime.Now.ToString("yyyy-MM-dd ") + line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
            var s = Sink; if (s != null) s(line);
        }

        public static string HistoryFile { get { return Path.Combine(Dir, "history.tsv"); } }
        public static void History(string summary)
        {
            try { Directory.CreateDirectory(Dir); File.AppendAllText(HistoryFile, DateTime.Now.ToString("s") + "\t" + summary + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }
        public static string LastHistory()
        {
            try
            {
                if (!File.Exists(HistoryFile)) return null;
                var last = File.ReadAllLines(HistoryFile).Where(l => l.Trim().Length > 0).LastOrDefault();
                if (last == null) return null;
                var parts = last.Split('\t');
                DateTime when;
                if (parts.Length < 2 || !DateTime.TryParse(parts[0], out when)) return null;
                return "Last run " + when.ToString("d MMM yyyy, HH:mm") + " · " + parts[1];
            }
            catch { return null; }
        }

        public static void Crash(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, "crash.log"), DateTime.Now.ToString("s") + Environment.NewLine + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }

    static class Fmt
    {
        public static string Size(long b)
        {
            if (b < 0) return "–";
            if (b >= 1L << 30) return (b / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            if (b >= 1L << 20) return (b / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";
            if (b >= 1L << 10) return (b / (double)(1L << 10)).ToString("0", CultureInfo.InvariantCulture) + " KB";
            return b > 0 ? "< 1 KB" : "0";
        }
    }

    // ------------------------------------------------------------------ scan and clean (shrinking: Compact.cs)
    static class Core
    {
        public static string Version
        {
            get { var v = Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        // Runs the agent's scan in one distro (starting it if needed) and fills d.Items, d.Notes, d.Os, d.Used, d.Scanned.
        public static int ScanDistro(Distro d, string agent)
        {
            var dd = d;
            var items = new List<Item>(); var notes = new List<string>(); long used = -1; string os = null;
            bool spoke = false; var said = new List<string>();   // spoke: the agent answered, so the distro did start
            d.Error = null;
            int rc;
            try { rc = Wsl.Run("wsl.exe", "-d " + d.Name + " -u root -- sh -s scan", agent, line =>
            {
                var f = line.Split('\t');
                if (f[0] == "ITEM" || f[0] == "INFO" || f[0] == "NOTE") spoke = true;
                if (f[0] == "ITEM" && f.Length >= 7)
                {
                    long b; long.TryParse(f[3], out b);
                    items.Add(new Item { Id = f[1], Tier = f[2], Bytes = b, Title = f[4], Why = f[5], Paths = f[6].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries), Distro = dd });
                }
                else if (f[0] == "INFO" && f.Length >= 3) { if (f[1] == "used") long.TryParse(f[2], out used); else if (f[1] == "os") os = f[2]; }
                else if (f[0] == "NOTE" && f.Length >= 2) notes.Add(f[1]);
                else if (f[0] == "ERR") { lock (said) said.Add(line.Substring(4).Trim()); Log.Write("  " + dd.Name + ": " + line.Substring(4)); }
                else if (line.Trim().Length > 0) { lock (said) said.Add(line.Trim()); Log.Write("  " + dd.Name + ": " + line.Trim()); }
            }, 600000); }
            catch (Exception ex) { rc = -1; said.Add(ex.Message); }
            items = items.Where(i => i.Bytes >= (1L << 20) || i.Id.StartsWith("docker-")).ToList();   // hide crumbs under 1 MB
            d.Items = items.OrderBy(i => i.Tier == "safe" ? 0 : 1).ThenByDescending(i => i.Bytes).ToList();
            d.Notes = notes; d.Os = os; d.Scanned = rc == 0; if (used >= 0) d.Used = used;
            if (rc == 0)
                Log.Write(string.Format("{0}: {1} items, {2} safe, {3} to review", d.Name, items.Count,
                    Fmt.Size(items.Where(i => i.Tier == "safe").Sum(i => i.Bytes)), Fmt.Size(items.Where(i => i.Tier != "safe").Sum(i => i.Bytes))));
            else ReportDistroFailure(d, "scan", rc, spoke, said);
            return rc;
        }

        // Cleans (or previews) the picked items of one distro. onFreed receives the bytes freed inside the distro.
        // A distro that never answered did not start: say so, with what WSL said, and how to look at its disk.
        static void ReportDistroFailure(Distro d, string what, int rc, bool spoke, List<string> said)
        {
            string detail;
            lock (said) detail = string.Join(" ", said.Where(x => x.Length > 0).Take(4));
            if (detail.Length > 400) detail = detail.Substring(0, 400) + "…";
            if (!spoke)
            {
                d.Error = "could not start " + d.Name + ": " + (detail.Length > 0 ? detail : "wsl.exe exit code " + rc);
                Log.Write(d.Error);
                if (d.VhdPath != null)
                    Log.Write("  To look at its disk without changing anything, run in an administrator PowerShell: " + Compact.CheckCommand(d.VhdPath));
            }
            else
            {
                d.Error = what + " failed in " + d.Name + " (exit code " + rc + ")" + (detail.Length > 0 ? ": " + detail : "");
                Log.Write(d.Error);
            }
        }

        public static int CleanDistro(Distro d, List<Item> picked, bool preview, Action<long> onFreed)
        {
            Log.Write("— " + d.Name + ": " + string.Join(", ", picked.Select(i => i.Title)));
            var ids = string.Join(" ", picked.Select(i => i.Id));
            var agent = Wsl.Agent(picked);
            bool spoke = false; var said = new List<string>();
            d.Error = null;
            int rc;
            try { rc = Wsl.Run("wsl.exe", "-d " + d.Name + " -u root -- sh -s " + (preview ? "preview " : "clean ") + ids, agent, line =>
            {
                var f = line.Split('\t');
                if (f[0] == "LOG" || f[0] == "INFO") spoke = true;
                if (f[0] == "LOG" && f.Length >= 2) Log.Write(f[1]);
                else if (f[0] == "INFO" && f.Length >= 3 && f[1] == "freed") { long b; if (long.TryParse(f[2], out b) && onFreed != null) onFreed(Math.Max(0, b)); }
                else if (f[0] == "INFO" && f.Length >= 3 && f[1] == "used") { long b; if (long.TryParse(f[2], out b)) d.Used = b; }
                else if (f[0] == "ERR") { lock (said) said.Add(line.Substring(4).Trim()); Log.Write("  " + line.Substring(4)); }
                else if (line.Trim().Length > 0) { lock (said) said.Add(line.Trim()); Log.Write("  " + line.Trim()); }
            }, 3600000); }
            catch (Exception ex) { rc = -1; said.Add(ex.Message); }
            if (rc != 0) ReportDistroFailure(d, preview ? "preview" : "cleanup", rc, spoke, said);
            return rc;
        }

        public static bool DockerDesktopRunning()
        {
            try { return Process.GetProcessesByName("Docker Desktop").Length > 0; }
            catch { return false; }
        }

        // Disks that a shrink would compact: real (non-sparse) disks; managed ones only when Docker Desktop is closed.
        public static List<Distro> ShrinkTargets(IEnumerable<Distro> list, bool dockerDesktop)
        {
            var disks = list.Where(d => d.HasDisk && !d.Sparse).ToList();
            if (dockerDesktop) disks = disks.Where(d => !d.Managed).ToList();
            return disks;
        }

        public static void LogSparseSkipped(IEnumerable<Distro> list)
        {
            foreach (var d in list.Where(x => x.Sparse && x.HasDisk))
                Log.Write(d.Name + ": disk is a sparse file, which Windows shrinks by itself; skipped.");
        }
    }
}
