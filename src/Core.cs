// wslim core: everything that is not UI. Shared by wslim.exe (App.cs) and wslim-cli.exe (Cli.cs).
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
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]

namespace Wslim
{
    // ------------------------------------------------------------------ model
    class Distro
    {
        public string Name, BasePath, VhdPath, Os;
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
        public static List<Distro> List()
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
                        d.Managed = name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)
                                 || name.StartsWith("rancher-desktop", StringComparison.OrdinalIgnoreCase)
                                 || name.StartsWith("podman-machine", StringComparison.OrdinalIgnoreCase);
                        if (ver == 2 && basePath.Length > 0) d.VhdPath = Path.Combine(basePath, vhd);
                        list.Add(d);
                    }
                }
            }
            // Docker Desktop keeps its data disk outside the registered folder on newer versions.
            var dd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Docker\wsl\disk\docker_data.vhdx");
            if (File.Exists(dd) && !list.Any(x => string.Equals(x.VhdPath, dd, StringComparison.OrdinalIgnoreCase)))
                list.Add(new Distro { Name = "Docker Desktop data", VhdPath = dd, Version = 2, Managed = true });
            Refresh(list);
            return list.OrderBy(x => x.Managed).ThenBy(x => x.Name).ToList();
        }

        public static void Refresh(List<Distro> list)
        {
            var running = RunningNames();
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

        public static HashSet<string> RunningNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            Run("wsl.exe", "--list --running --quiet", null, l => sb.AppendLine(l), 15000);
            foreach (var l in sb.ToString().Split('\n'))
            {
                var t = l.Replace("\0", "").Replace("\uFEFF", "").Trim();
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
        public static Action<string> Sink;   // receives "HH:mm:ss  text"
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

    // ------------------------------------------------------------------ scan, clean, shrink
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
            int rc;
            try { rc = Wsl.Run("wsl.exe", "-d " + d.Name + " -u root -- sh -s scan", agent, line =>
            {
                var f = line.Split('\t');
                if (f[0] == "ITEM" && f.Length >= 7)
                {
                    long b; long.TryParse(f[3], out b);
                    items.Add(new Item { Id = f[1], Tier = f[2], Bytes = b, Title = f[4], Why = f[5], Paths = f[6].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries), Distro = dd });
                }
                else if (f[0] == "INFO" && f.Length >= 3) { if (f[1] == "used") long.TryParse(f[2], out used); else if (f[1] == "os") os = f[2]; }
                else if (f[0] == "NOTE" && f.Length >= 2) notes.Add(f[1]);
                else if (f[0] == "ERR") Log.Write("  " + dd.Name + ": " + line.Substring(4));
                else if (line.Trim().Length > 0) Log.Write("  " + dd.Name + ": " + line.Trim());
            }, 600000); }
            catch (Exception ex) { rc = -1; Log.Write(dd.Name + ": scan failed: " + ex.Message); }
            items = items.Where(i => i.Bytes >= (1L << 20) || i.Id.StartsWith("docker-")).ToList();   // hide crumbs under 1 MB
            d.Items = items.OrderBy(i => i.Tier == "safe" ? 0 : 1).ThenByDescending(i => i.Bytes).ToList();
            d.Notes = notes; d.Os = os; d.Scanned = rc == 0; if (used >= 0) d.Used = used;
            Log.Write(string.Format("{0}: {1} items, {2} safe, {3} to review", d.Name, items.Count,
                Fmt.Size(items.Where(i => i.Tier == "safe").Sum(i => i.Bytes)), Fmt.Size(items.Where(i => i.Tier != "safe").Sum(i => i.Bytes))));
            if (rc != 0) Log.Write(d.Name + ": scan exited with code " + rc);
            return rc;
        }

        // Cleans (or previews) the picked items of one distro. onFreed receives the bytes freed inside the distro.
        public static int CleanDistro(Distro d, List<Item> picked, bool preview, Action<long> onFreed)
        {
            Log.Write("— " + d.Name + ": " + string.Join(", ", picked.Select(i => i.Title)));
            var ids = string.Join(" ", picked.Select(i => i.Id));
            var agent = Wsl.Agent(picked);
            int rc;
            try { rc = Wsl.Run("wsl.exe", "-d " + d.Name + " -u root -- sh -s " + (preview ? "preview " : "clean ") + ids, agent, line =>
            {
                var f = line.Split('\t');
                if (f[0] == "LOG" && f.Length >= 2) Log.Write(f[1]);
                else if (f[0] == "INFO" && f.Length >= 3 && f[1] == "freed") { long b; if (long.TryParse(f[2], out b) && onFreed != null) onFreed(Math.Max(0, b)); }
                else if (f[0] == "INFO" && f.Length >= 3 && f[1] == "used") { long b; if (long.TryParse(f[2], out b)) d.Used = b; }
                else if (f[0] == "ERR") Log.Write("  " + line.Substring(4));
                else if (line.Trim().Length > 0) Log.Write("  " + line.Trim());
            }, 3600000); }
            catch (Exception ex) { rc = -1; Log.Write(d.Name + ": " + ex.Message); }
            if (rc != 0) Log.Write(d.Name + ": cleanup exited with code " + rc);
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);
        // diskpart reads its script in the OEM code page; an 8.3 path avoids trouble with non-ASCII user names.
        static string ShortPath(string path)
        {
            try
            {
                var sb = new StringBuilder(1024);
                if (!File.Exists(path)) File.WriteAllText(path, "");
                uint r = GetShortPathName(path, sb, (uint)sb.Capacity);
                var sp = r > 0 && r < sb.Capacity ? sb.ToString() : path;
                return sp.All(ch => ch < 128) ? sp : path;
            }
            catch { return path; }
        }

        // Shuts WSL down and runs diskpart elevated (one UAC prompt for all disks).
        public static bool Shrink(List<Distro> disks)
        {
            Log.Write("Shutting WSL down…");
            Wsl.Run("wsl.exe", "--shutdown", null, l => { }, 60000);
            for (int i = 0; i < 30 && Wsl.RunningNames().Count > 0; i++) Thread.Sleep(1000);
            Thread.Sleep(3000);

            var tmp = Path.Combine(Path.GetTempPath(), "wslim");
            Directory.CreateDirectory(tmp);
            var output = Path.Combine(tmp, "shrink-out.txt");
            var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            File.WriteAllText(output, "", oem);
            var chain = new List<string>();
            int n = 0;
            foreach (var d in disks)
            {
                var file = Path.Combine(tmp, "shrink-" + (n++) + ".txt");
                var vhd = ShortPath(d.VhdPath);
                File.WriteAllText(file, "select vdisk file=\"" + vhd + "\"\r\nattach vdisk readonly\r\ncompact vdisk noerr\r\ndetach vdisk noerr\r\nexit\r\n", oem);
                chain.Add("diskpart /s \"" + ShortPath(file) + "\" >> \"" + ShortPath(output) + "\" 2>&1");
            }
            var script = string.Join(" & ", chain);

            foreach (var d in disks) Log.Write(string.Format("Shrinking {0} ({1})…", d.Name, Fmt.Size(d.FileSize)));
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + script)
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (File.Exists(output))
                        foreach (var l in File.ReadAllLines(output, oem))
                            if (l.Trim().Length > 0 && !l.StartsWith("Microsoft DiskPart") && !l.StartsWith("Copyright") && !l.StartsWith("On computer"))
                                Log.Write("    " + l.Trim());
                    if (p.ExitCode != 0) { Log.Write("diskpart reported an error (code " + p.ExitCode + "); see the lines above."); return false; }
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) { Log.Write("Shrinking cancelled: administrator permission was not given."); return false; }
                Log.Write("Could not start diskpart: " + ex.Message); return false;
            }
            foreach (var d in disks)
            {
                long old = d.FileSize; long now = new FileInfo(d.VhdPath).Length;
                Log.Write(string.Format("{0}: {1} → {2}", d.Name, Fmt.Size(old), Fmt.Size(now)));
            }
            return true;
        }

        public static void LogSparseSkipped(IEnumerable<Distro> list)
        {
            foreach (var d in list.Where(x => x.Sparse && x.HasDisk))
                Log.Write(d.Name + ": disk is a sparse file, which Windows shrinks by itself; skipped.");
        }

        // "disks 40 GB → 12 GB, freed 28 GB on C:" or "freed 3 GB inside WSL", as shown and written to history.tsv.
        public static string Summary(bool shrink, Dictionary<Distro, long> before, List<Distro> after, long freedInside)
        {
            long b = before.Where(kv => kv.Value > 0).Sum(kv => kv.Value);
            long a = after.Where(d => before.ContainsKey(d) && before[d] > 0).Sum(d => d.FileSize);
            return shrink
                ? string.Format("disks {0} → {1}, freed {2} on C:", Fmt.Size(b), Fmt.Size(a), Fmt.Size(Math.Max(0, b - a)))
                : string.Format("freed {0} inside WSL", Fmt.Size(freedInside));
        }
    }
}
