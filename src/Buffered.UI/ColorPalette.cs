using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Buffered.UI;

/// <summary>
/// The color picker used for folders and webhooks: presets, a color square with a hue slider, hex and RGB boxes, and the recent colors.
/// Any color can be chosen, including black. It is applied when the picker closes (a preset applies at once).
/// </summary>
internal static class ColorPalette
{
    /// <summary>The settings, where the recent colors are kept. Set once at start-up.</summary>
    public static Settings? Settings;

    private static readonly string[] Presets =
    {
        "#7FA6FF", "#9B8CF0", "#5ED0A0", "#F0A56C", "#E07FB0", "#C9C46A", "#F0A0A0", "#8FD3D9",
        "#4F7DF0", "#7C5CE0", "#2FB67C", "#E8833A", "#D44E95", "#B8A92E", "#E05252", "#3CB8C4",
    };

    public static SolidColorBrush Brush(string hex)
    {
        try { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; }
        catch { return new SolidColorBrush(Color.FromRgb(0xC9, 0xC4, 0x6A)); }
    }

    /// <summary>A readable text color to put on top of this color: dark on light colors, white on dark ones.</summary>
    public static Brush TextOn(Color c)
    {
        double L(byte x) { double v = x / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        double lum = 0.2126 * L(c.R) + 0.7152 * L(c.G) + 0.0722 * L(c.B);
        return lum > 0.4 ? new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1A)) : Brushes.White;
    }

    // ---- color math -----------------------------------------------------------

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn, h = 0;
        if (d > 0)
        {
            if (mx == r) h = ((g - b) / d) % 6; else if (mx == g) h = (b - r) / d + 2; else h = (r - g) / d + 4;
            h *= 60; if (h < 0) h += 360;
        }
        return (h, mx == 0 ? 0 : d / mx, mx);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double F(int n) { double k = (n + h / 60) % 6; return v - v * s * Math.Max(0, Math.Min(Math.Min(k, 4 - k), 1)); }
        return Color.FromRgb((byte)Math.Round(F(5) * 255), (byte)Math.Round(F(3) * 255), (byte)Math.Round(F(1) * 255));
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool TryHex(string text, out Color c)
    {
        c = default;
        text = text.Trim().TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out int v)) return false;
        c = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    // ---- the picker -------------------------------------------------------------

    /// <summary>
    /// Shows the picker under <paramref name="target"/>. <paramref name="chosen"/> gets the final color when the picker closes (or null for
    /// "no color" when that is allowed).
    /// </summary>
    public static void Show(FrameworkElement target, string title, string? current, bool allowNone, Action<string?> chosen)
    {
        var popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade };

        Color start = current is not null && TryHex(current, out var parsed) ? parsed : Color.FromRgb(0x7F, 0xA6, 0xFF);
        var (h, s, v) = ToHsv(start);
        bool dirty = false, none = false, syncing = false;
        Color Now() => FromHsv(h, s, v);

        // the preview: a clip with the selection ring and check circle in the chosen color
        var ring = new Border { Width = 64, Height = 42, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(3), Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x52, 0x70)) };
        var disc = new Ellipse { Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4) };
        ring.Child = disc;

        // color square: the hue, white from the left, black from the bottom
        var hueFill = new Rectangle();
        var whiteFade = new Rectangle { Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) };
        var blackFade = new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) };
        var svKnob = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        var svCanvas = new Canvas { IsHitTestVisible = false };
        svCanvas.Children.Add(svKnob);
        var sv = new Grid { Width = 300, Height = 150, Cursor = Cursors.Cross, Background = Brushes.Transparent, ClipToBounds = true, Children = { hueFill, whiteFade, blackFade, svCanvas } };

        var hueBar = new Border
        {
            Width = 300, Height = 14, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand, Background = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Colors.Red, 0), new GradientStop(Colors.Yellow, 1.0 / 6), new GradientStop(Colors.Lime, 2.0 / 6), new GradientStop(Colors.Cyan, 3.0 / 6),
                new GradientStop(Colors.Blue, 4.0 / 6), new GradientStop(Colors.Magenta, 5.0 / 6), new GradientStop(Colors.Red, 1),
            }, 0),
        };
        var hueKnob = new Ellipse { Width = 16, Height = 16, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -1, 0, 0) };
        var hueHost = new Grid { Width = 300, Height = 16, Margin = new Thickness(0, 10, 0, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        hueBar.Margin = new Thickness(0, 1, 0, 0);
        hueHost.Children.Add(hueBar); hueHost.Children.Add(hueKnob);

        TextBox Box(string name, int max, double width)
        {
            var t = new TextBox { MaxLength = max, Height = 32, Width = width, VerticalContentAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas") };
            System.Windows.Automation.AutomationProperties.SetName(t, name);
            return t;
        }
        var hexBox = Box("Hex color", 7, 92);
        var rBox = Box("Red", 3, 56); var gBox = Box("Green", 3, 56); var bBox = Box("Blue", 3, 56);

        void Refresh()
        {
            var c = Now();
            syncing = true;
            hueFill.Fill = new SolidColorBrush(FromHsv(h, 1, 1));
            Canvas.SetLeft(svKnob, s * 300 - 8); Canvas.SetTop(svKnob, (1 - v) * 150 - 8);
            svKnob.Fill = new SolidColorBrush(c);
            hueKnob.Margin = new Thickness(h / 360 * 284, -1, 0, 0);
            hueKnob.Fill = new SolidColorBrush(FromHsv(h, 1, 1));
            ring.BorderBrush = none ? Brushes.Transparent : new SolidColorBrush(c);
            disc.Fill = none ? Brushes.Transparent : new SolidColorBrush(c);
            if (!hexBox.IsKeyboardFocused) hexBox.Text = none ? "" : Hex(c);
            if (!rBox.IsKeyboardFocused) rBox.Text = none ? "" : c.R.ToString();
            if (!gBox.IsKeyboardFocused) gBox.Text = none ? "" : c.G.ToString();
            if (!bBox.IsKeyboardFocused) bBox.Text = none ? "" : c.B.ToString();
            syncing = false;
        }

        void Pick(Color c) { (h, s, v) = ToHsv(c); none = false; dirty = true; Refresh(); }

        void Drag(UIElement el, Action<Point> move, Func<UIElement, Point> pos)
        {
            el.MouseLeftButtonDown += (_, e) => { el.CaptureMouse(); move(pos(el)); };
            el.MouseMove += (_, e) => { if (el.IsMouseCaptured) move(pos(el)); };
            el.MouseLeftButtonUp += (_, _) => el.ReleaseMouseCapture();
        }
        Drag(sv, p => { s = Math.Clamp(p.X / 300, 0, 1); v = 1 - Math.Clamp(p.Y / 150, 0, 1); none = false; dirty = true; Refresh(); }, el => Mouse.GetPosition(el));
        Drag(hueHost, p => { h = Math.Clamp((p.X - 8) / 284, 0, 1) * 359.9; none = false; dirty = true; Refresh(); }, el => Mouse.GetPosition(el));

        hexBox.TextChanged += (_, _) => { if (!syncing && hexBox.IsKeyboardFocused && TryHex(hexBox.Text, out var c)) Pick(c); };
        void FromRgb()
        {
            if (syncing) return;
            if (byte.TryParse(rBox.Text, out var r) && byte.TryParse(gBox.Text, out var g) && byte.TryParse(bBox.Text, out var b)) Pick(Color.FromRgb(r, g, b));
        }
        foreach (var box in new[] { rBox, gBox, bBox })
        {
            box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
            box.TextChanged += (_, _) => FromRgb();
            DataObject.AddPastingHandler(box, (_, e) => { if (e.DataObject.GetData(DataFormats.Text) is not string t || !t.All(char.IsDigit)) e.CancelCommand(); });
        }
        rBox.LostFocus += (_, _) => Refresh(); gBox.LostFocus += (_, _) => Refresh(); bBox.LostFocus += (_, _) => Refresh(); hexBox.LostFocus += (_, _) => Refresh();

        Border Section(string text) => new() { Margin = new Thickness(0, 12, 0, 6), Child = new TextBlock { Text = text, FontSize = 11, Opacity = 0.7 } };

        Button Swatch(string hex, bool closeOnPick)
        {
            var b = new Button { Width = 28, Height = 28, MinHeight = 0, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = hex };
            b.Content = new Ellipse { Width = 20, Height = 20, Fill = Brush(hex) };
            System.Windows.Automation.AutomationProperties.SetName(b, $"Color {hex}");
            b.Click += (_, _) => { if (TryHex(hex, out var c)) { Pick(c); if (closeOnPick) popup.IsOpen = false; } };
            return b;
        }

        var presets = new UniformGrid { Columns = 8 };
        foreach (var p in Presets) presets.Children.Add(Swatch(p, closeOnPick: true));

        var recentPanel = new WrapPanel();
        void FillRecent()
        {
            recentPanel.Children.Clear();
            var list = Settings?.RecentColors ?? new List<string>();
            if (list.Count == 0) recentPanel.Children.Add(new TextBlock { Text = "Colors you pick show up here.", FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 4, 0, 0) });
            foreach (var c in list) recentPanel.Children.Add(Swatch(c, closeOnPick: false));
        }
        FillRecent();

        var done = new Button { Content = "Done", Height = 32, MinWidth = 76, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        done.Click += (_, _) => popup.IsOpen = false;
        var noColor = new Button { Content = "No color", Height = 32, Visibility = allowNone ? Visibility.Visible : Visibility.Collapsed };
        noColor.Click += (_, _) => { none = true; dirty = true; Refresh(); popup.IsOpen = false; };
        var foot = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        foot.Children.Add(noColor);
        foot.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { done } });

        var hexRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0),
            Children = { ring, new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children =
                { new TextBlock { Text = "Hex", FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 3) }, hexBox } } },
        };
        var rgbRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new TextBlock { Text = "R", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) }, rBox,
                new TextBlock { Text = "G", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) }, gBox,
                new TextBlock { Text = "B", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) }, bBox,
            },
        };

        var stack = new StackPanel { Width = 300 };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        stack.Children.Add(presets);
        stack.Children.Add(Section("Custom"));
        stack.Children.Add(sv); stack.Children.Add(hueHost); stack.Children.Add(hexRow); stack.Children.Add(rgbRow);
        stack.Children.Add(Section("Recent")); stack.Children.Add(recentPanel);
        stack.Children.Add(foot);

        var card = new Border { Margin = new Thickness(8), Padding = new Thickness(16), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black };
        popup.Child = card;

        popup.Closed += (_, _) =>
        {
            if (!dirty) return;
            if (none) { chosen(null); return; }
            string hex = Hex(Now());
            if (Settings is { } st)
            {
                st.RecentColors.RemoveAll(x => x.Equals(hex, StringComparison.OrdinalIgnoreCase));
                st.RecentColors.Insert(0, hex);
                if (st.RecentColors.Count > 8) st.RecentColors.RemoveRange(8, st.RecentColors.Count - 8);
                try { st.Save(); } catch { }
            }
            chosen(hex);
        };

        Refresh();
        popup.IsOpen = true;
    }
}
