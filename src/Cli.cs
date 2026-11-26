// wslim-cli: the console version of wslim. Same scan, clean and shrink as the window (Core.cs, Compact.cs).
// It is also the elevated worker that the window and the console start for the disk work (--compact-worker).
// .NET Framework 4.8, C# 5 so the compiler that ships with Windows can build it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Wslim
{
    static class Cli
    {
        const string Usage =
@"wslim-cli {0}: finds what WSL can safely let go of, cleans it, and shrinks its disks.

Usage:
  wslim-cli scan   [--distro NAME] [--json]
  wslim-cli clean  [--safe | --ids a,b,c] [--distro NAME] [--shrink] [--preview] [--yes]
  wslim-cli shrink [--distro NAME] [--yes]
  wslim-cli recover
  wslim-cli --help | --version

Commands:
  scan     List the disks and what can be cleaned in each distribution. Changes nothing.
  clean    Clean the selected items. By default all items marked safe (--safe).
           Items marked review are only cleaned when named with --ids.
           An id selects that item in every scanned distribution (narrow with --distro).
  shrink   Shut WSL down (all distros), compact the virtual disks (asks for administrator
           permission), check every disk, then start the distros again.
  recover  After a shrink that did not end properly: check (read-only) whether a disk is still
           attached to Windows, and unblock wslim when none is.

Options:
  --distro NAME  Only this distribution.
  --json         Machine-readable output (scan).
  --safe         Select all safe items (the default for clean).
  --ids a,b,c    Select these item ids (see scan). Can include review items.
  --shrink       After cleaning, shut WSL down (all distros) and compact the disks.
  --preview      Show what clean would do. Nothing is changed.
  --yes, -y      Do not ask for confirmation.

Exit codes: 0 success or no space reclaimed, 1 failed, partial success or cancelled, 2 usage,
            3 recovery required.
Logs: {1}";

        static bool outCon, errCon;   // stdout / stderr is a console: dim text allowed, plain characters only
        static TextWriter Out, Err;

        static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Crash(e.ExceptionObject as Exception ?? new Exception("unknown"));
            // Hidden mode: the elevated worker for the disk work. Started only by wslim itself (Compact.cs).
            if (args.Length > 0 && args[0] == "--compact-worker")
            {
                try
                {
                    if (args.Length != 2) return Compact.WorkerRefused;
                    return Compact.WorkerMain(new RealHost(), args[1]);
                }
                catch (Exception ex) { Log.Crash(ex); Log.Write("[worker] unexpected error: " + ex.Message); return 1; }
            }
            SetupOutput();
            try { return Run(args); }
            catch (Exception ex)
            {
                Log.Crash(ex);
                Err.WriteLine("wslim-cli: " + ex.Message);
                return 1;
            }
        }

        // A console gets ASCII-safe text through the console's code page; a pipe or file gets UTF-8.
        static void SetupOutput()
        {
            bool outRedir = true, errRedir = true;
            try { outRedir = Console.IsOutputRedirected; errRedir = Console.IsErrorRedirected; } catch { }
            outCon = !outRedir; errCon = !errRedir;
            Out = new SafeWriter(outRedir ? Utf8(Console.OpenStandardOutput()) : Console.Out);
            Err = new SafeWriter(errRedir ? Utf8(Console.OpenStandardError()) : Console.Error);
        }

        // Started from a WSL shell, stdout and stderr go through WSL, so they break when shrink runs
        // wsl --shutdown. Output errors are ignored so the shrink still finishes (the log file has everything).
        sealed class SafeWriter : TextWriter
        {
            readonly TextWriter w;
            public SafeWriter(TextWriter inner) { w = inner; NewLine = inner.NewLine; }
            public override Encoding Encoding { get { return w.Encoding; } }
            public override void Write(char value) { try { w.Write(value); } catch (IOException) { } catch (ObjectDisposedException) { } }
            public override void Write(string value) { try { w.Write(value); } catch (IOException) { } catch (ObjectDisposedException) { } }
            public override void Write(char[] buffer, int index, int count) { try { w.Write(buffer, index, count); } catch (IOException) { } catch (ObjectDisposedException) { } }
            public override void Flush() { try { w.Flush(); } catch (IOException) { } catch (ObjectDisposedException) { } }
        }
        static TextWriter Utf8(Stream s)
        {
            var w = new StreamWriter(s, new UTF8Encoding(false)); w.AutoFlush = true; w.NewLine = "\n";
            return TextWriter.Synchronized(w);
        }

        // ---------------------------------------------------------------- arguments
        class Opts
        {
            public string Command, Distro;
            public bool Json, Safe, Shrink, Preview, Yes;
            public List<string> Ids;
        }

        static int UsageError(string msg)
        {
            Err.WriteLine("wslim-cli: " + msg);
            Err.WriteLine("Run 'wslim-cli --help' for usage.");
            return 2;
        }

        static int Run(string[] args)
        {
            var o = new Opts();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "-h": case "--help": case "/?": case "help":
                        Out.WriteLine(Text(string.Format(Usage, Core.Version, Log.LogDir)).Replace("\r\n", "\n"));
                        return 0;
                    case "-V": case "--version":
                        Out.WriteLine("wslim-cli " + Core.Version);
                        return 0;
                    case "--json": o.Json = true; break;
                    case "--safe": o.Safe = true; break;
                    case "--shrink": o.Shrink = true; break;
                    case "--preview": case "--dry-run": o.Preview = true; break;
                    case "-y": case "--yes": o.Yes = true; break;
                    case "--distro": case "-d":
                        if (i + 1 >= args.Length) return UsageError(a + " needs a distribution name");
                        o.Distro = args[++i]; break;
                    case "--ids":
                        if (i + 1 >= args.Length) return UsageError("--ids needs a comma-separated list of item ids");
                        o.Ids = args[++i].Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                        if (o.Ids.Count == 0) return UsageError("--ids needs at least one id");
                        break;
                    default:
                        if (a.StartsWith("-")) return UsageError("unknown option " + a);
                        if (o.Command != null) return UsageError("unexpected argument " + a);
                        o.Command = a; break;
                }
            }
            if (o.Command == null) return UsageError("missing command (scan, clean, shrink or recover)");
            if (o.Command != "scan" && o.Command != "clean" && o.Command != "shrink" && o.Command != "recover") return UsageError("unknown command " + o.Command);
            if (o.Json && o.Command != "scan") return UsageError("--json only works with scan");
            if (o.Command != "clean" && (o.Safe || o.Ids != null || o.Shrink || o.Preview))
                return UsageError("--safe, --ids, --shrink and --preview only work with clean");
            if (o.Safe && o.Ids != null) return UsageError("use either --safe or --ids, not both");

            if (o.Command == "recover" && (o.Distro != null || o.Yes)) return UsageError("recover takes no options");

            Log.Write("wslim-cli " + string.Join(" ", args));
            if (o.Command == "recover") return RecoverCmd();
            // An unfinished shrink blocks everything, and nothing (not even wsl.exe) is run until it is resolved.
            var pending = Journal.Pending();
            if (pending != null)
            {
                Log.Write("Recovery required: an earlier shrink did not end properly (" + Journal.FileIn(Log.Dir) + ").");
                foreach (var l in Compact.RecoveryLines(pending.Disks.Select(d => d.Path), pending.Disks.Any(d => d.AttachedByWslim))) Err.WriteLine(Text(l));
                return Report.ExitCode(Outcome.RecoveryRequired);
            }
            List<Distro> all;
            try { all = Wsl.List(); }
            catch (Exception ex) { Log.Write("Could not read WSL distributions: " + ex.Message); Err.WriteLine("wslim-cli: could not read WSL distributions: " + ex.Message); return 1; }
            var list = all;
            if (o.Distro != null)
            {
                list = all.Where(d => string.Equals(d.Name, o.Distro, StringComparison.OrdinalIgnoreCase)).ToList();
                if (list.Count == 0)
                {
                    Err.WriteLine("wslim-cli: no WSL distribution named '" + o.Distro + "'. Found: " + (all.Count == 0 ? "none" : string.Join(", ", all.Select(d => d.Name))));
                    return 1;
                }
            }
            if (all.Count == 0)
            {
                if (o.Json) { Out.WriteLine("{\"version\":" + J(Core.Version) + ",\"distros\":[]}"); return 0; }
                Out.WriteLine("No WSL distributions found on this PC.");
                return 0;
            }

            if (o.Command == "shrink") return ShrinkCmd(list, o);
            bool scanned = Scan(list);
            if (o.Command == "scan")
            {
                if (o.Json) PrintJson(list); else PrintScan(list);
                return scanned ? 0 : 1;
            }
            return CleanCmd(list, o);
        }

        // ---------------------------------------------------------------- scan
        // Scans every distribution that is not managed by its app. Progress and log lines go to stderr.
        static bool Scan(List<Distro> list)
        {
            var targets = list.Where(d => !d.Managed).ToList();
            if (targets.Count == 0) return true;
            Log.Write("Scan started");
            Log.Sink = line => Muted(Err, "  " + line.Substring(10));
            var agent = Wsl.Agent();
            foreach (var d in targets)
            {
                Muted(Err, "Scanning " + d.Name + (d.Running ? "…" : " (starting it)…"));
                Core.ScanDistro(d, agent);
            }
            Wsl.Refresh(list);
            Log.Sink = null;
            var failed = targets.Where(d => !d.Scanned).ToList();
            foreach (var d in failed) Err.WriteLine(Text("Scan failed: " + (d.Error ?? "could not scan " + d.Name)));
            if (failed.Count > 0) Err.WriteLine(Text("See the log in " + Log.LogDir));
            return failed.Count == 0;
        }

        static void PrintScan(List<Distro> list)
        {
            Out.WriteLine();
            Out.WriteLine("DISKS");
            var rows = new List<string[]>();
            rows.Add(new[] { "NAME", "ON DISK", "USED", "GIVE BACK", "STATE" });
            foreach (var d in list)
            {
                var meta = new List<string>();
                if (d.Os != null) meta.Add(d.Os);
                meta.Add(d.Running ? "running" : "stopped");
                if (d.Managed) meta.Add("managed by its app, shrink only");
                if (d.Version == 1) meta.Add("WSL 1, no virtual disk");
                if (d.Sparse) meta.Add("sparse disk, Windows shrinks it itself");
                long gap = d.FileSize >= 0 && d.Used >= 0 ? Math.Max(0, d.FileSize - d.Used) : -1;
                rows.Add(new[] { d.Name, Size(d.FileSize), Size(d.Used), Size(gap), string.Join(", ", meta) });
            }
            Table(rows, new[] { false, true, true, true, false });

            long safeAll = 0, reviewAll = 0;
            foreach (var d in list.Where(x => x.Scanned || x.Items.Count > 0 || x.Notes.Count > 0))
            {
                Out.WriteLine();
                Out.WriteLine("FOUND IN " + Text(d.Name).ToUpperInvariant());
                if (d.Items.Count == 0) Muted(Out, "  Nothing worth cleaning here.");
                else
                {
                    var r = new List<string[]>();
                    r.Add(new[] { "TIER", "SIZE", "ID", "TITLE" });
                    foreach (var i in d.Items) r.Add(new[] { i.Tier, Size(i.Bytes), i.Id, i.Title });
                    Table(r, new[] { false, true, false, false });
                    long safe = d.Items.Where(i => i.Tier == "safe").Sum(i => i.Bytes);
                    long review = d.Items.Where(i => i.Tier != "safe").Sum(i => i.Bytes);
                    safeAll += safe; reviewAll += review;
                    Muted(Out, "  safe " + Size(safe) + ", review " + Size(review));
                }
                foreach (var n in d.Notes) Out.WriteLine(Text("  note: " + n));
            }
            if (list.Any(d => d.Scanned))
            {
                long reclaim = list.Where(d => d.HasDisk && !d.Sparse && d.Used >= 0 && d.FileSize > 0).Sum(d => Math.Max(0, d.FileSize - d.Used));
                Out.WriteLine();
                Out.WriteLine(Text("Safe to clean: " + Size(safeAll) + ". To review: " + Size(reviewAll) + ". Shrinking gives Windows back about " + Size(reclaim + safeAll) + " after a safe clean."));
                Muted(Out, "Next: wslim-cli clean --preview   (then: wslim-cli clean --shrink)");
                Muted(Out, "Never touched: Docker named volumes, databases, project source code, git data, settings in ~/.config, SSH and GPG keys, sessions and logins.");
            }
        }

        // ---------------------------------------------------------------- clean
        static int CleanCmd(List<Distro> list, Opts o)
        {
            var items = list.SelectMany(d => d.Items).ToList();
            List<Item> picked;
            if (o.Ids != null)
            {
                var want = new HashSet<string>(o.Ids);
                picked = items.Where(i => want.Contains(i.Id)).ToList();
                var missing = o.Ids.Where(id => !items.Any(i => i.Id == id)).ToList();
                if (missing.Count > 0) Err.WriteLine(Text("Not found in the scan (skipped): " + string.Join(", ", missing)));
            }
            else picked = items.Where(i => i.Tier == "safe").ToList();

            bool dockerDesktop = o.Shrink && Core.DockerDesktopRunning();
            var disks = o.Shrink ? Core.ShrinkTargets(list, dockerDesktop) : new List<Distro>();
            if (picked.Count == 0 && (!o.Shrink || disks.Count == 0))
            {
                Out.WriteLine(o.Shrink ? "Nothing to clean and no disk to shrink." : "Nothing to clean.");
                return o.Ids != null ? 1 : 0;
            }

            Out.WriteLine();
            Out.WriteLine("PLAN");
            foreach (var g in picked.GroupBy(i => i.Distro))
            {
                Out.WriteLine(Text("  " + g.Key.Name));
                var r = new List<string[]>();
                foreach (var i in g) r.Add(new[] { i.Tier, Size(i.Bytes), i.Id, i.Title });
                Table(r, new[] { false, true, false, false }, "    ");
            }
            if (picked.Count > 0) Out.WriteLine(Text(string.Format("  Clean {0} item(s), about {1}.", picked.Count, Fmt.Size(picked.Sum(i => i.Bytes)))));
            if (o.Shrink)
            {
                if (disks.Count == 0) Out.WriteLine("  No disk to shrink.");
                else Out.WriteLine(Text("  Then shut WSL down and shrink: " + string.Join(", ", disks.Select(d => d.Name + " (" + Fmt.Size(d.FileSize) + ")"))));
                if (!o.Preview && disks.Count > 0) Consent();
                if (dockerDesktop) Muted(Out, "  Docker Desktop is running, so its disk is skipped. Quit it first to include it.");
            }
            Out.WriteLine();

            if (!o.Preview && !Confirm(o.Yes)) { Out.WriteLine("Cancelled. Nothing was changed."); return 1; }

            return Execute(list, picked, o.Preview, o.Shrink, disks);
        }

        // Runs the shared clean / preview / shrink flow and reports its outcome. The last line and the exit code
        // come from the same outcome as the log line and history.tsv.
        static int Execute(List<Distro> list, List<Item> picked, bool preview, bool shrink, List<Distro> disks)
        {
            Log.Sink = line => Out.WriteLine(Text(line.Substring(10)));
            if (shrink && !preview) Core.LogSparseSkipped(list);
            var r = Operation.Execute(new RealHost(), picked, preview, shrink, disks, Compact.CurrentMode(), Core.CleanDistro);
            Log.Sink = null;
            if (preview) return r.ExitCode;
            Out.WriteLine();
            Out.WriteLine(Text(r.LogLine));
            foreach (var l in r.RecoveryLines) Out.WriteLine(Text(l));
            return r.ExitCode;
        }

        // ---------------------------------------------------------------- shrink
        static void Consent()
        {
            bool ok;
            var running = Wsl.RunningNames(out ok);
            foreach (var l in Compact.ConsentLines(running, ok)) Muted(Out, "  " + l);
        }

        static int RecoverCmd()
        {
            var pending = Journal.Pending();
            if (pending != null)
                foreach (var l in Compact.RecoveryLines(pending.Disks.Select(d => d.Path), pending.Disks.Any(d => d.AttachedByWslim))) Out.WriteLine(Text(l));
            Out.WriteLine();
            Out.WriteLine("Checking (read-only)...");
            var lines = new List<string>();
            bool cleared = Compact.RecoverCheck(new RealHost(), lines);
            foreach (var l in lines) Out.WriteLine(Text("  " + l));
            return cleared ? 0 : Report.ExitCode(Outcome.RecoveryRequired);
        }

        static int ShrinkCmd(List<Distro> list, Opts o)
        {
            bool dockerDesktop = Core.DockerDesktopRunning();
            var disks = Core.ShrinkTargets(list, dockerDesktop);
            Out.WriteLine();
            if (disks.Count == 0)
            {
                Out.WriteLine("No disk to shrink.");
                foreach (var d in list.Where(x => x.Sparse && x.HasDisk)) Muted(Out, "  " + d.Name + ": sparse disk, Windows shrinks it itself.");
                if (dockerDesktop) Muted(Out, "  Docker Desktop is running, so its disk is skipped. Quit it first to include it.");
                return 0;
            }
            Out.WriteLine("PLAN");
            var r = new List<string[]>();
            foreach (var d in disks) r.Add(new[] { d.Name, Size(d.FileSize), d.Running ? "running" : "stopped" });
            Table(r, new[] { false, true, false }, "  ");
            Out.WriteLine("  WSL must stop, then these disks are compacted.");
            Consent();
            if (dockerDesktop) Muted(Out, "  Docker Desktop is running, so its disk is skipped. Quit it first to include it.");
            Out.WriteLine();
            if (!Confirm(o.Yes)) { Out.WriteLine("Cancelled. Nothing was changed."); return 1; }

            return Execute(list, new List<Item>(), false, true, disks);
        }

        static bool Confirm(bool yes)
        {
            if (yes) return true;
            Out.Write("Continue? [y/N] ");
            Out.Flush();
            var answer = Console.In.ReadLine();
            if (answer == null) { Out.WriteLine(); return false; }
            answer = answer.Trim().ToLowerInvariant();
            return answer == "y" || answer == "yes";
        }

        // ---------------------------------------------------------------- output helpers
        static string Size(long b) { return b < 0 ? "-" : Text(Fmt.Size(b)); }

        // Plain characters for a console that may not use UTF-8; pipes and files keep the original text.
        static string Text(string s)
        {
            if (!(outCon || errCon) || s == null) return s;
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '·': sb.Append('-'); break;
                    case '–': case '—': sb.Append('-'); break;
                    case '→': sb.Append("->"); break;
                    case '…': sb.Append("..."); break;
                    case '“': case '”': sb.Append('"'); break;
                    case '‘': case '’': sb.Append('\''); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }

        static readonly object Gate = new object();
        static void Muted(TextWriter w, string s)
        {
            lock (Gate)
            {
                bool dim = (w == Out && outCon) || (w == Err && errCon);
                if (dim) { try { Console.ForegroundColor = ConsoleColor.DarkGray; } catch { dim = false; } }
                w.WriteLine(Text(s));
                if (dim) { try { Console.ResetColor(); } catch { } }
            }
        }

        // Left- or right-aligned columns; the last column is never padded.
        static void Table(List<string[]> rows, bool[] right, string indent = "  ")
        {
            int cols = rows[0].Length;
            var width = new int[cols];
            foreach (var r in rows) for (int c = 0; c < cols; c++) width[c] = Math.Max(width[c], (r[c] ?? "").Length);
            foreach (var r in rows)
            {
                var sb = new StringBuilder(indent);
                for (int c = 0; c < cols; c++)
                {
                    var v = r[c] ?? "";
                    if (c == cols - 1) sb.Append(right[c] ? v.PadLeft(width[c]) : v);
                    else sb.Append(right[c] ? v.PadLeft(width[c]) : v.PadRight(width[c])).Append("   ");
                }
                Out.WriteLine(Text(sb.ToString().TrimEnd()));
            }
        }

        // ---------------------------------------------------------------- JSON
        static void PrintJson(List<Distro> list)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"version\": ").Append(J(Core.Version)).Append(",\n  \"distros\": [");
            bool firstD = true;
            foreach (var d in list)
            {
                sb.Append(firstD ? "\n" : ",\n"); firstD = false;
                long gap = d.FileSize >= 0 && d.Used >= 0 ? Math.Max(0, d.FileSize - d.Used) : -1;
                sb.Append("    {\"name\": ").Append(J(d.Name))
                  .Append(", \"os\": ").Append(J(d.Os))
                  .Append(", \"wslVersion\": ").Append(d.Version)
                  .Append(", \"running\": ").Append(B(d.Running))
                  .Append(", \"managed\": ").Append(B(d.Managed))
                  .Append(", \"sparse\": ").Append(B(d.Sparse))
                  .Append(", \"scanned\": ").Append(B(d.Scanned))
                  .Append(",\n     \"vhd\": ").Append(J(d.VhdPath))
                  .Append(", \"fileBytes\": ").Append(N(d.FileSize))
                  .Append(", \"usedBytes\": ").Append(N(d.Used))
                  .Append(", \"reclaimableBytes\": ").Append(N(gap))
                  .Append(",\n     \"notes\": [").Append(string.Join(", ", d.Notes.Select(J))).Append("]")
                  .Append(",\n     \"items\": [");
                bool firstI = true;
                foreach (var i in d.Items)
                {
                    sb.Append(firstI ? "\n" : ",\n"); firstI = false;
                    sb.Append("       {\"id\": ").Append(J(i.Id))
                      .Append(", \"tier\": ").Append(J(i.Tier))
                      .Append(", \"bytes\": ").Append(i.Bytes.ToString(CultureInfo.InvariantCulture))
                      .Append(", \"title\": ").Append(J(i.Title))
                      .Append(", \"why\": ").Append(J(i.Why))
                      .Append(", \"paths\": [").Append(string.Join(", ", i.Paths.Select(J))).Append("]}");
                }
                sb.Append(d.Items.Count > 0 ? "\n     ]}" : "]}");
            }
            sb.Append(list.Count > 0 ? "\n  ]\n}" : "]\n}");
            Out.WriteLine(sb.ToString());
        }
        static string B(bool b) { return b ? "true" : "false"; }
        static string N(long n) { return n < 0 ? "null" : n.ToString(CultureInfo.InvariantCulture); }
        static string J(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        // Non-ASCII is escaped too, so the JSON survives any console code page.
                        if (ch < 0x20 || ch > 0x7e) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
