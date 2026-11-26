// wslim: finds what WSL can safely let go of, cleans it, and shrinks the virtual disks on C:.
// The window (WPF). Scanning, cleaning and shrinking live in Core.cs, shared with wslim-cli.exe (Cli.cs).
// .NET Framework 4.8 (built into Windows 10/11), C# 5 so the compiler that ships with Windows can build it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace Wslim
{
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
                C["ACCENT"] = "#2DD4BF"; C["ONACCENT"] = "#04201D"; C["WARN"] = "#E5A54B"; C["GOOD"] = "#5BC489"; C["BAD"] = "#F07070";
            }
            else
            {
                C["BG"] = "#FFFFFF"; C["SURFACE"] = "#F6F6F6"; C["HOVER"] = "#EEEEEE"; C["LINE"] = "#E4E4E4";
                C["TEXT"] = "#1B1B1B"; C["MUTED"] = "#6A6A6A"; C["FAINT"] = "#A0A0A0";
                C["ACCENT"] = "#0D9488"; C["ONACCENT"] = "#FFFFFF"; C["WARN"] = "#A15C07"; C["GOOD"] = "#16803C"; C["BAD"] = "#C62828";
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
        public bool Unattended;   // screenshot mode: never block on dialogs

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
                if (busy && !Unattended && MessageBox.Show(W, "wslim is still working. Closing now may leave a disk half shrunk.\n\nClose anyway?",
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
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void ShowLog(bool show)
        {
            LogPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            LogBtn.Content = show ? "Hide log" : "Show log";
        }

        void Ui(Action a) { W.Dispatcher.BeginInvoke(a); }

        static bool Ticked(Item i) { var box = i.Box as CheckBox; return box != null && box.IsChecked == true; }

        void SetBusy(bool b, string status)
        {
            busy = b;
            ScanBtn.IsEnabled = CleanBtn.IsEnabled = PreviewBtn.IsEnabled = ShrinkChk.IsEnabled = !b;
            foreach (var d in Distros) foreach (var i in d.Items) { var box = i.Box as CheckBox; if (box != null) box.IsEnabled = !b; }
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
            foreach (var d in targets) { d.Items = new List<Item>(); d.Notes = new List<string>(); d.Scanned = false; }
            Render();
            var scrambles = new List<Scramble>();
            Body.Children.Add(Scramble.Placeholder(string.Join(", ", targets.Select(t => t.Name)), k => T.B(k), scrambles));
            var agent = Wsl.Agent();
            var watch = Stopwatch.StartNew();
            try { await Task.Run(() =>
            {
                foreach (var d in targets)
                {
                    var dd = d;
                    Ui(() => Status.Text = "Scanning " + dd.Name + (dd.Running ? "…" : " (starting it)…"));
                    Core.ScanDistro(d, agent);
                }
                Wsl.Refresh(Distros);
            }); }
            catch (Exception ex) { Log.Crash(ex); Log.Write("Error: " + ex.Message); }
            if (watch.ElapsedMilliseconds < 1600) await Task.Delay(1600 - (int)watch.ElapsedMilliseconds);   // let the scan view settle, never flash
            foreach (var sc in scrambles) sc.Stop();
            Render();
            SetBusy(false, null);
            UpdateFooter();
            if (Distros.Any(x => !x.Managed && !x.Scanned)) Status.Text = "Some distributions could not be scanned. See the log.";
            ScanDone.Set();
        }

        async void Clean(bool preview)
        {
            if (busy) return;
            var picked = Distros.SelectMany(d => d.Items).Where(Ticked).ToList();
            bool shrink = !preview && ShrinkChk.IsChecked == true;
            if (picked.Count == 0 && !shrink)
            {
                Status.Text = preview ? "Select something to preview." : "Nothing selected. Tick items, or turn on “Shrink disks afterwards”.";
                return;
            }
            bool dockerDesktop = Core.DockerDesktopRunning();
            var disks = Core.ShrinkTargets(Distros, shrink && dockerDesktop);

            if (!preview)
            {
                var msg = new StringBuilder();
                if (picked.Count > 0) msg.AppendLine(string.Format("Clean {0} item(s), about {1}.", picked.Count, Fmt.Size(picked.Sum(i => i.Bytes))));
                if (shrink)
                {
                    msg.AppendLine();
                    var running = Wsl.RunningNames();
                    msg.AppendLine("Then WSL must stop so its disks can be shrunk." + (running.Count > 0 ? " Running now: " + string.Join(", ", running) + "." : " No distro is running right now."));
                    msg.AppendLine("Claude Code, VS Code, terminals and containers running in WSL will close, so save your work first.");
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
                    int rc = Core.CleanDistro(d, g.ToList(), preview, b => Interlocked.Add(ref freedInside, b));
                    if (rc != 0) failed = true;
                    else lock (okDistros) okDistros.Add(d);
                }
                if (preview)
                {
                    if (ShrinkWanted()) Log.Write("Would then shut WSL down and shrink: " + string.Join(", ", disks.Select(d => d.Name)));
                    Log.Write("Preview finished. Nothing was changed.");
                    return;
                }
                if (picked.Count > 0) Log.Write("Freed inside WSL: " + Fmt.Size(freedInside));
                if (shrink && disks.Count > 0) { if (!Core.Shrink(disks)) failed = true; }
                Core.LogSparseSkipped(Distros);
                Wsl.Refresh(Distros);
            }); }
            catch (Exception ex) { Log.Crash(ex); Log.Write("Error: " + ex.Message); }

            if (!preview)
            {
                var summary = Core.Summary(shrink, before, Distros, freedInside);
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
            var picked = Distros.SelectMany(d => d.Items).Where(Ticked).ToList();
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
                mw.Unattended = true; mw.Window.ShowInTaskbar = false; mw.Window.WindowStartupLocation = WindowStartupLocation.Manual;
                mw.Window.Left = -20000; mw.Window.Top = -20000;
                mw.Window.ContentRendered += (s, e) => Task.Run(() =>
                {
                    if (Array.IndexOf(args, "--during-scan") >= 0) Thread.Sleep(900); else mw.ScanDone.Wait(TimeSpan.FromMinutes(5));
                    Thread.Sleep(500);
                    mw.Window.Dispatcher.Invoke(new Action(() => { mw.Screenshot(file); mw.Window.Close(); }));
                });
            }
            return app.Run(mw.Window);
        }
    }
}
