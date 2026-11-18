using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Buffered.UI;

/// <summary>
/// The Settings page: a list of sections on the left and a search box on top. The rows themselves are still written in
/// MainWindow.xaml; at startup they are moved, untouched, into five sections, so every control keeps its look and behavior.
/// </summary>
public partial class MainWindow
{
    private sealed class SettingsUnit
    {
        public string Title = "";
        public Grid Row = null!;                 // the row itself
        public List<FrameworkElement> Extras = new();   // panels that belong to it (per-game settings, the app panel)
        public Border Wrap = null!;
        public string Keys = "";
    }

    private sealed class SettingsSection
    {
        public string Id = "", Name = "", Sub = "", Glyph = "";
        public StackPanel Header = null!; public TextBlock HeaderName = null!, HeaderSub = null!;
        public Border Card = null!;
        public List<SettingsUnit> Units = new();
        public ToggleButton Nav = null!; public TextBlock Count = null!, GlyphText = null!, Label = null!; public Grid NavGrid = null!;
    }

    private bool _navCompact;

    /// <summary>In a narrow window the section list shrinks to icons, so the settings keep their room.</summary>
    private void UpdateSettingsNav()
    {
        bool compact = ActualWidth < 960;
        if (compact == _navCompact || _sections.Count == 0) return;
        _navCompact = compact;
        SettingsNavColumn.Width = new GridLength(compact ? 62 : 176);
        foreach (var s in _sections)
        {
            s.Label.Visibility = s.Count.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            s.GlyphText.Margin = compact ? new Thickness(0, 1, 0, 0) : new Thickness(0, 1, 10, 0);
            s.NavGrid.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        }
    }

    private readonly List<SettingsSection> _sections = new();
    private string _settingsSection = "rec";
    private StackPanel? _settingsEmpty;
    private TextBlock? _settingsEmptyTitle;

    private static readonly (string Id, string Name, string Glyph, string Sub)[] SectionDefs =
    {
        ("rec", "Recording", "", "What gets recorded, and how much of it is kept"),
        ("aud", "Audio", "", "Game sound, microphone and apps"),
        ("sav", "Saving", "", "Where clips go and how you save them"),
        ("sha", "Sharing", "", "Places you can send a clip with one click"),
        ("edi", "Editor", "", "How the clip editor looks and works"),
        ("sys", "System", "", "How Buffered runs on this PC"),
        ("upd", "Updates", "", "Check for a newer version and install it"),
    };

    // Row title, its section, and extra words that find it. The order here is the order on screen.
    private static readonly (string Title, string Section, string Keys)[] SettingOrder =
    {
        ("Monitor", "rec", "screen display"),
        ("Quality", "rec", "resolution fps frame rate bitrate 720p 1080p 1440p"),
        ("Capture border", "rec", "yellow border outline screenshot frame windows 10 11 recording edge"),
        ("Buffer length", "rec", "ram memory replay seconds minutes"),
        ("Record game windows", "rec", "vrchat per-game window profile games"),
        ("Keep game windows visible", "rec", "minimized restore"),
        ("Game audio", "aud", "speakers output device headphones"),
        ("Ignore Windows volume", "aud", "quiet loud master"),
        ("Microphone", "aud", "mic input"),
        ("Game volume", "aud", "gain loudness db"),
        ("Mic volume", "aud", "gain loudness db"),
        ("App on its own track", "aud", "discord spotify chrome separate"),
        ("Keep Game and Mic as separate tracks", "aud", "stems premiere davinci"),
        ("Switch for SteamVR", "aud", "vr vrchat headset"),
        ("Save hotkey", "sav", "shortcut key keybind"),
        ("Extra clip hotkeys", "sav", "shortcut key keybind seconds minutes last length shorter clip time"),
        ("Pause hotkey (optional)", "sav", "shortcut key keybind pause resume hold"),
        ("Stop hotkey (optional)", "sav", "shortcut key keybind stop start recording off"),
        ("Clips folder", "sav", "location move storage drive directory"),
        ("Sound when a clip is saved", "sav", "beep chime audio cue"),
        ("Discord webhooks", "sha", "discord webhook share send post channel server link embed color order"),
        ("Timeline detail", "edi", "editor timeline waveform filmstrip frames pictures sound audio trim gear"),
        ("Open the editor straight away", "edi", "click clip preview editor open straight away edit"),
        ("Arrow key seek", "edi", "arrow keys seek skip jump seconds shift left right editor keyboard"),
        ("Moving the playhead while playing", "edi", "scrub scrubbing drag playhead pause resume stay paused keep playing live editor seek"),
        ("Start with Windows", "sys", "startup boot login autostart"),
        ("Shortcuts", "sys", "start menu desktop icon"),
        ("Interface zoom", "sys", "zoom size scale bigger smaller text ui dpi magnify ctrl wheel"),
        ("Draw this window on the CPU", "sys", "gpu software rendering vram"),
        ("Start-up picture", "sys", "splash loading screen logo opening"),
        ("Fade the window in", "sys", "animation smooth opening transition"),
        ("Compress audio in memory", "sys", "ram memory mu-law"),
        ("Updates", "upd", "version upgrade release download install check new"),
    };

    private static string FirstText(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock tb && tb.Text.Length > 0) return tb.Text;
            if (child is Popup) continue;
            var inner = FirstText(child);
            if (inner.Length > 0) return inner;
        }
        return "";
    }

    private static IEnumerable<TextBlock> TextsOf(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is TextBlock tb) yield return tb;
            else if (child is Popup) continue;   // the pickers hold lists of names, not text of the row itself
            else foreach (var t in TextsOf(child)) yield return t;
        }
    }

    private void BuildSettingsLayout()
    {
        var oldCards = SettingsSource.Children.OfType<Border>().ToList();
        // Which section each old card falls back to if a row isn't in the list above.
        string[] fallback = { "rec", "aud", "sys" };

        foreach (var d in SectionDefs)
        {
            var s = new SettingsSection { Id = d.Id, Name = d.Name, Sub = d.Sub, Glyph = d.Glyph };
            s.HeaderName = new TextBlock { Text = d.Name, FontSize = 18, FontWeight = FontWeights.SemiBold };
            s.HeaderSub = new TextBlock { Text = d.Sub, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 4, 0, 0) };
            s.Header = new StackPanel { Margin = new Thickness(0, 4, 0, 10), Children = { s.HeaderName, s.HeaderSub } };
            s.Card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 16), Child = new StackPanel() };
            _sections.Add(s);
        }

        for (int c = 0; c < oldCards.Count; c++)
        {
            if (oldCards[c].Child is not StackPanel stack) continue;
            var kids = stack.Children.Cast<UIElement>().ToList();
            stack.Children.Clear();
            SettingsUnit? cur = null;
            foreach (var k in kids)
            {
                if (k is TextBlock) continue;                                       // the old section caption
                if (k is Border { Height: 1 } && k is FrameworkElement { Name: "" }) continue;   // the old dividers
                if (k is Grid g)
                {
                    cur = new SettingsUnit { Row = g, Title = FirstText(g) };
                    string sec = fallback[Math.Min(c, 2)];
                    foreach (var o in SettingOrder) if (o.Title == cur.Title) { sec = o.Section; cur.Keys = o.Keys; }
                    _sections.First(x => x.Id == sec).Units.Add(cur);
                }
                else if (cur is not null && k is FrameworkElement fe) cur.Extras.Add(fe);   // GamePanel, AppPanel
            }
        }

        foreach (var s in _sections)
        {
            s.Units = s.Units.OrderBy(u => Array.FindIndex(SettingOrder, o => o.Title == u.Title) is var i and >= 0 ? i : int.MaxValue).ToList();
            var panel = (StackPanel)s.Card.Child;
            foreach (var u in s.Units)
            {
                var inner = new StackPanel();
                u.Row.Margin = new Thickness(18, 15, 18, 15);   // a little more air than the old cards had
                inner.Children.Add(u.Row);
                foreach (var e in u.Extras) inner.Children.Add(e);
                u.Wrap = new Border { Child = inner, BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = (Brush)FindResource("ControlStrokeColorDefaultBrush") };
                panel.Children.Add(u.Wrap);
            }
            SettingsBody.Children.Add(s.Header);
            SettingsBody.Children.Add(s.Card);

            // the list on the left
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            s.GlyphText = new TextBlock { Text = s.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 10, 0), Opacity = 0.75 };
            grid.Children.Add(s.GlyphText);
            var label = s.Label = new TextBlock { Text = s.Name, VerticalAlignment = VerticalAlignment.Center };
            s.NavGrid = grid;
            Grid.SetColumn(label, 1); grid.Children.Add(label);
            s.Count = new TextBlock { Style = (Style)FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(s.Count, 2); grid.Children.Add(s.Count);
            s.Nav = new ToggleButton { Style = (Style)FindResource("RailItem"), Content = grid, Tag = s.Id, ToolTip = s.Name };
            System.Windows.Automation.AutomationProperties.SetName(s.Nav, s.Name);
            var id = s.Id;
            // Click covers the mouse and keyboard; Checked covers assistive tools, which toggle the button instead of clicking it.
            void Go() { if (_settingsBusy) return; SettingsSearch.Text = ""; _settingsSection = id; ShowSettings(); }
            s.Nav.Click += (_, _) => Go();
            s.Nav.Checked += (_, _) => Go();
            SettingsNav.Children.Add(s.Nav);
        }

        _settingsEmptyTitle = new TextBlock { FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center };
        _settingsEmpty = new StackPanel
        {
            Margin = new Thickness(0, 60, 0, 0), Visibility = Visibility.Collapsed,
            Children =
            {
                _settingsEmptyTitle,
                new TextBlock { Text = "Try a shorter word, like \"audio\" or \"folder\".", Style = (Style)FindResource("Caption"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) },
            }
        };
        SettingsBody.Children.Add(_settingsEmpty);
        SettingsSource.Children.Clear();
        ShowSettings();
    }

    private static readonly Brush FindBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xE0, 0xB8, 0x4A));

    private static bool WordMatches(string text, string word) =>
        Regex.IsMatch(text, @"(^|[^\p{L}\p{N}])" + Regex.Escape(word), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The words a row is matched against: what it says on screen, plus its extra words.</summary>
    private static string UnitText(SettingsUnit u) => string.Join(" ", TextsOf(u.Row).Select(t => t.Text)) + " " + u.Keys;

    private bool _settingsBusy;

    private void ShowSettings()
    {
        _settingsBusy = true;
        try { ShowSettingsCore(); } finally { _settingsBusy = false; }
    }

    private void ShowSettingsCore()
    {
        string q = SettingsSearch.Text.Trim();
        var words = q.Length == 0 ? Array.Empty<string>() : q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool searching = words.Length > 0;
        SettingsSearchHint.Visibility = SettingsSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsSearchClear.Visibility = SettingsSearch.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        int total = 0;
        foreach (var s in _sections)
        {
            var shown = new List<SettingsUnit>();
            foreach (var u in s.Units)
            {
                bool hit = !searching || words.All(w => WordMatches(UnitText(u), w));
                u.Wrap.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
                u.Wrap.BorderThickness = new Thickness(0, 0, 0, 1);
                if (hit) shown.Add(u);
                Highlight(u, hit && searching ? words : Array.Empty<string>());
            }
            if (shown.Count > 0) shown[^1].Wrap.BorderThickness = new Thickness(0);
            total += shown.Count;

            bool show = searching ? shown.Count > 0 : s.Id == _settingsSection;
            s.Header.Visibility = s.Card.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            s.HeaderName.FontSize = searching ? 13 : 18;
            s.HeaderName.FontWeight = searching ? FontWeights.Normal : FontWeights.SemiBold;
            s.HeaderName.Opacity = searching ? 0.7 : 1;
            s.HeaderSub.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
            s.Nav.IsChecked = !searching && s.Id == _settingsSection;
            s.Count.Text = searching && shown.Count > 0 ? shown.Count.ToString() : "";
            s.Nav.Opacity = searching && shown.Count == 0 ? 0.4 : 1;
        }

        _settingsEmpty!.Visibility = searching && total == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (searching && total == 0) _settingsEmptyTitle!.Text = $"No settings match \"{q}\"";
        SettingsScroll.ScrollToTop();
    }

    /// <summary>Marks the searched words in a row's title and description, or puts the plain text back.</summary>
    private static void Highlight(SettingsUnit u, string[] words)
    {
        foreach (var panel in u.Row.Children.OfType<StackPanel>())
            foreach (var tb in panel.Children.OfType<TextBlock>())
            {
                string text = tb.Text;
                // Always start from plain text, so marks from an earlier search never linger.
                if (tb.Inlines.Count > 1 || tb.Inlines.FirstInline is System.Windows.Documents.Run { Background: not null }) tb.Text = text;
                if (words.Length == 0) continue;
                var spans = new List<(int From, int To)>();
                foreach (var w in words)
                    foreach (Match m in Regex.Matches(text, @"(?<=^|[^\p{L}\p{N}])" + Regex.Escape(w), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        spans.Add((m.Index, m.Index + m.Length));
                if (spans.Count == 0) continue;
                spans.Sort();
                tb.Inlines.Clear();
                int p = 0;
                foreach (var (from, to) in spans)
                {
                    if (from < p) continue;
                    if (from > p) tb.Inlines.Add(new System.Windows.Documents.Run(text[p..from]));
                    tb.Inlines.Add(new System.Windows.Documents.Run(text[from..to]) { Background = FindBrush });
                    p = to;
                }
                if (p < text.Length) tb.Inlines.Add(new System.Windows.Documents.Run(text[p..]));
            }
    }

    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e) { if (_sections.Count > 0) ShowSettings(); }

    private void SettingsSearchClear_Click(object sender, RoutedEventArgs e) { SettingsSearch.Text = ""; SettingsSearch.Focus(); }

    /// <summary>Ctrl+F jumps to the settings search; Escape clears it. Returns true when the key was used.</summary>
    private bool SettingsKey(KeyEventArgs e)
    {
        if (SettingsPage.Visibility != Visibility.Visible || Dialogs.IsOpen) return false;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SettingsSearch.Focus(); SettingsSearch.SelectAll(); return true; }
        if (e.Key == Key.Escape && SettingsSearch.Text.Length > 0) { SettingsSearch.Text = ""; return true; }
        return false;
    }
}
