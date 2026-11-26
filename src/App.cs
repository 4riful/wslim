// wslim: finds what WSL can safely let go of, cleans it, and shrinks the virtual disks on C:.
// Single file, .NET Framework 4.8 (built into Windows 10/11), C# 5 so the compiler that ships
// with Windows can build it. See README.md.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

[assembly: AssemblyTitle("wslim")]
[assembly: AssemblyProduct("wslim")]
[assembly: AssemblyDescription("Clean WSL safely and shrink its disks")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

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
        public CheckBox Box;
        public bool? Checked;
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

        public static string Agent()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("agent.sh"))
            using (var r = new StreamReader(s)) return r.ReadToEnd().Replace("\r\n", "\n");
        }
    }

    // ------------------------------------------------------------------ logging
    static class Log
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wslim");
        public static readonly string LogDir = Path.Combine(Dir, "logs");
        public static Action<string> Sink;
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

    // ------------------------------------------------------------------ theme
    class Theme
    {
        public bool Dark;
        public Dictionary<string, string> C = new Dictionary<string, string>();
        public Theme()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    Dark = v is int && (int)v == 0;
                }
            }
            catch { }
            if (Dark)
            {
                C["BG"] = "#1E1E1E"; C["SURFACE"] = "#252525"; C["HOVER"] = "#2D2D2D"; C["LINE"] = "#363636";
                C["TEXT"] = "#ECECEC"; C["MUTED"] = "#9B9B9B"; C["FAINT"] = "#6E6E6E";
                C["ACCENT"] = "#4C8DFF"; C["ONACCENT"] = "#FFFFFF"; C["WARN"] = "#E5A54B"; C["GOOD"] = "#5BC489"; C["BAD"] = "#F07070";
            }
            else
            {
                C["BG"] = "#FFFFFF"; C["SURFACE"] = "#F6F6F6"; C["HOVER"] = "#EEEEEE"; C["LINE"] = "#E4E4E4";
                C["TEXT"] = "#1B1B1B"; C["MUTED"] = "#6A6A6A"; C["FAINT"] = "#A0A0A0";
                C["ACCENT"] = "#2463EB"; C["ONACCENT"] = "#FFFFFF"; C["WARN"] = "#A15C07"; C["GOOD"] = "#16803C"; C["BAD"] = "#C62828";
            }
        }
        public Brush B(string key)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(C[key]));
            b.Freeze(); return b;
        }
        public string Apply(string xaml)
        {
            foreach (var kv in C) xaml = xaml.Replace("{" + kv.Key + "}", kv.Value);
            return xaml;
        }
    }

    // ------------------------------------------------------------------ UI
    class MainWindow
    {
        const string Xaml = @"
<Window xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Title='wslim' Width='900' Height='680' MinWidth='720' MinHeight='500' WindowStartupLocation='CenterScreen'
        Background='{BG}' Foreground='{TEXT}' FontFamily='Segoe UI' FontSize='13' UseLayoutRounding='True'
        TextOptions.TextFormattingMode='Display'>
  <Window.Resources>
    <Style x:Key='Btn' TargetType='Button'>
      <Setter Property='Background' Value='{SURFACE}'/><Setter Property='Foreground' Value='{TEXT}'/>
      <Setter Property='BorderBrush' Value='{LINE}'/><Setter Property='BorderThickness' Value='1'/>
      <Setter Property='Padding' Value='16,6'/><Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                  BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='4' Padding='{TemplateBinding Padding}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.85'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style x:Key='Primary' TargetType='Button' BasedOn='{StaticResource Btn}'>
      <Setter Property='Background' Value='{ACCENT}'/><Setter Property='BorderBrush' Value='{ACCENT}'/><Setter Property='Foreground' Value='{ONACCENT}'/>
    </Style>
    <Style x:Key='Link' TargetType='Button'>
      <Setter Property='Foreground' Value='{MUTED}'/><Setter Property='Cursor' Value='Hand'/><Setter Property='Padding' Value='0'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='Button'>
          <ContentPresenter x:Name='c' Margin='{TemplateBinding Padding}'/>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter Property='Foreground' Value='{TEXT}'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='c' Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style TargetType='CheckBox'>
      <Setter Property='Cursor' Value='Hand'/><Setter Property='Foreground' Value='{TEXT}'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='CheckBox'>
          <StackPanel Orientation='Horizontal' Background='Transparent'>
            <Border x:Name='box' Width='16' Height='16' CornerRadius='3' BorderThickness='1.5' BorderBrush='{FAINT}' VerticalAlignment='Center'>
              <Path x:Name='tick' Data='M3.5,8 L6.8,11.2 L12.5,4.8' Stroke='{ONACCENT}' StrokeThickness='1.8' Visibility='Collapsed' Margin='-1.5'/>
            </Border>
            <ContentPresenter Margin='8,0,0,0' VerticalAlignment='Center'/>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='box' Property='Background' Value='{ACCENT}'/><Setter TargetName='box' Property='BorderBrush' Value='{ACCENT}'/>
              <Setter TargetName='tick' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
    <Style TargetType='ScrollBar'>
      <Setter Property='Width' Value='10'/><Setter Property='MinWidth' Value='10'/>
      <Setter Property='Template'><Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Track x:Name='PART_Track' IsDirectionReversed='True'>
            <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'>
              <Border CornerRadius='3' Background='{LINE}' Margin='3,2'/>
            </ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
          </Track>
          <ControlTemplate.Triggers>
            <Trigger Property='Orientation' Value='Horizontal'>
              <Setter Property='Height' Value='10'/><Setter Property='MinHeight' Value='10'/><Setter Property='Width' Value='Auto'/>
              <Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value></Setter>
    </Style>
  </Window.Resources>

  <DockPanel>
    <Grid DockPanel.Dock='Top' Margin='28,22,28,14'>
      <StackPanel>
        <TextBlock Text='wslim' FontSize='20' FontWeight='SemiBold'/>
        <TextBlock x:Name='Sub' Text='Finds what WSL can safely let go of, and shrinks its disks on Windows.' Foreground='{MUTED}' Margin='0,2,0,0'/>
      </StackPanel>
      <Button x:Name='ScanBtn' Style='{StaticResource Btn}' Content='Scan' HorizontalAlignment='Right' VerticalAlignment='Center' MinWidth='88'/>
    </Grid>

    <Border DockPanel.Dock='Bottom' BorderBrush='{LINE}' BorderThickness='0,1,0,0'>
      <Grid Margin='28,12'>
        <Grid.ColumnDefinitions><ColumnDefinition/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>
        <StackPanel VerticalAlignment='Center'>
          <TextBlock x:Name='Status' Text='' Foreground='{MUTED}' TextTrimming='CharacterEllipsis'/>
          <StackPanel Orientation='Horizontal' Margin='0,4,0,0'>
            <Button x:Name='LogBtn' Style='{StaticResource Link}' Content='Show log'/>
            <TextBlock Text='  ·  ' Foreground='{FAINT}'/>
            <Button x:Name='FolderBtn' Style='{StaticResource Link}' Content='Log folder'/>
          </StackPanel>
        </StackPanel>
        <StackPanel Grid.Column='1' Orientation='Horizontal' VerticalAlignment='Center'>
          <CheckBox x:Name='ShrinkChk' Content='Shrink disks afterwards' IsChecked='True' Margin='0,0,20,0' VerticalAlignment='Center'/>
          <Button x:Name='PreviewBtn' Style='{StaticResource Btn}' Content='Preview' Margin='0,0,8,0' MinWidth='88'/>
          <Button x:Name='CleanBtn' Style='{StaticResource Primary}' Content='Clean' MinWidth='110'/>
        </StackPanel>
      </Grid>
    </Border>

    <Border x:Name='LogPane' DockPanel.Dock='Bottom' Height='190' Visibility='Collapsed' BorderBrush='{LINE}' BorderThickness='0,1,0,0' Background='{SURFACE}'>
      <TextBox x:Name='LogBox' IsReadOnly='True' BorderThickness='0' Background='Transparent' Foreground='{MUTED}' Padding='24,10'
               FontFamily='Cascadia Mono, Consolas' FontSize='12' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Auto'/>
    </Border>

    <ScrollViewer VerticalScrollBarVisibility='Auto' Focusable='False'>
      <StackPanel x:Name='Body' Margin='28,4,28,24'/>
    </ScrollViewer>
  </DockPanel>
</Window>";

        readonly Theme T = new Theme();
        readonly Window W;
        readonly StackPanel Body;
        readonly TextBox LogBox;
        readonly TextBlock Status;
        readonly Button ScanBtn, CleanBtn, PreviewBtn, LogBtn, FolderBtn;
        readonly CheckBox ShrinkChk;
        readonly Border LogPane;
        List<Distro> Distros = new List<Distro>();
        bool busy;
        string resultBanner;
        public readonly ManualResetEventSlim ScanDone = new ManualResetEventSlim(false);

        public Window Window { get { return W; } }

        public MainWindow()
        {
            W = (Window)XamlReader.Parse(T.Apply(Xaml));
            Body = (StackPanel)W.FindName("Body");
            LogBox = (TextBox)W.FindName("LogBox");
            Status = (TextBlock)W.FindName("Status");
            ScanBtn = (Button)W.FindName("ScanBtn");
            CleanBtn = (Button)W.FindName("CleanBtn");
            PreviewBtn = (Button)W.FindName("PreviewBtn");
            LogBtn = (Button)W.FindName("LogBtn");
            FolderBtn = (Button)W.FindName("FolderBtn");
            ShrinkChk = (CheckBox)W.FindName("ShrinkChk");
            LogPane = (Border)W.FindName("LogPane");

            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("wslim.ico"))
                    if (s != null) W.Icon = BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch { }

            Log.Sink = line => W.Dispatcher.BeginInvoke(new Action(() =>
            {
                LogBox.AppendText((LogBox.Text.Length > 0 ? Environment.NewLine : "") + line);
                LogBox.ScrollToEnd();
            }));

            ScanBtn.Click += (s, e) => Scan();
            CleanBtn.Click += (s, e) => Clean(false);
            PreviewBtn.Click += (s, e) => Clean(true);
            LogBtn.Click += (s, e) => ShowLog(LogPane.Visibility != Visibility.Visible);
            FolderBtn.Click += (s, e) => { Directory.CreateDirectory(Log.LogDir); Process.Start("explorer.exe", "\"" + Log.LogDir + "\""); };
            ShrinkChk.Click += (s, e) => UpdateFooter();
            W.SourceInitialized += (s, e) => DarkTitleBar();
            W.Closing += (s, e) =>
            {
                if (busy && MessageBox.Show(W, "wslim is still working. Closing now may leave a disk half shrunk.\n\nClose anyway?",
                        "wslim", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) e.Cancel = true;
            };
            W.Loaded += (s, e) => Start();
        }

        void DarkTitleBar()
        {
            if (!T.Dark) return;
            try { int on = 1; var h = new WindowInteropHelper(W).Handle; if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4); }
            catch { }
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
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void ShowLog(bool show)
        {
            LogPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LogBtn.Content = show ? "Hide log" : "Show log";
        }

        void Ui(Action a) { W.Dispatcher.BeginInvoke(a); }

        void SetBusy(bool b, string status)
        {
            busy = b;
            ScanBtn.IsEnabled = CleanBtn.IsEnabled = PreviewBtn.IsEnabled = ShrinkChk.IsEnabled = !b;
            foreach (var d in Distros) foreach (var i in d.Items) if (i.Box != null) i.Box.IsEnabled = !b;
            if (status != null) Status.Text = status;
            W.Cursor = b ? Cursors.AppStarting : null;
        }

        // ---------------------------------------------------------------- flow
        void Start()
        {
            Log.Write("wslim started");
            try { Distros = Wsl.List(); }
            catch (Exception ex) { Log.Write("Could not read WSL distributions: " + ex.Message); }
            Render();
            var last = Log.LastHistory();
            Status.Text = Distros.Count == 0 ? "No WSL distributions found on this PC."
                        : (last ?? "Press Scan to see what can be cleaned. Nothing changes until you press Clean.");
            if (Distros.Any(d => d.Running && !d.Managed)) Scan();
            else ScanDone.Set();
        }

        async void Scan()
        {
            if (busy) return;
            var targets = Distros.Where(d => !d.Managed).ToList();
            if (targets.Count == 0) { ScanDone.Set(); return; }
            resultBanner = null;
            SetBusy(true, "Scanning…");
            Log.Write("Scan started");
            var agent = Wsl.Agent();
            try { await Task.Run(() =>
            {
                foreach (var d in targets)
                {
                    var dd = d;
                    Ui(() => Status.Text = "Scanning " + dd.Name + (dd.Running ? "…" : " (starting it)…"));
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
                }
                Wsl.Refresh(Distros);
            }); }
            catch (Exception ex) { Log.Crash(ex); Log.Write("Error: " + ex.Message); }
            Render();
            SetBusy(false, null);
            UpdateFooter();
            if (Distros.Any(x => !x.Managed && !x.Scanned)) Status.Text = "Some distributions could not be scanned. See the log.";
            ScanDone.Set();
        }

        async void Clean(bool preview)
        {
            if (busy) return;
            var picked = Distros.SelectMany(d => d.Items).Where(i => i.Box != null && i.Box.IsChecked == true).ToList();
            bool shrink = !preview && ShrinkChk.IsChecked == true;
            var disks = Distros.Where(d => d.HasDisk && !d.Sparse).ToList();
            if (picked.Count == 0 && !shrink)
            {
                Status.Text = preview ? "Select something to preview." : "Nothing selected. Tick items, or turn on “Shrink disks afterwards”.";
                return;
            }
            bool dockerDesktop = Process.GetProcessesByName("Docker Desktop").Length > 0;
            if (shrink && dockerDesktop) disks = disks.Where(d => !d.Managed).ToList();

            if (!preview)
            {
                var msg = new StringBuilder();
                if (picked.Count > 0) msg.AppendLine(string.Format("Clean {0} item(s), about {1}.", picked.Count, Fmt.Size(picked.Sum(i => i.Bytes))));
                if (shrink)
                {
                    msg.AppendLine();
                    msg.AppendLine("Then WSL shuts down and its disks are shrunk. Claude Code, VS Code, terminals and containers running in WSL will close, so save your work first.");
                    msg.AppendLine();
                    msg.AppendLine("Windows will ask for administrator permission (needed to shrink the disks).");
                    if (dockerDesktop) { msg.AppendLine(); msg.AppendLine("Docker Desktop is running, so its disk is skipped. Quit it first to include it."); }
                }
                if (MessageBox.Show(W, msg.ToString().Trim(), "wslim", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            }

            ShowLog(true);
            SetBusy(true, preview ? "Previewing…" : "Cleaning…");
            var before = Distros.ToDictionary(d => d, d => d.FileSize);
            long freedInside = 0;
            bool failed = false;
            var okDistros = new HashSet<Distro>();

            try { await Task.Run(() =>
            {
                Log.Write(preview ? "Preview (nothing is changed)" : "Clean started");
                foreach (var g in picked.GroupBy(i => i.Distro))
                {
                    var d = g.Key;
                    Log.Write("— " + d.Name + ": " + string.Join(", ", g.Select(i => i.Title)));
                    var ids = string.Join(" ", g.Select(i => i.Id));
                    var agent = Wsl.Agent(g);
                    int rc;
                    try { rc = Wsl.Run("wsl.exe", "-d " + d.Name + " -u root -- sh -s " + (preview ? "preview " : "clean ") + ids, agent, line =>
                    {
                        var f = line.Split('\t');
                        if (f[0] == "LOG" && f.Length >= 2) Log.Write(f[1]);
                        else if (f[0] == "INFO" && f.Length >= 3 && f[1] == "freed") { long b; if (long.TryParse(f[2], out b)) Interlocked.Add(ref freedInside, Math.Max(0, b)); }
                        else if (f[0] == "INFO" && f.Length >= 3 && f[1] == "used") { long b; if (long.TryParse(f[2], out b)) d.Used = b; }
                        else if (f[0] == "ERR") Log.Write("  " + line.Substring(4));
                        else if (line.Trim().Length > 0) Log.Write("  " + line.Trim());
                    }, 3600000); }
                    catch (Exception ex) { rc = -1; Log.Write(d.Name + ": " + ex.Message); }
                    if (rc != 0) { failed = true; Log.Write(d.Name + ": cleanup exited with code " + rc); }
                    else lock (okDistros) okDistros.Add(d);
                }
                if (preview)
                {
                    if (ShrinkWanted()) Log.Write("Would then shut WSL down and shrink: " + string.Join(", ", disks.Select(d => d.Name)));
                    Log.Write("Preview finished. Nothing was changed.");
                    return;
                }
                if (picked.Count > 0) Log.Write("Freed inside WSL: " + Fmt.Size(freedInside));
                if (shrink && disks.Count > 0) { if (!Shrink(disks)) failed = true; }
                foreach (var d in Distros.Where(x => x.Sparse && x.HasDisk))
                    Log.Write(d.Name + ": disk is a sparse file, which Windows shrinks by itself; skipped.");
                Wsl.Refresh(Distros);
            }); }
            catch (Exception ex) { Log.Crash(ex); Log.Write("Error: " + ex.Message); }

            if (!preview)
            {
                long b = before.Where(kv => kv.Value > 0).Sum(kv => kv.Value);
                long a = Distros.Where(d => before.ContainsKey(d) && before[d] > 0).Sum(d => d.FileSize);
                var summary = shrink
                    ? string.Format("disks {0} → {1}, freed {2} on C:", Fmt.Size(b), Fmt.Size(a), Fmt.Size(Math.Max(0, b - a)))
                    : string.Format("freed {0} inside WSL", Fmt.Size(freedInside));
                Log.Write("Done: " + summary);
                Log.History(summary + (failed ? " (with errors)" : ""));
                resultBanner = (failed ? "Finished with errors · " : "Done · ") + summary;
                foreach (var d in Distros) { var dd = d; if (okDistros.Contains(dd)) dd.Items = dd.Items.Where(i => !picked.Contains(i)).ToList(); }
                Render();
            }
            SetBusy(false, preview ? "Preview finished. Nothing was changed." : resultBanner);
            if (!preview) UpdateFooter(resultBanner);
        }

        bool ShrinkWanted() { bool r = false; W.Dispatcher.Invoke(new Action(() => r = ShrinkChk.IsChecked == true)); return r; }

        // Shuts WSL down and runs diskpart elevated (one UAC prompt for all disks).
        bool Shrink(List<Distro> disks)
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

        // ---------------------------------------------------------------- rendering
        TextBlock Text(string s, string color, double size = 13, FontWeight? weight = null)
        {
            var t = new TextBlock { Text = s, Foreground = T.B(color), FontSize = size, TextWrapping = TextWrapping.Wrap };
            if (weight.HasValue) t.FontWeight = weight.Value;
            return t;
        }
        void Heading(string s, double top)
        {
            var t = Text(s.ToUpperInvariant(), "MUTED", 11, FontWeights.SemiBold);
            t.Margin = new Thickness(0, top, 0, 8);
            Body.Children.Add(t);
        }
        void Rule()
        {
            Body.Children.Add(new Border { Height = 1, Background = T.B("LINE"), Margin = new Thickness(0, 0, 0, 0) });
        }

        void Render()
        {
            Body.Children.Clear();

            if (resultBanner != null)
            {
                var banner = Text(resultBanner, resultBanner.StartsWith("Done") ? "GOOD" : "WARN", 13, FontWeights.SemiBold);
                banner.Margin = new Thickness(0, 6, 0, 0);
                Body.Children.Add(banner);
            }

            Heading("Disks", 14);
            Rule();
            if (Distros.Count == 0)
                Body.Children.Add(Text("WSL is not installed, or no distributions are registered for this user.", "MUTED"));
            foreach (var d in Distros) Body.Children.Add(DiskRow(d));

            foreach (var d in Distros.Where(x => x.Scanned || x.Items.Count > 0 || x.Notes.Count > 0))
            {
                Heading("Found in " + d.Name, 26);
                var safe = d.Items.Where(i => i.Tier == "safe").ToList();
                var review = d.Items.Where(i => i.Tier != "safe").ToList();
                if (safe.Count == 0 && review.Count == 0)
                    Body.Children.Add(Text("Nothing worth cleaning here.", "MUTED"));
                Group("Safe to clean", "Rebuilds itself when needed", safe, true);
                Group("Review first", "Can be rebuilt, but costs time or a large download", review, false);
                foreach (var n in d.Notes) { var t = Text(n, "WARN", 12); t.Margin = new Thickness(0, 10, 0, 0); Body.Children.Add(t); }
            }

            if (Distros.Any(d => d.Scanned))
            {
                var never = Text("Never touched: Docker named volumes, databases, project source code, git data, settings in ~/.config, SSH and GPG keys, sessions and logins.", "FAINT", 12);
                never.Margin = new Thickness(0, 22, 0, 0);
                Body.Children.Add(never);
            }
            UpdateFooter();
        }

        FrameworkElement DiskRow(Distro d)
        {
            var g = new Grid { Margin = new Thickness(0, 10, 0, 10) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel();
            left.Children.Add(Text(d.Name, "TEXT", 13, FontWeights.SemiBold));
            var meta = new List<string>();
            if (d.Os != null) meta.Add(d.Os);
            meta.Add(d.Running ? "running" : "stopped");
            if (d.Managed) meta.Add("managed by its app, shrink only");
            if (d.Version == 1) meta.Add("WSL 1, no virtual disk");
            if (d.Sparse) meta.Add("sparse disk, Windows shrinks it itself");
            left.Children.Add(Text(string.Join(" · ", meta), "MUTED", 12));
            g.Children.Add(left);

            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(right, 1);
            if (d.FileSize >= 0)
            {
                var line = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right };
                line.Inlines.Add(new System.Windows.Documents.Run(Fmt.Size(d.FileSize)) { FontWeight = FontWeights.SemiBold, Foreground = T.B("TEXT") });
                line.Inlines.Add(new System.Windows.Documents.Run(" on disk") { Foreground = T.B("MUTED") });
                right.Children.Add(line);
                if (d.Used >= 0)
                {
                    long gap = Math.Max(0, d.FileSize - d.Used);
                    var sub = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, FontSize = 12 };
                    sub.Inlines.Add(new System.Windows.Documents.Run(Fmt.Size(d.Used) + " used · ") { Foreground = T.B("MUTED") });
                    sub.Inlines.Add(new System.Windows.Documents.Run(Fmt.Size(gap) + " can be given back") { Foreground = T.B(gap > (1L << 30) ? "GOOD" : "MUTED") });
                    right.Children.Add(sub);
                }
            }
            g.Children.Add(right);
            var wrap = new StackPanel();
            wrap.Children.Add(g);
            wrap.Children.Add(new Border { Height = 1, Background = T.B("LINE") });
            return wrap;
        }

        void Group(string title, string hint, List<Item> items, bool checkedByDefault)
        {
            if (items.Count == 0) return;
            var head = new Grid { Margin = new Thickness(0, 6, 0, 2) };
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ht = new TextBlock();
            ht.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold, Foreground = T.B("TEXT") });
            ht.Inlines.Add(new System.Windows.Documents.Run("   " + hint) { Foreground = T.B("FAINT"), FontSize = 12 });
            head.Children.Add(ht);
            var total = Text(Fmt.Size(items.Sum(i => i.Bytes)), "MUTED", 12);
            total.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(total, 1);
            head.Children.Add(total);
            Body.Children.Add(head);
            foreach (var i in items) Body.Children.Add(ItemRow(i, checkedByDefault));
        }

        FrameworkElement ItemRow(Item i, bool on)
        {
            var row = new Border { Padding = new Thickness(0, 8, 0, 8), BorderBrush = T.B("LINE"), BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var box = new CheckBox { IsChecked = i.Checked.HasValue ? i.Checked.Value : on, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            box.Click += (s, e) => { i.Checked = box.IsChecked == true; UpdateFooter(); };
            i.Box = box;
            var mid = new StackPanel();
            mid.Children.Add(Text(i.Title, "TEXT"));
            mid.Children.Add(Text(i.Why, "MUTED", 12));
            Grid.SetColumn(mid, 1);
            var size = Text(Fmt.Size(i.Bytes), "TEXT", 13);
            size.HorizontalAlignment = HorizontalAlignment.Right; size.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(size, 2);
            g.Children.Add(box); g.Children.Add(mid); g.Children.Add(size);
            row.Child = g;
            if (i.Paths.Length > 0)
            {
                var tip = string.Join("\n", i.Paths.Take(12)) + (i.Paths.Length > 12 ? "\n… and " + (i.Paths.Length - 12) + " more" : "");
                row.ToolTip = tip;
                ToolTipService.SetInitialShowDelay(row, 500);
            }
            row.MouseEnter += (s, e) => row.Background = T.B("HOVER");
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonUp += (s, e) =>
            {
                if (!box.IsEnabled || e.OriginalSource is Path || IsInside(e.OriginalSource as DependencyObject, box)) return;
                box.IsChecked = box.IsChecked != true; i.Checked = box.IsChecked == true; UpdateFooter();
            };
            return row;
        }

        static bool IsInside(DependencyObject o, DependencyObject parent)
        {
            while (o != null) { if (o == parent) return true; o = o is Visual ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o); }
            return false;
        }

        void UpdateFooter(string text = null)
        {
            if (busy) return;
            var picked = Distros.SelectMany(d => d.Items).Where(i => i.Box != null && i.Box.IsChecked == true).ToList();
            CleanBtn.Content = picked.Count > 0 ? "Clean " + Fmt.Size(picked.Sum(i => i.Bytes))
                             : (ShrinkChk.IsChecked == true ? "Shrink disks" : "Clean");
            if (text != null) Status.Text = text;
            else if (Distros.Any(d => d.Scanned))
            {
                long sel = picked.Sum(i => i.Bytes);
                if (ShrinkChk.IsChecked == true)
                {
                    long reclaim = Distros.Where(d => d.HasDisk && !d.Sparse && d.Used >= 0 && d.FileSize > 0).Sum(d => Math.Max(0, d.FileSize - d.Used));
                    Status.Text = string.Format("{0} selected · Windows gets back about {1} after shrinking", Fmt.Size(sel), Fmt.Size(reclaim + sel));
                }
                else Status.Text = string.Format("{0} selected · freed inside WSL only; the disk file on Windows keeps its size until you shrink it", Fmt.Size(sel));
            }
        }

        // Renders the window to a PNG (used by the build check: wslim.exe --screenshot file.png).
        public void Screenshot(string path)
        {
            W.UpdateLayout();
            var src = (FrameworkElement)W.Content;
            var bmp = new RenderTargetBitmap((int)src.ActualWidth, (int)src.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(T.B("BG"), null, new Rect(0, 0, src.ActualWidth, src.ActualHeight));
                dc.DrawRectangle(new VisualBrush(src), null, new Rect(0, 0, src.ActualWidth, src.ActualHeight));
            }
            bmp.Render(dv);
            var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(path)) enc.Save(fs);
        }
    }

    // ------------------------------------------------------------------ entry
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Crash(e.ExceptionObject as Exception ?? new Exception("unknown"));
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Crash(e.Exception);
                Log.Write("Unexpected error: " + e.Exception.Message);
                MessageBox.Show("Something went wrong: " + e.Exception.Message + "\n\nDetails were saved to " + Path.Combine(Log.Dir, "crash.log") + ". The app keeps running.",
                    "wslim", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            };
            MainWindow mw;
            try { mw = new MainWindow(); }
            catch (Exception ex)
            {
                Log.Crash(ex);
                MessageBox.Show("wslim could not start: " + ex.Message + "\n\nDetails: " + Path.Combine(Log.Dir, "crash.log"), "wslim", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }

            int shot = Array.IndexOf(args, "--screenshot");
            if (shot >= 0 && shot + 1 < args.Length)
            {
                var file = args[shot + 1];
                mw.Window.ShowInTaskbar = false; mw.Window.WindowStartupLocation = WindowStartupLocation.Manual;
                mw.Window.Left = -20000; mw.Window.Top = -20000;
                mw.Window.ContentRendered += (s, e) => Task.Run(() =>
                {
                    mw.ScanDone.Wait(TimeSpan.FromMinutes(5));
                    Thread.Sleep(500);
                    mw.Window.Dispatcher.Invoke(new Action(() => { mw.Screenshot(file); mw.Window.Close(); }));
                });
            }
            return app.Run(mw.Window);
        }
    }
}
