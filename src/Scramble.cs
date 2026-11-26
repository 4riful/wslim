// Scramble text effect used while scanning: random glyphs settle, left to right, into real words.
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wslim
{
    class Scramble
    {
        const string Glyphs = "▓▒░█<>/\\_-=+*#$%&01{}[]";
        static readonly Random Rng = new Random();

        readonly TextBlock target;
        readonly DispatcherTimer timer;
        readonly bool resolve;
        string[] words = new string[0];
        int wordIndex, revealed, hold, width;

        // resolve = true: each word settles and holds before the next one.
        // resolve = false: endless noise of the given width (placeholder rows).
        public Scramble(TextBlock target, bool resolve, int intervalMs, int width)
        {
            this.target = target; this.resolve = resolve; this.width = width;
            timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(intervalMs) };
            timer.Tick += (s, e) => Tick();
        }

        public void Start(IList<string> cycle)
        {
            words = new string[cycle.Count]; cycle.CopyTo(words, 0);
            wordIndex = Rng.Next(Math.Max(1, words.Length)); revealed = 0; hold = 0;
            timer.Start();
        }

        public void Stop() { timer.Stop(); }

        void Tick()
        {
            if (!resolve) { target.Text = Noise(width); return; }
            if (words.Length == 0) return;
            var w = words[wordIndex];
            if (revealed >= w.Length)
            {
                target.Text = w;
                if (++hold > 14) { hold = 0; revealed = 0; wordIndex = (wordIndex + 1) % words.Length; }
                return;
            }
            revealed++;
            var sb = new StringBuilder(w.Length);
            sb.Append(w, 0, revealed);
            for (int i = revealed; i < w.Length; i++) sb.Append(w[i] == ' ' ? ' ' : Glyphs[Rng.Next(Glyphs.Length)]);
            target.Text = sb.ToString();
        }

        static string Noise(int n)
        {
            var sb = new StringBuilder(n);
            for (int i = 0; i < n; i++) sb.Append(Rng.Next(9) == 0 ? ' ' : Glyphs[Rng.Next(Glyphs.Length)]);
            return sb.ToString();
        }

        // Builds the scanning placeholder: one settling line plus faint noise rows. Returns the panel and
        // the running scrambles (stop them when the scan ends).
        public static FrameworkElement Placeholder(string distro, Func<string, Brush> brush, List<Scramble> running)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 26, 0, 0) };
            var head = new TextBlock { Text = ("Scanning " + distro).ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = brush("MUTED"), Margin = new Thickness(0, 0, 0, 10) };
            panel.Children.Add(head);
            var line = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 15, Foreground = brush("ACCENT"), Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(line);
            var main = new Scramble(line, true, 32, 0);
            main.Start(new[] { "checking npm cache", "checking pip and uv", "checking Docker leftovers", "checking old tool versions",
                               "checking ~/.cache folders", "checking system journal", "checking package archives",
                               "checking inactive projects", "measuring sizes" });
            running.Add(main);
            int[] widths = { 34, 22, 41, 28 };
            foreach (var w in widths)
            {
                var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var box = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.5), BorderBrush = brush("LINE"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
                var t = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, Foreground = brush("FAINT") };
                var size = new TextBlock { FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, Foreground = brush("FAINT") };
                Grid.SetColumn(t, 1); Grid.SetColumn(size, 2);
                row.Children.Add(box); row.Children.Add(t); row.Children.Add(size);
                panel.Children.Add(row);
                panel.Children.Add(new Border { Height = 1, Background = brush("LINE") });
                var s1 = new Scramble(t, false, 70, w); s1.Start(new string[0]); running.Add(s1);
                var s2 = new Scramble(size, false, 110, 6); s2.Start(new string[0]); running.Add(s2);
            }
            return panel;
        }
    }
}
