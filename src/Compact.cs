// wslim compaction: everything that shrinks a virtual disk, and the reporting around it.
// Shared by wslim.exe, wslim-cli.exe and the tests. No WPF here. .NET Framework 4.8, C# 5.
//
// Design (WSLIM_COMPACTION_INCIDENT_REPORT.md, section 6):
//   - The disk is not attached to Windows at all in the default mode, so nothing can be left attached.
//   - The app (not elevated) stops WSL and checks that it stopped, writes a journal, then starts ONE elevated
//     worker (wslim-cli.exe --compact-worker <job>). The worker checks each disk, runs DiskPart, checks each
//     disk again and writes a result file. The app reads the result and starts WSL again only if the worker
//     verified every disk.
//   - Everything that touches the machine goes through IHost, so the logic is tested with a fake host.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Wslim
{
    // ------------------------------------------------------------------ model
    enum Outcome { Success, NoSpaceReclaimed, PartialSuccess, Failed, Cancelled, RecoveryRequired }

    // Detached: select + compact, the disk is never attached. AttachReadOnly: the fallback (report 6.5).
    enum CompactMode { Detached, AttachReadOnly }

    enum WorkerLaunch { Exited, UacDeclined, StartFailed, TimedOut }

    class DiskSpec
    {
        public string Name, Path;
        public long Size;
    }

    class DiskResult
    {
        public string Name, Path, Message;
        public Outcome Outcome = Outcome.Failed;
        public bool Skipped;            // not a target after all (sparse); not counted in the overall outcome
        public bool Checked;            // the final disk state below was read after the last disk command
        public bool? Attached;          // attached to Windows at the end: true, false, or null = could not be read
        public bool Exclusive;          // could be opened with FileShare.None at the end
        public long Before = -1, After = -1;
    }

    class CompactResult
    {
        public Outcome Outcome = Outcome.Failed;
        public List<DiskResult> Disks = new List<DiskResult>();
        public string Message;          // one sentence for a failure that is not about one disk
        public bool WslStopped;         // wsl --shutdown ran and returned 0
        public bool WslRestarted;
        public string WslMessage;       // "WSL is running again", or why it is not
        public List<string> RecoveryPaths = new List<string>();
    }

    // Everything the compaction does to the machine. RealHost does it; the tests use a fake.
    interface IHost
    {
        string DataDir { get; }                      // %LOCALAPPDATA%\wslim: journal, job and result files
        void Write(string text);                     // log line
        void History(string text);                   // history.tsv line
        void Sleep(int ms);
        int Run(string exe, string args, int timeoutMs, out string output);
        // Runs one DiskPart script in its own DiskPart process. Returns the exit code, or Compact.TimedOut when
        // it was killed after the timeout and has exited, or Compact.StillRunning when it could not be stopped.
        int RunDiskpart(string script, int timeoutMs, out string output);
        int Dismount(string path, out string output);    // Dismount-DiskImage; only ever used on a disk wslim attached
        bool? IsAttached(string path);               // attached to Windows? null = could not be read
        bool CanOpenExclusive(string path);          // FileShare.None probe; opens and closes, changes nothing
        bool FileExists(string path);
        long FileLength(string path);
        bool IsReparsePoint(string path);
        bool IsSparse(string path);
        string ScriptPath(string path, out string error);   // the spelling of the path DiskPart can read, or null
        HashSet<string> RunningDistros(out bool ok);
        string RegisteredVhdPath(string distroName); // from the registry right now, or null
        List<string> AllDiskPaths();
        bool IsElevated();
        string WorkerExe();                          // full path of wslim-cli.exe next to this program, or null
        WorkerLaunch RunWorker(string exe, string jobFile, int timeoutMs, out int exitCode, out string error);
        IDisposable TryLock(string name);            // null when another process holds it
    }

    // ------------------------------------------------------------------ tiny JSON (journal, job and result files)
    static class Json
    {
        public static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (var ch in s)
            {
                if (ch == '"') sb.Append("\\\"");
                else if (ch == '\\') sb.Append("\\\\");
                else if (ch == '\n') sb.Append("\\n");
                else if (ch == '\r') sb.Append("\\r");
                else if (ch == '\t') sb.Append("\\t");
                else if (ch < 0x20 || ch > 0x7e) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }
        public static string B(bool b) { return b ? "true" : "false"; }
        public static string N(long n) { return n.ToString(CultureInfo.InvariantCulture); }

        // Objects become Dictionary<string, object>, arrays List<object>, numbers long or double. Throws FormatException.
        public static object Parse(string text)
        {
            int i = 0;
            var v = Value(text, ref i);
            Ws(text, ref i);
            if (i != text.Length) throw new FormatException("unexpected text after the JSON value");
            return v;
        }
        static void Ws(string s, ref int i) { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n' || s[i] == '﻿')) i++; }
        static bool Lit(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length; return true;
        }
        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{')
            {
                i++;
                var d = new Dictionary<string, object>();
                Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException("expected a name");
                    var k = Str(s, ref i);
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':'");
                    i++;
                    d[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new FormatException("expected ',' or '}'");
                }
            }
            if (c == '[')
            {
                i++;
                var l = new List<object>();
                Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw new FormatException("expected ',' or ']'");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Lit(s, ref i, "true")) return true;
            if (Lit(s, ref i, "false")) return false;
            if (Lit(s, ref i, "null")) return null;
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
            if (i == start) throw new FormatException("unexpected character '" + c + "'");
            var num = s.Substring(start, i - start);
            long n;
            if (long.TryParse(num, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) return n;
            return double.Parse(num, CultureInfo.InvariantCulture);
        }
        static string Str(string s, ref int i)
        {
            i++;   // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("unterminated string");
                char e = s[i++];
                if (e == 'n') sb.Append('\n');
                else if (e == 'r') sb.Append('\r');
                else if (e == 't') sb.Append('\t');
                else if (e == 'b') sb.Append('\b');
                else if (e == 'f') sb.Append('\f');
                else if (e == 'u')
                {
                    if (i + 4 > s.Length) throw new FormatException("bad \\u escape");
                    sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                }
                else sb.Append(e);
            }
        }

        public static Dictionary<string, object> Obj(object o)
        {
            var d = o as Dictionary<string, object>;
            if (d == null) throw new FormatException("expected an object");
            return d;
        }
        public static string S(Dictionary<string, object> d, string key)
        {
            object v; return d.TryGetValue(key, out v) ? v as string : null;
        }
        public static long L(Dictionary<string, object> d, string key, long fallback)
        {
            object v;
            if (!d.TryGetValue(key, out v)) return fallback;
            if (v is long) return (long)v;
            if (v is double) return (long)(double)v;
            return fallback;
        }
        public static bool Bool(Dictionary<string, object> d, string key)
        {
            object v; return d.TryGetValue(key, out v) && v is bool && (bool)v;
        }
        public static List<object> A(Dictionary<string, object> d, string key)
        {
            object v; d.TryGetValue(key, out v);
            return v as List<object> ?? new List<object>();
        }

        // Writes the whole file or nothing: a half-written journal must never be read as "no journal".
        public static void WriteFile(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".new";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }

    // ------------------------------------------------------------------ journal
    class JournalDisk
    {
        public string Name, Path, Phase = "pending";
        public long Size;
        public bool AttachedByWslim;   // true from just before wslim attaches a disk until wslim has verified it detached
    }

    // operation.json: exists from just before the elevated worker starts until every disk is verified safe.
    // If it exists when wslim starts, the previous run did not end properly and everything is blocked.
    class Journal
    {
        public const string FileName = "operation.json";
        public string Id, Phase, Mode, Started;
        public int Pid;
        public bool Corrupt;            // the file exists but could not be read
        public List<string> Running = new List<string>();
        public List<JournalDisk> Disks = new List<JournalDisk>();

        public static string FileIn(string dir) { return Path.Combine(dir, FileName); }

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"wslim\": \"operation\",\n  \"version\": 1,\n  \"id\": ").Append(Json.Q(Id))
              .Append(",\n  \"pid\": ").Append(Json.N(Pid))
              .Append(",\n  \"started\": ").Append(Json.Q(Started))
              .Append(",\n  \"phase\": ").Append(Json.Q(Phase))
              .Append(",\n  \"mode\": ").Append(Json.Q(Mode))
              .Append(",\n  \"running\": [").Append(string.Join(", ", Running.Select(Json.Q))).Append("]")
              .Append(",\n  \"disks\": [");
            for (int i = 0; i < Disks.Count; i++)
            {
                var d = Disks[i];
                sb.Append(i == 0 ? "\n" : ",\n")
                  .Append("    {\"name\": ").Append(Json.Q(d.Name))
                  .Append(", \"path\": ").Append(Json.Q(d.Path))
                  .Append(", \"size\": ").Append(Json.N(d.Size))
                  .Append(", \"attachedByWslim\": ").Append(Json.B(d.AttachedByWslim))
                  .Append(", \"phase\": ").Append(Json.Q(d.Phase)).Append("}");
            }
            sb.Append(Disks.Count > 0 ? "\n  ]\n}\n" : "]\n}\n");
            return sb.ToString();
        }

        public void Save(string dir) { Json.WriteFile(FileIn(dir), ToJson()); }

        // null when there is no journal. A journal that cannot be read is still a journal (Corrupt).
        public static Journal Load(string dir)
        {
            var file = FileIn(dir);
            if (!File.Exists(file)) return null;
            var j = new Journal();
            try
            {
                var o = Json.Obj(Json.Parse(File.ReadAllText(file, Encoding.UTF8)));
                j.Id = Json.S(o, "id"); j.Phase = Json.S(o, "phase"); j.Mode = Json.S(o, "mode"); j.Started = Json.S(o, "started");
                j.Pid = (int)Json.L(o, "pid", 0);
                foreach (var r in Json.A(o, "running")) if (r is string) j.Running.Add((string)r);
                foreach (var x in Json.A(o, "disks"))
                {
                    var d = Json.Obj(x);
                    var jd = new JournalDisk { Name = Json.S(d, "name"), Path = Json.S(d, "path"), Size = Json.L(d, "size", -1), AttachedByWslim = Json.Bool(d, "attachedByWslim") };
                    jd.Phase = Json.S(d, "phase") ?? "pending";
                    if (jd.Path == null) throw new FormatException("a disk without a path");
                    j.Disks.Add(jd);
                }
            }
            catch (Exception) { j.Corrupt = true; j.Disks.Clear(); }
            return j;
        }

        public static void Clear(string dir)
        {
            var file = FileIn(dir);
            if (File.Exists(file)) File.Delete(file);
        }

        // Used at every start of the window and the console program.
        public static Journal Pending() { return Load(Log.Dir); }
    }

    // ------------------------------------------------------------------ compaction
    static class Compact
    {
        // One place decides how disks are compacted. Detached never attaches the disk. Whether it reclaims as
        // much as AttachReadOnly on a trimmed ext4 disk is measured by the CI integration job (report, Q1).
        public const CompactMode DefaultMode = CompactMode.Detached;

        public const int TimedOut = -900001;       // RunDiskpart: killed after the timeout, and it has exited
        public const int StillRunning = -900002;   // RunDiskpart: timed out and could not be stopped
        public const int StartError = -900003;     // the program could not be started
        public const int ScriptPauseMs = 15000;    // Microsoft: "allow at least 15 seconds between each script"
        public const string LockName = "wslim-compact";
        public const string WorkerLockName = "wslim-compact-worker";
        public const string JobFileName = "compact-job.json";
        public const string ResultFileName = "compact-result.json";
        public const int WorkerOk = 0;             // worker exit code: a result file was written
        public const int WorkerRefused = 10;       // worker exit code: refused before looking at any disk

        // WSLIM_COMPACT_MODE=attach (or =detached) overrides DefaultMode, for testing.
        public static CompactMode CurrentMode()
        {
            string v = null;
            try { v = Environment.GetEnvironmentVariable("WSLIM_COMPACT_MODE"); } catch { }
            if (v != null)
            {
                v = v.Trim().ToLowerInvariant();
                if (v == "attach" || v == "attachreadonly") return CompactMode.AttachReadOnly;
                if (v == "detached") return CompactMode.Detached;
            }
            return DefaultMode;
        }

        // A path may go into a DiskPart script only if it cannot end the quoted string or the line.
        public static string ScriptSafetyError(string path)
        {
            if (string.IsNullOrEmpty(path)) return "the disk path is empty";
            foreach (var ch in path)
                if (ch < 0x20 || ch == '"' || ch == 0x7f) return "the disk path contains a character that is not allowed (" + ((int)ch).ToString("x2") + ")";
            if (path.Length < 4 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\') return "the disk path is not on a local drive: " + path;
            return null;
        }

        // The DiskPart scripts for one disk; each element is run as its own DiskPart process.
        //   Detached:       one script, exactly two lines: select vdisk, compact vdisk.
        //   AttachReadOnly: three scripts: attach / compact / detach. Detach never depends on an earlier line.
        // "compact vdisk" takes no parameters. In particular it never gets "noerr" (that was the incident).
        public static string[] BuildCompactScript(string path, CompactMode mode)
        {
            var err = ScriptSafetyError(path);
            if (err != null) throw new ArgumentException(err);
            var select = "select vdisk file=\"" + path + "\"\r\n";
            if (mode == CompactMode.Detached) return new[] { select + "compact vdisk\r\n" };
            return new[] { select + "attach vdisk readonly\r\n", select + "compact vdisk\r\n", select + "detach vdisk\r\n" };
        }

        // 10 minutes plus 1 minute per GB, at least 15 minutes, at most 24 hours.
        public static int CompactTimeoutMs(long bytes)
        {
            long gb = bytes <= 0 ? 0 : (bytes + (1L << 30) - 1) / (1L << 30);
            long minutes = Math.Max(15, 10 + gb);
            return (int)(Math.Min(minutes, 24 * 60) * 60000);
        }

        public static int WorkerTimeoutMs(IEnumerable<DiskSpec> disks)
        {
            long ms = 5 * 60000;
            foreach (var d in disks) ms += (long)CompactTimeoutMs(d.Size) + 4 * 60000;
            return (int)Math.Min(ms, int.MaxValue - 1);
        }

        public static string CheckCommand(string path) { return "Get-DiskImage -ImagePath \"" + path + "\" | Format-List Attached,DevicePath"; }
        public static string DismountCommand(string path) { return "Dismount-DiskImage -ImagePath \"" + path + "\""; }

        // The exact commands for the user, one per element, ready to copy.
        public static List<string> RecoveryCommands(IEnumerable<string> paths)
        {
            var l = new List<string>();
            l.Add("wsl --shutdown");
            foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase)) { l.Add(CheckCommand(p)); l.Add(DismountCommand(p)); }
            return l;
        }

        // The whole recovery text, as shown by the window and the console program.
        public static List<string> RecoveryLines(IEnumerable<string> paths, bool attachedByWslim)
        {
            var list = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
            var l = new List<string>();
            l.Add("Recovery required: a disk may still be attached to Windows, or its state could not be verified.");
            l.Add("wslim will not scan, clean or shrink until this is resolved. Do not start WSL yet.");
            if (attachedByWslim) l.Add("wslim attached this disk itself (fallback mode) and could not verify that it was detached again.");
            l.Add("Run these in PowerShell as administrator:");
            if (list.Count == 0)
            {
                l.Add("  wsl --shutdown");
                l.Add("  Get-DiskImage -ImagePath \"<path of your ext4.vhdx>\" | Format-List Attached,DevicePath");
                l.Add("  Dismount-DiskImage -ImagePath \"<path of your ext4.vhdx>\"");
            }
            else foreach (var c in RecoveryCommands(list)) l.Add("  " + c);
            l.Add("Run Dismount-DiskImage only for a disk that shows Attached : True.");
            l.Add("Then let wslim check again: the \"Check disk state\" button in the window, or: wslim-cli recover");
            return l;
        }

        public static bool SamePath(string a, string b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.Trim().TrimEnd('\\'), b.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        // Is this still the disk that was scanned? null = yes. sparse is set when the only objection is that
        // the file is sparse (skipped with an explanation, not a failure).
        public static string IdentityError(IHost host, DiskSpec d, out bool sparse)
        {
            sparse = false;
            var e = ScriptSafetyError(d.Path);
            if (e != null) return e;
            if (!d.Path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase)) return "not a .vhdx file: " + d.Path;
            string full;
            try { full = Path.GetFullPath(d.Path); } catch (Exception ex) { return "not a valid path (" + ex.Message + "): " + d.Path; }
            if (!SamePath(full, d.Path)) return "not a plain full path: " + d.Path;
            var reg = host.RegisteredVhdPath(d.Name);
            if (reg == null) return "no disk is registered for " + d.Name + " any more";
            if (!SamePath(reg, d.Path)) return "the disk path changed since the scan (was " + d.Path + ", is now " + reg + ")";
            if (!host.FileExists(d.Path)) return "the disk file does not exist: " + d.Path;
            if (host.IsReparsePoint(d.Path)) return "the disk file is a link (reparse point), not a regular file: " + d.Path;
            if (host.IsSparse(d.Path)) { sparse = true; return "disk is a sparse file, which Windows shrinks by itself; skipped"; }
            return null;
        }

        public static Outcome Aggregate(List<DiskResult> disks)
        {
            var real = disks.Where(d => !d.Skipped).ToList();
            if (real.Count == 0) return Outcome.NoSpaceReclaimed;
            if (real.Any(d => d.Outcome == Outcome.RecoveryRequired)) return Outcome.RecoveryRequired;
            if (real.All(d => d.Outcome == Outcome.Cancelled)) return Outcome.Cancelled;
            int ok = real.Count(d => d.Outcome == Outcome.Success || d.Outcome == Outcome.NoSpaceReclaimed);
            if (ok == real.Count) return real.Any(d => d.Outcome == Outcome.Success) ? Outcome.Success : Outcome.NoSpaceReclaimed;
            return ok == 0 ? Outcome.Failed : Outcome.PartialSuccess;
        }

        static void LogOutput(IHost host, string output)
        {
            if (output == null) return;
            foreach (var raw in output.Split('\n'))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l.StartsWith("Microsoft DiskPart") || l.StartsWith("Copyright") || l.StartsWith("On computer")) continue;
                host.Write("    " + l);
            }
        }

        static string RcText(int rc)
        {
            if (rc == TimedOut) return "it took too long and was stopped";
            if (rc == StillRunning) return "it took too long and could not be stopped";
            if (rc == StartError) return "it could not be started";
            return "exit code " + rc + " (0x" + rc.ToString("X8") + ")";
        }

        // ---------------------------------------------------------------- the app side (not elevated)
        // Stops WSL, starts the elevated worker, reads its result, and starts WSL again when that is safe.
        public static CompactResult Run(IHost host, List<DiskSpec> disks, CompactMode mode)
        {
            var res = new CompactResult();
            foreach (var d in disks) res.Disks.Add(new DiskResult { Name = d.Name, Path = d.Path, Before = d.Size });

            // An unfinished earlier operation blocks everything.
            var pending = Journal.Load(host.DataDir);
            if (pending != null)
            {
                foreach (var r in res.Disks) r.Outcome = Outcome.RecoveryRequired;
                res.Message = "an earlier shrink did not end properly; nothing was started";
                res.RecoveryPaths = pending.Disks.Select(x => x.Path).ToList();
                if (res.RecoveryPaths.Count == 0) res.RecoveryPaths = disks.Select(x => x.Path).ToList();
                res.Outcome = Outcome.RecoveryRequired;
                host.Write("Recovery required: " + res.Message + ".");
                return res;
            }

            using (var gate = host.TryLock(LockName))
            {
                if (gate == null)
                {
                    res.Message = "another wslim is shrinking disks right now, so this request was refused; nothing was changed";
                    res.Outcome = Outcome.Failed;
                    host.Write("Refused: " + res.Message + ".");
                    return res;
                }
                try { RunLocked(host, disks, mode, res); }
                catch (Exception ex)
                {
                    // Unknown state: say so. If a journal was written it is still there and blocks the next start.
                    host.Write("Unexpected error while shrinking: " + ex.Message);
                    bool journal = Journal.Load(host.DataDir) != null;
                    foreach (var r in res.Disks) if (!r.Skipped && !r.Checked) { r.Outcome = journal ? Outcome.RecoveryRequired : Outcome.Failed; }
                    res.Message = "unexpected error: " + ex.Message;
                    if (journal) res.RecoveryPaths = disks.Select(x => x.Path).ToList();
                    res.Outcome = journal ? Outcome.RecoveryRequired : Aggregate(res.Disks);
                    if (res.Outcome == Outcome.Success || res.Outcome == Outcome.NoSpaceReclaimed) res.Outcome = Outcome.Failed;
                }
            }
            return res;
        }

        static void FailAll(CompactResult res, List<DiskSpec> todo, Outcome o, string message)
        {
            foreach (var d in todo) res.Disks.First(r => r.Path == d.Path && r.Name == d.Name).Outcome = o;
            res.Message = message;
            res.Outcome = Aggregate(res.Disks);
        }

        static void RunLocked(IHost host, List<DiskSpec> disks, CompactMode mode, CompactResult res)
        {
            // 1. Identity of every disk, before anything is stopped.
            var todo = new List<DiskSpec>();
            for (int i = 0; i < disks.Count; i++)
            {
                var d = disks[i]; var r = res.Disks[i];
                bool sparse;
                var err = IdentityError(host, d, out sparse);
                if (err == null) { string se; if (host.ScriptPath(d.Path, out se) == null) err = se ?? "DiskPart cannot read this path"; }
                if (err == null) { todo.Add(d); continue; }
                if (sparse) { r.Skipped = true; r.Outcome = Outcome.NoSpaceReclaimed; r.Message = err; host.Write(d.Name + ": " + err + "."); }
                else { r.Outcome = Outcome.Failed; r.Message = err + "; not touched"; host.Write(d.Name + ": failed before anything was changed: " + err + "."); }
            }
            if (todo.Count == 0) { res.Outcome = Aggregate(res.Disks); return; }

            // 2. The worker program must be there before WSL is stopped for it.
            var exe = host.WorkerExe();
            if (exe == null)
            {
                FailAll(res, todo, Outcome.Failed, "wslim-cli.exe was not found next to wslim.exe, and shrinking needs it; nothing was changed (WSL was not stopped)");
                host.Write("Failed: " + res.Message + ".");
                return;
            }

            // 3. Stop WSL and prove that it stopped. "Could not ask" is unknown, and unknown stops here.
            bool ok;
            var running = host.RunningDistros(out ok);
            if (!ok)
            {
                FailAll(res, todo, Outcome.Failed, "could not ask WSL which distros are running; WSL was not stopped and no disk was touched");
                host.Write("Failed: " + res.Message + ".");
                return;
            }
            host.Write("Shutting WSL down (wsl --shutdown)" + (running.Count > 0 ? "; running: " + string.Join(", ", running) : "") + "…");
            string output;
            int rc = host.Run("wsl.exe", "--shutdown", 60000, out output);
            if (rc != 0)
            {
                FailAll(res, todo, Outcome.Failed, "wsl --shutdown failed (" + RcText(rc) + ")" + Tail(output) + "; no disk was touched");
                host.Write("Failed: " + res.Message + ".");
                return;
            }
            res.WslStopped = true;
            bool stopped = false; HashSet<string> still = null;
            for (int i = 0; i < 30; i++)
            {
                still = host.RunningDistros(out ok);
                if (!ok) break;
                if (still.Count == 0) { stopped = true; break; }
                host.Sleep(1000);
            }
            if (!stopped)
            {
                FailAll(res, todo, Outcome.Failed, (!ok ? "could not confirm that WSL stopped (asking WSL failed)" : "WSL did not stop; still running: " + string.Join(", ", still)) + "; no disk was touched");
                host.Write("Failed: " + res.Message + ".");
                res.WslMessage = "wsl --shutdown was already run; wslim did not start anything again";
                return;
            }
            // Give the WSL virtual machine a moment to let go of the files (bounded; the worker decides, not this loop).
            for (int i = 0; i < 10 && !todo.All(d => host.CanOpenExclusive(d.Path)); i++) host.Sleep(1000);

            // 4. Journal first, then the elevated worker.
            var journal = new Journal { Id = Guid.NewGuid().ToString("N"), Pid = Process.GetCurrentProcess().Id, Started = DateTime.Now.ToString("s"), Phase = "worker-starting", Mode = mode.ToString(), Running = running.ToList() };
            foreach (var d in todo) journal.Disks.Add(new JournalDisk { Name = d.Name, Path = d.Path, Size = d.Size });
            journal.Save(host.DataDir);
            var jobFile = Path.Combine(host.DataDir, JobFileName);
            var resultFile = Path.Combine(host.DataDir, ResultFileName);
            if (File.Exists(resultFile)) File.Delete(resultFile);
            Json.WriteFile(jobFile, JobJson(journal.Id, mode, todo));

            foreach (var d in todo) host.Write(string.Format("Shrinking {0} ({1})…", d.Name, Fmt.Size(d.Size)));
            host.Write("Asking Windows for administrator permission (one prompt)…");
            int exit; string launchError;
            var launch = host.RunWorker(exe, jobFile, WorkerTimeoutMs(todo), out exit, out launchError);

            if (launch == WorkerLaunch.UacDeclined || launch == WorkerLaunch.StartFailed)
            {
                // The worker never ran, so wslim issued no disk command at all: the journal has nothing to guard.
                Journal.Clear(host.DataDir);
                TryDelete(jobFile);
                if (launch == WorkerLaunch.UacDeclined)
                    FailAll(res, todo, Outcome.Cancelled, "administrator permission was not given, so no disk was changed (WSL had already been shut down)");
                else
                    FailAll(res, todo, Outcome.Failed, "the elevated worker could not be started (" + launchError + "); no disk was changed (WSL had already been shut down)");
                host.Write((launch == WorkerLaunch.UacDeclined ? "Cancelled: " : "Failed: ") + res.Message + ".");
                RestartIfUntouched(host, res, todo, running);
                return;
            }
            if (launch == WorkerLaunch.TimedOut)
            {
                FailAll(res, todo, Outcome.RecoveryRequired, "the elevated worker did not end in time, so the state of the disks is unknown");
                res.RecoveryPaths = todo.Select(d => d.Path).ToList();
                host.Write("Recovery required: " + res.Message + ".");
                res.WslMessage = "WSL was not started again";
                return;
            }

            var wr = ReadResult(resultFile, journal.Id);
            if (wr == null)
            {
                if (exit == WorkerRefused)
                {
                    // The worker itself reports that it refused the job before looking at any disk.
                    Journal.Clear(host.DataDir);
                    TryDelete(jobFile);
                    FailAll(res, todo, Outcome.Failed, "the elevated worker refused the job before touching any disk (see the log; this happens when the administrator account is a different user); no disk was changed");
                    host.Write("Failed: " + res.Message + ".");
                    RestartIfUntouched(host, res, todo, running);
                    return;
                }
                FailAll(res, todo, Outcome.RecoveryRequired, "the elevated worker ended without a readable result (" + RcText(exit) + "), so the state of the disks is unknown");
                res.RecoveryPaths = todo.Select(d => d.Path).ToList();
                host.Write("Recovery required: " + res.Message + ".");
                res.WslMessage = "WSL was not started again";
                return;
            }

            // 5. Per-disk outcomes exactly as the worker verified them.
            foreach (var d in todo)
            {
                var r = res.Disks.First(x => x.Path == d.Path && x.Name == d.Name);
                var w = wr.Disks.FirstOrDefault(x => SamePath(x.Path, d.Path) && x.Name == d.Name);
                if (w == null) { r.Outcome = Outcome.RecoveryRequired; r.Message = "the worker did not report on this disk"; continue; }
                r.Outcome = w.Outcome; r.Message = w.Message; r.Skipped = w.Skipped; r.Checked = w.Checked;
                r.Attached = w.Attached; r.Exclusive = w.Exclusive; r.Before = w.Before; r.After = w.After;
            }
            foreach (var d in todo)
            {
                var r = res.Disks.First(x => x.Path == d.Path && x.Name == d.Name);
                if (r.Skipped) host.Write(d.Name + ": " + r.Message + ".");
                else if (r.Outcome == Outcome.Success || r.Outcome == Outcome.NoSpaceReclaimed)
                    host.Write(string.Format("{0}: {1} → {2} ({3})", d.Name, Fmt.Size(r.Before), Fmt.Size(r.After), Report.Word(r.Outcome)));
                else host.Write(d.Name + ": " + Report.Word(r.Outcome) + ": " + r.Message + ".");
            }
            res.Outcome = Aggregate(res.Disks);
            bool safe = wr.Safe && res.Outcome != Outcome.RecoveryRequired;
            if (!safe)
            {
                res.Outcome = Outcome.RecoveryRequired;
                res.RecoveryPaths = res.Disks.Where(r => r.Outcome == Outcome.RecoveryRequired).Select(r => r.Path).ToList();
                if (res.RecoveryPaths.Count == 0) res.RecoveryPaths = todo.Select(d => d.Path).ToList();
                res.WslMessage = "WSL was not started again";
                return;   // the journal stays
            }
            Journal.Clear(host.DataDir);   // only here: the worker's result says every disk is verified safe
            TryDelete(jobFile); TryDelete(resultFile);

            // 6. Bring WSL back, only when every disk was verified: not attached and openable.
            var unverified = todo.Where(d => { var r = res.Disks.First(x => x.Path == d.Path && x.Name == d.Name); return !r.Skipped && r.Checked && !(r.Attached == false && r.Exclusive); }).ToList();
            if (unverified.Count > 0)
            {
                res.WslMessage = "WSL was stopped and wslim did not start it again, because the disk of " + string.Join(", ", unverified.Select(d => d.Name)) + " could not be verified as free (not attached and not in use)";
                host.Write(res.WslMessage + ".");
                return;
            }
            RestartDistros(host, res, running);
        }

        static string Tail(string output)
        {
            if (string.IsNullOrEmpty(output)) return "";
            var t = string.Join(" ", output.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0));
            if (t.Length > 300) t = t.Substring(0, 300) + "…";
            return t.Length > 0 ? ": " + t : "";
        }

        static void TryDelete(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }

        // After a cancel or a worker that never ran: wslim only stopped WSL. Start it again unless a disk is
        // visibly not free (then starting WSL would only produce the error from the incident).
        static void RestartIfUntouched(IHost host, CompactResult res, List<DiskSpec> todo, HashSet<string> running)
        {
            foreach (var d in todo)
            {
                if (host.IsAttached(d.Path) == true || !host.CanOpenExclusive(d.Path))
                {
                    res.WslMessage = "WSL was stopped and wslim did not start it again, because the disk of " + d.Name + " is attached or in use";
                    host.Write(res.WslMessage + ". To look at it: " + CheckCommand(d.Path));
                    return;
                }
            }
            RestartDistros(host, res, running);
        }

        // Starts the distros that were running before the shutdown. Managed ones (Docker Desktop) are left to their app.
        public static void RestartDistros(IHost host, CompactResult res, IEnumerable<string> running)
        {
            var names = running.Where(n => !Wsl.IsManagedName(n)).ToList();
            if (names.Count == 0)
            {
                res.WslMessage = "no distro was running before, so none was started";
                host.Write("WSL stays stopped: " + res.WslMessage + ".");
                return;
            }
            var errors = new List<string>();
            foreach (var n in names)
            {
                host.Write("Starting " + n + " again…");
                string output;
                int rc = host.Run("wsl.exe", "-d " + n + " -- true", 60000, out output);
                if (rc != 0) errors.Add("could not start " + n + " (" + RcText(rc) + ")" + Tail(output));
            }
            if (errors.Count == 0)
            {
                res.WslRestarted = true;
                res.WslMessage = "WSL is running again (" + string.Join(", ", names) + ")";
            }
            else res.WslMessage = string.Join("; ", errors);
            host.Write(res.WslMessage + ".");
        }

        // ---------------------------------------------------------------- job and result files
        static string JobJson(string id, CompactMode mode, List<DiskSpec> disks)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"wslim\": \"compact-job\",\n  \"id\": ").Append(Json.Q(id)).Append(",\n  \"mode\": ").Append(Json.Q(mode.ToString())).Append(",\n  \"disks\": [");
            for (int i = 0; i < disks.Count; i++)
                sb.Append(i == 0 ? "\n" : ",\n").Append("    {\"name\": ").Append(Json.Q(disks[i].Name)).Append(", \"path\": ").Append(Json.Q(disks[i].Path)).Append(", \"size\": ").Append(Json.N(disks[i].Size)).Append("}");
            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        class WorkerResult
        {
            public bool Safe;
            public List<DiskResult> Disks = new List<DiskResult>();
        }

        static string ResultJson(string id, bool safe, List<DiskResult> disks)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"wslim\": \"compact-result\",\n  \"id\": ").Append(Json.Q(id)).Append(",\n  \"safe\": ").Append(Json.B(safe)).Append(",\n  \"disks\": [");
            for (int i = 0; i < disks.Count; i++)
            {
                var d = disks[i];
                sb.Append(i == 0 ? "\n" : ",\n")
                  .Append("    {\"name\": ").Append(Json.Q(d.Name))
                  .Append(", \"path\": ").Append(Json.Q(d.Path))
                  .Append(", \"outcome\": ").Append(Json.Q(d.Outcome.ToString()))
                  .Append(", \"skipped\": ").Append(Json.B(d.Skipped))
                  .Append(", \"checked\": ").Append(Json.B(d.Checked))
                  .Append(", \"attached\": ").Append(Json.Q(d.Attached == null ? "unknown" : (d.Attached.Value ? "yes" : "no")))
                  .Append(", \"exclusive\": ").Append(Json.B(d.Exclusive))
                  .Append(", \"before\": ").Append(Json.N(d.Before))
                  .Append(", \"after\": ").Append(Json.N(d.After))
                  .Append(", \"message\": ").Append(Json.Q(d.Message)).Append("}");
            }
            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        static WorkerResult ReadResult(string file, string id)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var o = Json.Obj(Json.Parse(File.ReadAllText(file, Encoding.UTF8)));
                if (Json.S(o, "wslim") != "compact-result" || Json.S(o, "id") != id) return null;
                var wr = new WorkerResult { Safe = Json.Bool(o, "safe") };
                foreach (var x in Json.A(o, "disks"))
                {
                    var d = Json.Obj(x);
                    var r = new DiskResult { Name = Json.S(d, "name"), Path = Json.S(d, "path"), Message = Json.S(d, "message") };
                    Outcome oc;
                    if (!Enum.TryParse(Json.S(d, "outcome"), out oc) || !Enum.IsDefined(typeof(Outcome), oc)) return null;
                    r.Outcome = oc;
                    r.Skipped = Json.Bool(d, "skipped"); r.Checked = Json.Bool(d, "checked"); r.Exclusive = Json.Bool(d, "exclusive");
                    var a = Json.S(d, "attached");
                    r.Attached = a == "yes" ? (bool?)true : (a == "no" ? (bool?)false : null);
                    r.Before = Json.L(d, "before", -1); r.After = Json.L(d, "after", -1);
                    wr.Disks.Add(r);
                }
                return wr;
            }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------- the worker side (elevated)
        // wslim-cli.exe --compact-worker <job-file>. Does only disk work: check, DiskPart, check again, result file.
        public static int WorkerMain(IHost host, string jobFile)
        {
            if (!host.IsElevated()) { host.Write("Worker refused: it is not running as administrator."); return WorkerRefused; }

            // The job must be the file wslim itself wrote: fixed name, inside the data folder, matching the journal.
            string id; CompactMode mode; List<DiskSpec> disks; Journal journal;
            try
            {
                var full = Path.GetFullPath(jobFile);
                var dir = Path.GetFullPath(host.DataDir).TrimEnd('\\');
                if (!string.Equals(Path.GetDirectoryName(full).TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(Path.GetFileName(full), JobFileName, StringComparison.OrdinalIgnoreCase))
                { host.Write("Worker refused: the job file is not wslim's own job file in " + dir + "."); return WorkerRefused; }
                if (!File.Exists(full) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                { host.Write("Worker refused: the job file does not exist or is a link."); return WorkerRefused; }
                var o = Json.Obj(Json.Parse(File.ReadAllText(full, Encoding.UTF8)));
                if (Json.S(o, "wslim") != "compact-job") throw new FormatException("not a wslim job");
                id = Json.S(o, "id");
                if (string.IsNullOrEmpty(id)) throw new FormatException("no id");
                if (!Enum.TryParse(Json.S(o, "mode"), out mode) || !Enum.IsDefined(typeof(CompactMode), mode)) throw new FormatException("unknown mode");
                disks = new List<DiskSpec>();
                foreach (var x in Json.A(o, "disks"))
                {
                    var d = Json.Obj(x);
                    var spec = new DiskSpec { Name = Json.S(d, "name"), Path = Json.S(d, "path"), Size = Json.L(d, "size", -1) };
                    if (string.IsNullOrEmpty(spec.Name) || string.IsNullOrEmpty(spec.Path)) throw new FormatException("a disk without a name or path");
                    disks.Add(spec);
                }
                if (disks.Count == 0) throw new FormatException("no disks");
                journal = Journal.Load(host.DataDir);
                if (journal == null || journal.Corrupt || journal.Id != id) { host.Write("Worker refused: the job does not belong to the operation in the journal."); return WorkerRefused; }
                // Every path must be a disk that is registered right now (a WSL distro, or Docker Desktop's data disk).
                foreach (var d in disks)
                {
                    var e = ScriptSafetyError(d.Path);
                    if (e == null && !SamePath(host.RegisteredVhdPath(d.Name), d.Path)) e = "it is not the disk registered for " + d.Name;
                    if (e != null) { host.Write("Worker refused: " + d.Path + ": " + e + "."); return WorkerRefused; }
                }
            }
            catch (Exception ex) { host.Write("Worker refused: the job file could not be read (" + ex.Message + ")."); return WorkerRefused; }

            using (var gate = host.TryLock(WorkerLockName))
            {
                if (gate == null) { host.Write("Worker refused: another worker is already running."); return WorkerRefused; }

                var results = new List<DiskResult>();
                bool diskpartRan = false, stop = false;
                journal.Phase = "working"; journal.Save(host.DataDir);
                foreach (var d in disks)
                {
                    var r = new DiskResult { Name = d.Name, Path = d.Path, Before = d.Size };
                    results.Add(r);
                    var jd = journal.Disks.FirstOrDefault(x => SamePath(x.Path, d.Path));
                    if (jd == null) { r.Outcome = Outcome.Failed; r.Message = "not in the journal; not touched"; continue; }
                    if (stop) { r.Outcome = Outcome.Failed; r.Message = "not attempted, because an earlier disk needs recovery; not touched"; jd.Phase = "not-attempted"; continue; }
                    try { CompactOne(host, journal, jd, d, mode, r, ref diskpartRan); }
                    catch (Exception ex)
                    {
                        r.Outcome = Outcome.RecoveryRequired;
                        r.Message = "unexpected error (" + ex.Message + "); the state of this disk was not verified";
                    }
                    jd.Phase = r.Outcome.ToString();
                    try { journal.Save(host.DataDir); } catch { }
                    host.Write("[worker] " + d.Name + ": " + Report.Word(r.Outcome) + (r.Message != null ? ": " + r.Message : ""));
                    if (r.Outcome == Outcome.RecoveryRequired) stop = true;
                }
                bool safe = !results.Any(r => r.Outcome == Outcome.RecoveryRequired);
                journal.Phase = safe ? "verified" : "recovery-required";
                try { journal.Save(host.DataDir); } catch { }
                Json.WriteFile(Path.Combine(host.DataDir, ResultFileName), ResultJson(id, safe, results));
                return WorkerOk;
            }
        }

        // Reads the disk state into r. True when the disk is verified: not attached, and openable exclusively.
        static bool ReadState(IHost host, string path, DiskResult r)
        {
            r.Attached = host.IsAttached(path);
            r.Exclusive = host.CanOpenExclusive(path);
            r.Checked = true;
            return r.Attached == false && r.Exclusive;
        }

        static void NeedsRecovery(DiskResult r, string what)
        {
            r.Outcome = Outcome.RecoveryRequired;
            r.Message = what + (r.Checked ? " (attached to Windows: " + (r.Attached == null ? "could not be read" : (r.Attached.Value ? "yes" : "no")) + ", can be opened: " + (r.Exclusive ? "yes" : "no") + ")" : "");
        }

        static void CompactOne(IHost host, Journal journal, JournalDisk jd, DiskSpec d, CompactMode mode, DiskResult r, ref bool diskpartRan)
        {
            // Preflight. Nothing is changed in this part.
            bool sparse;
            var err = IdentityError(host, d, out sparse);
            if (err != null)
            {
                if (sparse) { r.Skipped = true; r.Outcome = Outcome.NoSpaceReclaimed; r.Message = err; }
                else { r.Outcome = Outcome.Failed; r.Message = err + "; not touched"; }
                return;
            }
            string pathErr;
            var scriptPath = host.ScriptPath(d.Path, out pathErr);
            if (scriptPath == null) { r.Outcome = Outcome.Failed; r.Message = (pathErr ?? "DiskPart cannot read this path") + "; not touched"; return; }
            var scripts = BuildCompactScript(scriptPath, mode);

            r.Attached = host.IsAttached(d.Path);
            r.Checked = true;
            if (r.Attached == true)
            {
                // Somebody else attached it. wslim did not, and wslim never detaches a disk it did not attach.
                r.Exclusive = false;
                r.Outcome = Outcome.RecoveryRequired;
                r.Message = "the disk is already attached to Windows by something else; wslim did not attach it and will not detach it";
                return;
            }
            r.Exclusive = host.CanOpenExclusive(d.Path);
            if (r.Attached == null)
            {
                r.Outcome = Outcome.Failed;
                r.Message = "could not read whether the disk is attached to Windows (Get-DiskImage failed), so it was not touched";
                return;
            }
            if (!r.Exclusive)
            {
                r.Outcome = Outcome.Failed;
                r.Message = "the disk file is in use by another program; nothing was changed";
                return;
            }
            r.Before = host.FileLength(d.Path);
            int timeout = CompactTimeoutMs(r.Before);
            string output;

            if (mode == CompactMode.Detached)
            {
                if (diskpartRan) host.Sleep(ScriptPauseMs);
                jd.Phase = "compacting"; journal.Save(host.DataDir);
                host.Write("[worker] " + d.Name + ": compact vdisk (detached), up to " + (timeout / 60000) + " min");
                diskpartRan = true;
                r.Checked = false;
                int rc = host.RunDiskpart(scripts[0], timeout, out output);
                LogOutput(host, output);
                if (rc == StillRunning) { NeedsRecovery(r, "DiskPart took too long and could not be stopped, so the disk was not checked"); return; }
                Finish(host, d, r, rc);
                return;
            }

            // Fallback mode: attach read-only, compact, detach: three separate DiskPart runs.
            jd.AttachedByWslim = true; jd.Phase = "attaching";
            journal.Save(host.DataDir);                 // recorded BEFORE the attach
            if (diskpartRan) host.Sleep(ScriptPauseMs);
            host.Write("[worker] " + d.Name + ": attach vdisk readonly");
            diskpartRan = true;
            r.Checked = false;
            int rcAttach = host.RunDiskpart(scripts[0], 120000, out output);
            LogOutput(host, output);
            if (rcAttach == StillRunning) { NeedsRecovery(r, "DiskPart (attach) took too long and could not be stopped, so the disk was not checked"); return; }
            int rcCompact = rcAttach;
            if (rcAttach != 0 && host.IsAttached(d.Path) == false)
            {
                // The attach failed and left nothing behind.
                jd.AttachedByWslim = false;
                if (ReadState(host, d.Path, r)) { r.After = host.FileLength(d.Path); r.Outcome = Outcome.Failed; r.Message = "DiskPart could not attach the disk (" + RcText(rcAttach) + "); verified not attached, nothing was changed"; }
                else NeedsRecovery(r, "DiskPart could not attach the disk (" + RcText(rcAttach) + ") and its state could not be verified");
                return;
            }
            if (rcAttach == 0)
            {
                host.Sleep(ScriptPauseMs);
                jd.Phase = "compacting"; journal.Save(host.DataDir);
                host.Write("[worker] " + d.Name + ": compact vdisk (attached read-only), up to " + (timeout / 60000) + " min");
                rcCompact = host.RunDiskpart(scripts[1], timeout, out output);
                LogOutput(host, output);
                if (rcCompact == StillRunning) { NeedsRecovery(r, "DiskPart (compact) took too long and could not be stopped; the disk is still attached"); return; }
            }
            // Detach always, as its own DiskPart run, whatever happened to the compaction.
            host.Sleep(ScriptPauseMs);
            jd.Phase = "detaching"; journal.Save(host.DataDir);
            host.Write("[worker] " + d.Name + ": detach vdisk");
            int rcDetach = host.RunDiskpart(scripts[2], 120000, out output);
            LogOutput(host, output);
            if (rcDetach == StillRunning) { NeedsRecovery(r, "DiskPart (detach) took too long and could not be stopped, so the disk was not checked"); return; }
            if (host.IsAttached(d.Path) != false)
            {
                // One retry by another route. Allowed, because the journal says wslim attached this disk.
                host.Write("[worker] " + d.Name + ": still attached after detach vdisk (" + RcText(rcDetach) + "); trying Dismount-DiskImage once");
                int rcDis = host.Dismount(d.Path, out output);
                LogOutput(host, output);
                host.Write("[worker] " + d.Name + ": Dismount-DiskImage: " + RcText(rcDis));
            }
            if (!ReadState(host, d.Path, r)) { NeedsRecovery(r, "the disk was attached by wslim and could not be verified as detached"); return; }
            jd.AttachedByWslim = false;
            Finish(host, d, r, rcCompact);
        }

        // Postcondition after the last DiskPart run for this disk, then the outcome.
        static void Finish(IHost host, DiskSpec d, DiskResult r, int rcCompact)
        {
            if (!ReadState(host, d.Path, r)) { NeedsRecovery(r, "the disk could not be verified after DiskPart (" + RcText(rcCompact) + ")"); return; }
            r.After = host.FileLength(d.Path);
            if (rcCompact != 0)
            {
                r.Outcome = Outcome.Failed;
                r.Message = "DiskPart did not compact the disk (" + RcText(rcCompact) + "); verified afterwards: not attached and can be opened";
                return;
            }
            if (r.After < r.Before) { r.Outcome = Outcome.Success; r.Message = null; }
            else { r.Outcome = Outcome.NoSpaceReclaimed; r.Message = "compaction ran, the file kept its size"; }
        }

        // ---------------------------------------------------------------- recovery check (read-only)
        // Looks at the disks named in the journal. Clears the journal only when every one of them is verified
        // not attached. Returns true when the journal is gone afterwards.
        public static bool RecoverCheck(IHost host, List<string> lines)
        {
            var j = Journal.Load(host.DataDir);
            if (j == null) { lines.Add("Nothing to recover: no unfinished operation is recorded."); return true; }
            var paths = j.Disks.Select(d => d.Path).ToList();
            if (paths.Count == 0) paths = host.AllDiskPaths();
            bool allFree = paths.Count > 0;
            foreach (var p in paths)
            {
                if (!host.FileExists(p)) { lines.Add(p + ": the file does not exist (nothing can be attached)"); continue; }
                var a = host.IsAttached(p);
                if (a == false) lines.Add(p + ": not attached to Windows");
                else if (a == true) { allFree = false; lines.Add(p + ": ATTACHED to Windows"); }
                else { allFree = false; lines.Add(p + ": state could not be read"); }
            }
            if (allFree)
            {
                Journal.Clear(host.DataDir);
                TryDelete(Path.Combine(host.DataDir, JobFileName));
                TryDelete(Path.Combine(host.DataDir, ResultFileName));
                host.Write("Recovery check: no recorded disk is attached; the journal was cleared.");
                lines.Add("No recorded disk is attached to Windows. wslim is unblocked, and WSL can be started again.");
                return true;
            }
            host.Write("Recovery check: still blocked. " + string.Join("; ", lines));
            lines.Add("Still blocked. If the state could not be read, run the check from an administrator prompt.");
            lines.Add("If you have verified with the commands above that every disk shows Attached : False, you may delete " + Journal.FileIn(host.DataDir));
            return false;
        }

        // The text shown before WSL is stopped. wsl --shutdown stops every distro, not only the shrunk ones.
        public static List<string> ConsentLines(HashSet<string> running, bool ok)
        {
            var l = new List<string>();
            l.Add("To shrink the disks, wslim runs \"wsl --shutdown\". That stops ALL WSL distros, including ones that are not being shrunk.");
            if (!ok) l.Add("wslim could not ask WSL which distros are running right now.");
            else if (running.Count == 0) l.Add("No distro is running right now.");
            else l.Add("Running now, all will be stopped: " + string.Join(", ", running.OrderBy(x => x)) + ".");
            l.Add("Claude Code, VS Code, terminals and containers running in WSL will close, so save your work first.");
            l.Add("Afterwards, once every disk is verified safe, wslim starts the same distros again.");
            l.Add("Windows will ask for administrator permission once (needed to shrink the disks).");
            return l;
        }
    }

    // ------------------------------------------------------------------ outcome words and the final report
    static class Report
    {
        public static string Word(Outcome o)
        {
            switch (o)
            {
                case Outcome.Success: return "Success";
                case Outcome.NoSpaceReclaimed: return "No space reclaimed";
                case Outcome.PartialSuccess: return "Partial success";
                case Outcome.Failed: return "Failed";
                case Outcome.Cancelled: return "Cancelled";
                default: return "Recovery required";
            }
        }

        // Exit codes of wslim-cli: 0 ok, 1 failed / partial / cancelled, 3 recovery required (2 is usage).
        public static int ExitCode(Outcome o)
        {
            if (o == Outcome.Success || o == Outcome.NoSpaceReclaimed) return 0;
            return o == Outcome.RecoveryRequired ? 3 : 1;
        }
    }

    class OpResult
    {
        public Outcome Outcome = Outcome.Failed;
        public bool Preview;
        public string Detail = "";
        public long FreedInside;                 // bytes freed inside Linux (reported by the agent)
        public long Returned = -1;               // bytes returned to Windows; -1 when no compaction succeeded
        public CompactResult Compact;
        public List<Distro> CleanedOk = new List<Distro>();
        public List<Distro> CleanFailed = new List<Distro>();
        public List<string> RecoveryLines = new List<string>();

        public string Word { get { return Report.Word(Outcome); } }
        public string Banner { get { return Word + " · " + Detail; } }      // window banner and status bar
        public string LogLine { get { return Word + ": " + Detail; } }      // log, and the last line of wslim-cli
        public string HistoryLine { get { return Word + ": " + Detail; } }  // history.tsv
        public int ExitCode { get { return Report.ExitCode(Outcome); } }
    }

    delegate int CleanFn(Distro d, List<Item> picked, bool preview, Action<long> onFreed);

    // The clean / preview / shrink flow, the same for the window and the console program.
    static class Operation
    {
        // shrink: the user asked for the disks to be shrunk afterwards. disks: the shrink targets.
        // A preview never stops WSL, never elevates and never runs a disk command: it returns before any of that.
        public static OpResult Execute(IHost host, List<Item> picked, bool preview, bool shrink, List<Distro> disks, CompactMode mode, CleanFn clean)
        {
            var r = new OpResult { Preview = preview };
            var pending = Journal.Load(host.DataDir);
            if (pending != null)
            {
                r.Outcome = Outcome.RecoveryRequired;
                r.Detail = "an earlier shrink did not end properly; nothing was started";
                r.RecoveryLines = Compact.RecoveryLines(pending.Disks.Select(d => d.Path), pending.Disks.Any(d => d.AttachedByWslim));
                host.Write(r.LogLine);
                return r;
            }

            host.Write(preview ? "Preview (nothing is changed)" : "Clean started");
            long freed = 0;
            foreach (var g in picked.GroupBy(i => i.Distro))
            {
                int rc;
                try { rc = clean(g.Key, g.ToList(), preview, b => Interlocked.Add(ref freed, b)); }
                catch (Exception ex) { rc = -1; g.Key.Error = (preview ? "preview" : "cleanup") + " failed in " + g.Key.Name + ": " + ex.Message; host.Write(g.Key.Error); }
                if (rc == 0) r.CleanedOk.Add(g.Key); else r.CleanFailed.Add(g.Key);
            }
            r.FreedInside = Interlocked.Read(ref freed);

            if (preview)
            {
                if (r.CleanFailed.Count > 0)
                {
                    r.Outcome = r.CleanedOk.Count > 0 ? Outcome.PartialSuccess : Outcome.Failed;
                    r.Detail = "Preview failed for " + string.Join(", ", r.CleanFailed.Select(d => d.Name)) + ". Nothing was changed.";
                    foreach (var d in r.CleanFailed) host.Write("Preview failed for " + d.Name + (d.Error != null ? ": " + d.Error : "") + ".");
                    host.Write("Nothing was changed.");
                    return r;
                }
                if (shrink && disks.Count > 0) host.Write("Would then shut WSL down (all distros) and shrink: " + string.Join(", ", disks.Select(d => d.Name)));
                r.Outcome = Outcome.Success;
                r.Detail = "Preview finished. Nothing was changed.";
                host.Write(r.Detail);
                return r;
            }

            if (picked.Count > 0) host.Write("Freed inside Linux: " + Fmt.Size(r.FreedInside));
            bool shrinkSkipped = false;
            if (shrink && disks.Count > 0)
            {
                if (r.CleanFailed.Count > 0)
                {
                    shrinkSkipped = true;
                    host.Write("Shrinking skipped, because the cleanup did not work in: " + string.Join(", ", r.CleanFailed.Select(d => d.Name)) + ". WSL was not stopped.");
                }
                else r.Compact = Compact.Run(host, disks.Select(d => new DiskSpec { Name = d.Name, Path = d.VhdPath, Size = d.FileSize }).ToList(), mode);
            }

            Summarize(r, picked.Count > 0, shrink, shrinkSkipped);
            host.Write(r.LogLine);
            host.History(r.HistoryLine);
            foreach (var l in r.RecoveryLines) host.Write(l);
            return r;
        }

        public static void Summarize(OpResult r, bool cleaned, bool shrink, bool shrinkSkipped)
        {
            var c = r.Compact;
            // Outcome.
            if (c == null)
            {
                if (r.CleanFailed.Count == 0) r.Outcome = Outcome.Success;
                else r.Outcome = r.CleanedOk.Count > 0 ? Outcome.PartialSuccess : Outcome.Failed;
            }
            else if (c.Outcome == Outcome.Failed && r.CleanedOk.Count > 0) r.Outcome = Outcome.PartialSuccess;   // cleanup worked, compaction did not
            else r.Outcome = c.Outcome;

            // Detail: the two numbers are separate. "returned to Windows" only exists when a compaction worked.
            var parts = new List<string>();
            if (r.CleanFailed.Count > 0)
                parts.Add(string.Join("; ", r.CleanFailed.Select(d => d.Error ?? "cleanup did not work in " + d.Name)));
            if (cleaned && r.CleanedOk.Count > 0) parts.Add("freed inside Linux: " + Fmt.Size(r.FreedInside));
            if (shrinkSkipped) parts.Add("disks were not shrunk and WSL was not stopped");
            if (c != null)
            {
                var good = c.Disks.Where(d => !d.Skipped && (d.Outcome == Outcome.Success || d.Outcome == Outcome.NoSpaceReclaimed)).ToList();
                if (good.Count > 0)
                {
                    long b = good.Sum(d => d.Before), a = good.Sum(d => d.After);
                    r.Returned = Math.Max(0, b - a);
                    parts.Add(string.Format("returned to Windows: {0} (disks {1} → {2})", Fmt.Size(r.Returned), Fmt.Size(b), Fmt.Size(a)));
                }
                foreach (var d in c.Disks.Where(d => !d.Skipped && d.Message != null && d.Outcome != Outcome.Success && d.Outcome != Outcome.NoSpaceReclaimed))
                    parts.Add(d.Name + ": " + d.Message);
                if (c.Message != null) parts.Add(c.Message);
                if (c.WslMessage != null) parts.Add(c.WslMessage);
                if (c.Outcome == Outcome.RecoveryRequired)
                    r.RecoveryLines = Compact.RecoveryLines(c.RecoveryPaths, false);
            }
            else if (!shrink && cleaned && r.CleanedOk.Count > 0) parts.Add("disk files on Windows keep their size until they are shrunk");
            if (parts.Count == 0) parts.Add(shrink ? "no disk to shrink" : "nothing to do");
            r.Detail = string.Join("; ", parts);
        }
    }

    // ------------------------------------------------------------------ lock across processes
    sealed class ProcessLock : IDisposable
    {
        Mutex m;
        ProcessLock(Mutex mutex) { m = mutex; }

        // null when another process (or another thread) holds the lock, or when no lock can be had at all.
        public static ProcessLock TryAcquire(string name)
        {
            foreach (var prefix in new[] { "Global\\", "Local\\" })
            {
                Mutex mx;
                try
                {
                    // Everyone may wait on it, so an elevated and a normal wslim see the same lock.
                    var sec = new MutexSecurity();
                    sec.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
                    sec.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User, MutexRights.FullControl, AccessControlType.Allow));
                    bool created;
                    mx = new Mutex(false, prefix + name, out created, sec);
                }
                catch (UnauthorizedAccessException) { if (prefix == "Global\\") continue; return null; }
                bool got;
                try { got = mx.WaitOne(0); }
                catch (AbandonedMutexException) { got = true; }   // the previous owner died; the journal covers that case
                if (!got) { mx.Dispose(); return null; }
                return new ProcessLock(mx);
            }
            return null;
        }

        public void Dispose()
        {
            if (m == null) return;
            try { m.ReleaseMutex(); } catch { }
            m.Dispose(); m = null;
        }
    }

    // ------------------------------------------------------------------ the real machine
    class RealHost : IHost
    {
        static readonly string System32 = Environment.SystemDirectory;
        static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

        [DllImport("kernel32.dll")] static extern uint GetOEMCP();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle h, out ByHandleInfo info);
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct ByHandleInfo
        {
            public uint Attributes; public long Creation, LastAccess, LastWrite;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        public static Encoding Oem
        {
            get { try { return Encoding.GetEncoding((int)GetOEMCP()); } catch { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } }
        }

        public virtual string DataDir { get { return Log.Dir; } }
        public virtual void Write(string text) { Log.Write(text); }
        public virtual void History(string text) { Log.History(text); }
        public virtual void Sleep(int ms) { Thread.Sleep(ms); }

        // Starts a program, collects its output, and on timeout stops that one process and waits for it to exit.
        protected static int Exec(string exe, string args, Encoding enc, Dictionary<string, string> env, int timeoutMs, out string output)
        {
            var sb = new StringBuilder();
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = enc, StandardErrorEncoding = enc
                };
                if (env != null) foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;
                using (var p = new Process { StartInfo = psi })
                {
                    DataReceivedEventHandler h = (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data.Replace("\0", "")); };
                    p.OutputDataReceived += h; p.ErrorDataReceived += h;
                    p.Start();
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        bool gone = false;
                        try { gone = p.WaitForExit(30000); } catch { }
                        lock (sb) output = sb.ToString();
                        return gone ? Compact.TimedOut : Compact.StillRunning;
                    }
                    p.WaitForExit();   // flushes the async readers
                    lock (sb) output = sb.ToString();
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return Compact.StartError; }
        }

        public virtual int Run(string exe, string args, int timeoutMs, out string output)
        {
            var env = new Dictionary<string, string>(); env["WSL_UTF8"] = "1";
            int rc = Exec(exe, args, Encoding.UTF8, env, timeoutMs, out output);
            return rc == Compact.StillRunning ? Compact.TimedOut : rc;
        }

        public virtual int RunDiskpart(string script, int timeoutMs, out string output)
        {
            var dir = Path.Combine(Path.GetTempPath(), "wslim");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "diskpart-" + Guid.NewGuid().ToString("N") + ".txt");
            var oem = Oem;
            File.WriteAllText(file, script, oem);   // DiskPart reads its script in the OEM code page
            try { return Exec(Path.Combine(System32, "diskpart.exe"), "/s \"" + file + "\"", oem, null, timeoutMs, out output); }
            finally { try { File.Delete(file); } catch { } }
        }

        // The path travels in an environment variable, so no character in it can change the command.
        int PowerShellOnDisk(string command, string path, int timeoutMs, out string output)
        {
            var env = new Dictionary<string, string>(); env["WSLIM_VHD"] = path;
            return Exec(PowerShell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"$ErrorActionPreference='Stop'; " + command + "\"", Oem, env, timeoutMs, out output);
        }

        public virtual bool? IsAttached(string path)
        {
            string output;
            int rc = PowerShellOnDisk("$i = Get-DiskImage -ImagePath $env:WSLIM_VHD; 'WSLIM_ATTACHED=' + [string]$i.Attached", path, 60000, out output);
            if (rc != 0 || output == null) return null;
            if (output.IndexOf("WSLIM_ATTACHED=True", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (output.IndexOf("WSLIM_ATTACHED=False", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return null;
        }

        public virtual int Dismount(string path, out string output)
        {
            return PowerShellOnDisk("Dismount-DiskImage -ImagePath $env:WSLIM_VHD | Out-Null", path, 120000, out output);
        }

        public virtual bool CanOpenExclusive(string path)
        {
            try { using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) return true; }
            catch (Exception) { return false; }
        }

        public virtual bool FileExists(string path) { return File.Exists(path); }
        public virtual long FileLength(string path) { return new FileInfo(path).Length; }
        public virtual bool IsReparsePoint(string path) { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        public virtual bool IsSparse(string path) { return (File.GetAttributes(path) & FileAttributes.SparseFile) != 0; }

        static bool Ascii(string s) { return s.All(ch => ch < 128); }

        static bool SameFile(string a, string b)
        {
            const uint ShareAll = 7, OpenExisting = 3;
            using (var ha = CreateFile(a, 0, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
            using (var hb = CreateFile(b, 0, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
            {
                if (ha.IsInvalid || hb.IsInvalid) return false;
                ByHandleInfo ia, ib;
                if (!GetFileInformationByHandle(ha, out ia) || !GetFileInformationByHandle(hb, out ib)) return false;
                return ia.VolumeSerial == ib.VolumeSerial && ia.IndexHigh == ib.IndexHigh && ia.IndexLow == ib.IndexLow;
            }
        }

        // DiskPart reads its script in the OEM code page. A plain ASCII path is used as it is. Otherwise the 8.3
        // short path is used, but only if it exists already and is proved to be the same file (nothing is ever
        // created). Otherwise the long path, if the OEM code page can spell it. Otherwise: a clear error.
        public virtual string ScriptPath(string path, out string error)
        {
            error = null;
            if (Ascii(path)) return path;
            try
            {
                var sb = new StringBuilder(1024);
                uint n = GetShortPathName(path, sb, (uint)sb.Capacity);
                if (n > 0 && n < sb.Capacity)
                {
                    var sp = sb.ToString();
                    if (Ascii(sp) && Compact.ScriptSafetyError(sp) == null && SameFile(sp, path)) return sp;
                }
            }
            catch (Exception) { }
            var oem = Oem;
            if (oem.GetString(oem.GetBytes(path)) == path) return path;
            error = "the path has characters that DiskPart cannot read on this PC (code page " + oem.CodePage + ") and it has no short 8.3 name: " + path;
            return null;
        }

        public virtual HashSet<string> RunningDistros(out bool ok) { return Wsl.RunningNames(out ok); }

        public virtual string RegisteredVhdPath(string distroName)
        {
            foreach (var d in Wsl.Registered())
                if (string.Equals(d.Name, distroName, StringComparison.OrdinalIgnoreCase)) return d.VhdPath;
            return null;
        }

        public virtual List<string> AllDiskPaths()
        {
            return Wsl.Registered().Where(d => d.VhdPath != null && File.Exists(d.VhdPath)).Select(d => d.VhdPath).ToList();
        }

        public virtual bool IsElevated()
        {
            try { using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch (Exception) { return false; }
        }

        public virtual string WorkerExe()
        {
            try
            {
                var exe = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "wslim-cli.exe");
                return File.Exists(exe) ? exe : null;
            }
            catch (Exception) { return null; }
        }

        // One UAC prompt, hidden window. Error 1223 = the user said no.
        public virtual WorkerLaunch RunWorker(string exe, string jobFile, int timeoutMs, out int exitCode, out string error)
        {
            exitCode = -1; error = null;
            try
            {
                var psi = new ProcessStartInfo(exe, "--compact-worker \"" + jobFile + "\"")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = System32 };
                using (var p = Process.Start(psi))
                {
                    if (p == null) { error = "Windows did not start it"; return WorkerLaunch.StartFailed; }
                    if (!p.WaitForExit(timeoutMs)) return WorkerLaunch.TimedOut;
                    exitCode = p.ExitCode;
                    return WorkerLaunch.Exited;
                }
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return WorkerLaunch.UacDeclined;
                error = ex.Message; return WorkerLaunch.StartFailed;
            }
        }

        public virtual IDisposable TryLock(string name) { return ProcessLock.TryAcquire(name); }
    }
}
