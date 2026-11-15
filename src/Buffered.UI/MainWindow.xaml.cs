using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace Buffered.UI;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly Settings _settings;
    private readonly List<ClipItem> _allClips = new();
    private string _filter = "All";
    private bool _loading = true;
    private bool _capturingKey;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _polling;
    private bool _isPaused;
    private DateTime _restartUntil = DateTime.MinValue;

    public double CardWidth { get; private set; } = 260;
    public double CardHeight => CardWidth * 9 / 16;

    private string _pendingText = "Restarting…";

    public MainWindow(Settings settings, string? openClip = null, (double, double)? select = null, bool startedRecorder = false)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = this;

        if (startedRecorder)
        {
            // We just launched the recorder; give it a few seconds before calling it missing.
            _pendingText = "Starting recorder…";
            _restartUntil = DateTime.UtcNow.AddSeconds(12);
        }

        Player.Dialogs = Dialogs;
        Player.CloseRequested += () => { Player.Visibility = Visibility.Collapsed; ClipsPage.Visibility = Visibility.Visible; LoadClips(); };
        Player.FileReplaced += path => { _allClips.RemoveAll(c => c.Path == path); }; // drop the cached thumbnail and size

        _statusTimer.Tick += async (_, _) => await PollStatusAsync();
        _applyTimer.Tick += async (_, _) => { _applyTimer.Stop(); await ApplySettingsAsync(); };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); LoadClips(); };

        Loaded += (_, _) =>
        {
            PopulateSettings();
            CleanOrphanStems();
            LoadClips();
            WatchFolder();
            UpdateCardWidth();
            _statusTimer.Start();
            _ = PollStatusAsync();
            if (openClip is not null && File.Exists(openClip)) OpenPlayer(new ClipItem(openClip), select);
        };
        // Don't poll while minimized.
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) _statusTimer.Stop(); else _statusTimer.Start(); };
    }

    // ---- pages --------------------------------------------------------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && ClipsPage is null) return;
        bool settings = NavSettings.IsChecked == true;
        // Leaving the player (either way) closes it; only one page is ever active.
        if (Player.Visibility == Visibility.Visible) { Player.Release(); Player.Visibility = Visibility.Collapsed; }
        ClipsPage.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        if (!settings) LoadClips();
    }

    // ---- clips --------------------------------------------------------------

    /// <summary>Removes companion files whose clip was deleted outside Buffered (for example in Explorer).</summary>
    private void CleanOrphanStems()
    {
        try
        {
            if (!Directory.Exists(_settings.OutDir)) return;
            foreach (var s in Directory.EnumerateFiles(_settings.OutDir, ".*.stems.mka"))
            {
                string name = Path.GetFileName(s);                               // ".Label_2026-...stems.mka"
                string clip = Path.Combine(_settings.OutDir, name[1..^".stems.mka".Length] + ".mp4");
                if (!File.Exists(clip)) { try { File.Delete(s); } catch { } }
            }
        }
        catch { }
    }

    private void LoadClips()
    {
        var dir = _settings.OutDir;
        var known = _allClips.ToDictionary(c => c.Path);
        var fresh = new List<ClipItem>();

        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*.mp4"))
            {
                if (Path.GetFileName(f).StartsWith('.')) continue;
                try
                {
                    if (known.TryGetValue(f, out var existing)) fresh.Add(existing);
                    else { var c = new ClipItem(f); fresh.Add(c); _ = c.LoadMediaAsync(); }
                }
                catch (IOException) { /* still being written */ }
            }

        _allClips.Clear();
        _allClips.AddRange(fresh.OrderByDescending(c => c.When));
        RebuildFilters();
        ApplyFilter();
    }

    private void RebuildFilters()
    {
        FilterPanel.Children.Clear();
        var labels = new[] { "All" }.Concat(_allClips.Select(c => c.Label).Distinct().OrderBy(l => l)).ToList();
        if (!labels.Contains(_filter)) _filter = "All";
        foreach (var l in labels)
        {
            var b = new ToggleButton { Content = l, Style = (Style)FindResource("Pill"), IsChecked = l == _filter, Tag = l };
            b.Click += (_, _) => { _filter = (string)b.Tag; foreach (ToggleButton t in FilterPanel.Children) t.IsChecked = (string)t.Tag == _filter; ApplyFilter(); };
            FilterPanel.Children.Add(b);
        }
        FilterPanel.Visibility = labels.Count > 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyFilter()
    {
        string q = SearchBox.Text.Trim();
        var shown = _allClips.Where(c => (_filter == "All" || c.Label == _filter) &&
                                         (q.Length == 0 || c.Label.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        GroupList.ItemsSource = shown.GroupBy(c => c.When.Date).OrderByDescending(g => g.Key)
                                     .Select(g => new ClipGroup(ClipItem.FriendlyDay(g.Key), g.ToList())).ToList();

        long total = _allClips.Sum(c => c.Size);
        ClipMeta.Text = _allClips.Count == 0 ? "" : $"{_allClips.Count} clip{(_allClips.Count == 1 ? "" : "s")} · {ClipItem.SizeText(total)}";

        bool none = _allClips.Count == 0;
        EmptyState.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = none ? "Save your first clip" : "No clips match";
        EmptyBody.Text = none
            ? $"Press {_settings.Hotkey} while you play to keep the last {FormatBuffer(_settings.BufferSeconds)}."
            : "Try a different search or filter.";
    }

    private void WatchFolder()
    {
        _watcher?.Dispose();
        Directory.CreateDirectory(_settings.OutDir);
        _watcher = new FileSystemWatcher(_settings.OutDir, "*.mp4") { EnableRaisingEvents = true };
        void Queue(object _, FileSystemEventArgs __) => Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
        _watcher.Created += Queue; _watcher.Deleted += Queue; _watcher.Renamed += (s, e) => Queue(s, e);
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        if (((FrameworkElement)sender).DataContext is ClipItem c && File.Exists(c.Path)) OpenPlayer(c);
    }

    private void OpenPlayer(ClipItem clip, (double Start, double End)? select = null)
    {
        ClipsPage.Visibility = Visibility.Collapsed; // the player replaces the clip list while it is open
        Player.Visibility = Visibility.Visible;
        Player.Open(clip, select);
    }

    private void External_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).DataContext is ClipItem c && File.Exists(c.Path))
            Process.Start(new ProcessStartInfo(c.Path) { UseShellExecute = true });
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).DataContext is ClipItem c)
            Process.Start("explorer.exe", $"/select,\"{c.Path}\"");
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).DataContext is not ClipItem c) return;
        bool yes = await Dialogs.ConfirmAsync("Move this clip to the Recycle Bin?",
            $"\"{c.Title}\" ({ClipItem.SizeText(c.Size)}, {c.When:MMM d, h:mm tt}) will be moved to the Recycle Bin. You can restore it from there.",
            "Move to Recycle Bin", danger: true);
        if (!yes) return;
        try
        {
            FileSystem.DeleteFile(c.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            var stems = ClipNames.StemsPath(c.Path); // the hidden companion file goes with its clip
            if (File.Exists(stems)) File.Delete(stems);
        }
        catch (Exception ex) { await Dialogs.MessageAsync("Couldn't delete this clip", ex.Message); }
        LoadClips();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) ApplyFilter();
    }

    // ---- save / status ------------------------------------------------------

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        string old = SaveLabel.Text;
        SaveLabel.Text = "Saving…";
        var path = await Ipc.SaveClipAsync();
        SaveLabel.Text = old;
        SaveButton.IsEnabled = true;
        if (path is null)
        {
            Dialogs.Toast("Couldn't save a clip", _isPaused ? "Recording is paused." : "Is the recorder running?", error: true);
            return;
        }
        LoadClips();
        Dialogs.Toast("Clip saved", Path.GetFileName(path), "Show in folder", () => Process.Start("explorer.exe", $"/select,\"{path}\""));
    }

    private async Task PollStatusAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var s = await Ipc.GetStatusAsync();
            if (s is null && DateTime.UtcNow < _restartUntil)
            {
                // The recorder restarts itself after a pause or settings change; it is back in about a second.
                SetOffline(_pendingText, false);
            }
            else if (s is null)
            {
                SetOffline("Recorder isn't running", true);
            }
            else if (s.Paused)
            {
                SetOffline("Paused", false);
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
                ShowPauseButton(paused: true);
                VramText.Text = "VRAM 0 MB";
            }
            else if (!s.Recording)
            {
                SetOffline(s.Error is { } err ? $"Couldn't start: {err}" : "Recording is off", false);
            }
            else
            {
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B));
                StatusText.Text = "Recording";
                StartRecorderButton.Visibility = Visibility.Collapsed;
                ShowPauseButton(paused: false);
                BufferText.Text = $"Buffer {FormatClock(s.BufferedSeconds)} / {FormatClock(s.BufferSeconds)}";
                BufferBar.Visibility = Visibility.Visible;
                BufferBar.Value = s.BufferSeconds > 0 ? Math.Min(100, s.BufferedSeconds / s.BufferSeconds * 100) : 0;
                VramText.Text = s.VramMb >= 0 ? $"VRAM {s.VramMb} MB" : "";
                CpuText.Text = $"CPU {s.CpuPercent:F0}%";
                QualityText.Text = (s.Source is { Length: > 0 } src && src != "Monitor" ? $"{src} · " : "") + $"{s.Height}p · {s.Fps} fps";
                AudioMeters.Visibility = string.IsNullOrEmpty(s.Audio) ? Visibility.Collapsed : Visibility.Visible;
                GameMeter.Value = ToMeter(s.GameLevel);
                MicMeter.Value = ToMeter(s.MicLevel);
                StatusStrip.ToolTip = string.IsNullOrEmpty(s.Audio) ? "No audio is being recorded" : $"Audio: {s.Audio}";
            }
        }
        finally { _polling = false; }
    }

    private void ShowPauseButton(bool paused)
    {
        _isPaused = paused;
        PauseButton.Visibility = Visibility.Visible;
        PauseGlyph.Text = paused ? "" : ""; // play / pause
        PauseLabel.Text = paused ? "Resume" : "Pause";
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        _pendingText = "Restarting…";
        _restartUntil = DateTime.UtcNow.AddSeconds(6);
        StatusText.Text = "Restarting…";
        if (_isPaused) await Ipc.ResumeAsync(); else await Ipc.PauseAsync();
        PauseButton.IsEnabled = true;
    }

    private void SetOffline(string text, bool canStart)
    {
        StatusDot.Fill = Brushes.Gray;
        StatusText.Text = text;
        PauseButton.Visibility = Visibility.Collapsed;
        StartRecorderButton.Visibility = canStart ? Visibility.Visible : Visibility.Collapsed;
        BufferText.Text = ""; BufferBar.Visibility = Visibility.Collapsed; VramText.Text = ""; CpuText.Text = ""; QualityText.Text = "";
        AudioMeters.Visibility = Visibility.Collapsed;
    }

    /// <summary>Maps a 0 to 1 peak to a meter on a decibel scale (silence at -60 dB), so quiet sound still shows.</summary>
    private static double ToMeter(double peak) => peak <= 0.001 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60 * 100, 0, 100);

    private void StartRecorderButton_Click(object sender, RoutedEventArgs e)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "BufferedRecorder.exe");
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    // ---- settings -----------------------------------------------------------

    private static readonly int[] Heights = { 720, 1080, 1440 };
    private static readonly int[] Rates = { 30, 60, 120 };
    private static readonly int[] BufferChoices = { 30, 120, 300, 600 };

    private void PopulateSettings()
    {
        _loading = true;

        var monitors = Monitors.List();
        MonitorBox.ItemsSource = monitors;
        var chosen = monitors.FirstOrDefault(m => m.DeviceName == _settings.MonitorDevice) ?? monitors.First(m => m.IsPrimary);
        MonitorBox.SelectedItem = chosen;
        FillMonitorDependent(chosen);

        BufferBox.ItemsSource = BufferChoices.Select(s => new Choice(s, FormatBuffer(s))).ToList();
        BufferBox.DisplayMemberPath = nameof(Choice.Label);
        BufferBox.SelectedItem = ((IEnumerable<Choice>)BufferBox.ItemsSource).FirstOrDefault(c => c.Value == _settings.BufferSeconds)
                                 ?? ((IEnumerable<Choice>)BufferBox.ItemsSource).First(c => c.Value == 300);

        FillAudioChoices();

        HotkeyButton.Content = _settings.Hotkey;
        HotkeyChip.Text = _settings.Hotkey;
        FolderText.Text = _settings.OutDir;
        VrBox.IsChecked = _settings.VrAutoSwitch;
        SoundBox.IsChecked = _settings.SaveSound;
        CompBox.IsChecked = _settings.GameVolumeComp;
        _games.Clear();
        _games.AddRange((_settings.WindowApps ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase));
        RebuildGameChips();
        KeepVisibleBox.IsChecked = _settings.KeepWindowVisible;
        TracksBox.IsChecked = _settings.SeparateTracks;
        GameGainSlider.Value = _settings.GameGainDb;
        MicGainSlider.Value = _settings.MicGainDb;
        UpdateGainTexts();
        SoftwareBox.IsChecked = _settings.UiSoftwareRendering;
        UpdateRamHint();
        _hardKey = HardKey();
        _loading = false;
    }

    // Settings that need the recorder to restart (and so empty its buffer) versus ones it can take while running.
    private string _hardKey = "";
    private string HardKey() => string.Join("|", _settings.MonitorDevice, _settings.Height, _settings.Fps, _settings.BufferSeconds,
        _settings.GameAudioDevice, _settings.MicDevice, _settings.VrAutoSwitch, _settings.GameVolumeComp, _settings.OutDir, _settings.WindowApps);

    private static string DbText(double v) => Math.Abs(v) < 0.05 ? "0 dB" : $"{(v > 0 ? "+" : "−")}{Math.Abs(v):0.#} dB";

    private void UpdateGainTexts()
    {
        GameGainText.Text = DbText(GameGainSlider.Value);
        MicGainText.Text = DbText(MicGainSlider.Value);
    }

    private void Gain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        _settings.GameGainDb = GameGainSlider.Value;
        _settings.MicGainDb = MicGainSlider.Value;
        UpdateGainTexts();
        ApplyText.Text = "Applying…";
        _applyTimer.Stop(); _applyTimer.Start();
    }

    /// <summary>Audio dropdowns list the devices Windows reports right now, plus "system default" and (for the mic) "none".</summary>
    private void FillAudioChoices()
    {
        var outputs = AudioDevices.Outputs();
        var inputs = AudioDevices.Inputs();

        var game = new List<AudioChoice> { new(null, $"System default ({AudioDevices.DefaultOutputName() ?? "no output found"})") };
        game.AddRange(outputs.Select(d => new AudioChoice(d.Id, d.Name)));
        if (_settings.GameAudioDevice is { } gid && game.All(c => c.Id != gid)) game.Add(new(gid, "Saved device (not connected)"));
        GameAudioBox.ItemsSource = game;
        GameAudioBox.SelectedItem = game.First(c => c.Id == _settings.GameAudioDevice);

        var mic = new List<AudioChoice> { new(null, $"System default ({AudioDevices.DefaultInputName() ?? "no microphone found"})"), new(AudioDevices.None, "No microphone") };
        mic.AddRange(inputs.Select(d => new AudioChoice(d.Id, d.Name)));
        if (_settings.MicDevice is { } mid && mic.All(c => c.Id != mid)) mic.Add(new(mid, "Saved device (not connected)"));
        MicBox.ItemsSource = mic;
        MicBox.SelectedItem = mic.First(c => c.Id == _settings.MicDevice);
    }

    /// <summary>Resolution and frame rate choices only offer what the selected monitor can actually do.</summary>
    private void FillMonitorDependent(MonitorInfo m)
    {
        var heights = Heights.Where(h => h <= m.Height).DefaultIfEmpty(m.Height & ~1).ToList();
        ResolutionBox.ItemsSource = heights.Select(h => new Choice(h, $"{h}p")).ToList();
        ResolutionBox.DisplayMemberPath = nameof(Choice.Label);
        ResolutionBox.SelectedItem = ((IEnumerable<Choice>)ResolutionBox.ItemsSource).FirstOrDefault(c => c.Value == _settings.Height)
                                     ?? ((IEnumerable<Choice>)ResolutionBox.ItemsSource).Last();

        var rates = Rates.Where(r => r <= Math.Max(m.RefreshHz, 60)).ToList();
        FpsBox.ItemsSource = rates.Select(r => new Choice(r, $"{r} fps")).ToList();
        FpsBox.DisplayMemberPath = nameof(Choice.Label);
        FpsBox.SelectedItem = ((IEnumerable<Choice>)FpsBox.ItemsSource).FirstOrDefault(c => c.Value == _settings.Fps)
                              ?? ((IEnumerable<Choice>)FpsBox.ItemsSource).Last();
    }

    // ---- game windows ---------------------------------------------------------

    private readonly List<string> _games = new();
    private sealed record OpenWindow(string Process, string Title)
    {
        public string Detail => Title.Equals(Process, StringComparison.OrdinalIgnoreCase) ? "" : Title;
        public override string ToString() => Process + " " + Title;
    }
    private List<OpenWindow> _openWindows = new();

    private void RebuildGameChips()
    {
        GameChips.Children.Clear();
        foreach (var g in _games)
        {
            var name = g;
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            var x = new Button
            {
                Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9,
                Padding = new Thickness(5), Margin = new Thickness(6, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetName(x, "Remove " + name);
            x.Click += (_, e) => { _games.Remove(name); RebuildGameChips(); Setting_Changed(x, e); };
            var chip = new Border
            {
                CornerRadius = new CornerRadius(15), Height = 30, Padding = new Thickness(14, 0, 4, 0), Margin = new Thickness(0, 2, 6, 2),
                BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("ControlStrokeColorDefaultBrush"),
                Background = (Brush)FindResource("ControlFillColorDefaultBrush"),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, x } }
            };
            GameChips.Children.Add(chip);
        }
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "explorer", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "Buffered", "BufferedUI", "BufferedRecorder", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "LockApp" };
        _openWindows = Process.GetProcesses()
            .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0 && !skip.Contains(p.ProcessName); } catch { return false; } })
            .Select(p => new OpenWindow(p.ProcessName, p.MainWindowTitle))
            .DistinctBy(w => w.Process, StringComparer.OrdinalIgnoreCase)
            .OrderBy(w => w.Process, StringComparer.OrdinalIgnoreCase).ToList();
        GameSearch.Text = "";
        FillGameList();
        GamePopup.HorizontalOffset = AddGameButton.ActualWidth - 312; // line the popup's right edge up with the button
        GamePopup.IsOpen = true;
        GameSearch.Focus();
    }

    private void FillGameList()
    {
        var q = GameSearch.Text.Trim();
        var items = _openWindows.Where(w => !_games.Contains(w.Process, StringComparer.OrdinalIgnoreCase)
            && (q.Length == 0 || w.ToString().Contains(q, StringComparison.OrdinalIgnoreCase))).Cast<object>().ToList();
        // Anything typed that isn't open right now can still be added (a game that isn't running yet).
        if (q.Length > 0 && !_openWindows.Any(w => w.Process.Equals(q, StringComparison.OrdinalIgnoreCase)) && !_games.Contains(q, StringComparer.OrdinalIgnoreCase))
            items.Add(new OpenWindow(q, "add by name"));
        GameList.ItemsSource = items;
    }

    private void GameSearch_Changed(object sender, TextChangedEventArgs e) { if (GamePopup.IsOpen) FillGameList(); }

    private void GameList_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (GameList.SelectedItem is not OpenWindow w) return;
        _games.Add(w.Process);
        GamePopup.IsOpen = false;
        RebuildGameChips();
        Setting_Changed(GameList, e);
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (sender == MonitorBox && MonitorBox.SelectedItem is MonitorInfo m)
        {
            _loading = true;
            FillMonitorDependent(m);
            _loading = false;
        }

        if (MonitorBox.SelectedItem is MonitorInfo mon) _settings.MonitorDevice = mon.IsPrimary ? null : mon.DeviceName; // main monitor stays the default
        if (ResolutionBox.SelectedItem is Choice r) _settings.Height = r.Value;
        if (FpsBox.SelectedItem is Choice f) _settings.Fps = f.Value;
        if (BufferBox.SelectedItem is Choice b) _settings.BufferSeconds = b.Value;
        if (GameAudioBox.SelectedItem is AudioChoice ga) _settings.GameAudioDevice = ga.Id;
        if (MicBox.SelectedItem is AudioChoice mi) _settings.MicDevice = mi.Id;
        _settings.VrAutoSwitch = VrBox.IsChecked == true;
        _settings.SaveSound = SoundBox.IsChecked == true;
        _settings.GameVolumeComp = CompBox.IsChecked == true;
        _settings.WindowApps = string.Join(", ", _games);
        _settings.KeepWindowVisible = KeepVisibleBox.IsChecked == true;
        _settings.SeparateTracks = TracksBox.IsChecked == true;
        _settings.UiSoftwareRendering = SoftwareBox.IsChecked == true;

        UpdateRamHint();
        ApplyText.Text = "Applying…";
        _applyTimer.Stop(); _applyTimer.Start(); // wait for the user to finish clicking around
    }

    private async Task ApplySettingsAsync()
    {
        try { _settings.Save(); }
        catch (Exception ex) { ApplyText.Text = "Couldn't save settings: " + ex.Message; return; }

        string key = HardKey();
        bool needsRestart = key != _hardKey;
        _hardKey = key;

        if (needsRestart)
        {
            _pendingText = "Restarting…";
            _restartUntil = DateTime.UtcNow.AddSeconds(6);
            bool ok = await Ipc.ReloadAsync();
            ApplyText.Text = ok ? "Saved. Recording restarts with the new settings (the buffer starts over)." : "Saved. The recorder will use these next time it starts.";
        }
        else
        {
            // Volumes, hotkey, sound and track options apply on the fly; the buffer is kept.
            bool ok = await Ipc.SoftReloadAsync();
            ApplyText.Text = ok ? "Saved. Applies to your next clip." : "Saved. The recorder will use these next time it starts.";
        }
        LoadClips();
    }

    private void UpdateRamHint()
    {
        // Video at its bitrate, plus audio kept as raw 48 kHz 16-bit PCM: game stereo (192 KB/s) and mic mono (96 KB/s).
        double video = _settings.EffectiveBitrateKbps * 1000.0 / 8 * _settings.BufferSeconds / 1048576;
        double audio = (192_000.0 + (_settings.MicDevice == AudioDevices.None ? 0 : 96_000.0)) * (_settings.BufferSeconds + 15) / 1048576;
        RamHint.Text = $"Uses up to about {video + audio:F0} MB of RAM, not VRAM";
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where clips are saved", InitialDirectory = _settings.OutDir };
        if (dlg.ShowDialog(this) != true) return;

        _settings.OutDir = dlg.FolderName;
        FolderText.Text = _settings.OutDir;
        WatchFolder();
        LoadClips();
        ApplyText.Text = "Applying…";
        _applyTimer.Stop(); _applyTimer.Start();
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingKey = true;
        HotkeyButton.Content = "Press a key…";
        HotkeyButton.Focus();
    }

    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingKey) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { EndCapture(); return; }

        string? name = key switch
        {
            >= Key.F1 and <= Key.F24 => key.ToString(),
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
            _ => null,
        };
        if (name is null) { HotkeyButton.Content = "Use F1–F24, a letter, or a digit"; return; }

        _settings.Hotkey = name;
        HotkeyChip.Text = name;
        EndCapture();
        Setting_Changed(HotkeyButton, e);
    }

    private void HotkeyButton_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (_capturingKey) EndCapture(); }

    private void EndCapture() { _capturingKey = false; HotkeyButton.Content = _settings.Hotkey; }

    // ---- layout / lifetime --------------------------------------------------

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardWidth();

    private void UpdateCardWidth()
    {
        double avail = Scroller.ActualWidth > 0 ? Scroller.ActualWidth - SystemParameters.VerticalScrollBarWidth - 4 : ActualWidth - 140;
        int cols = Math.Max(1, (int)(avail / 250));
        CardWidth = Math.Floor((avail - cols * 14) / cols);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CardHeight)));
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        Player.Release();
        _statusTimer.Stop(); _watcher?.Dispose();
        Application.Current.Shutdown();
    }

    private static string FormatClock(double s) => $"{(int)s / 60}:{(int)s % 60:00}";
    private static string FormatBuffer(int s) => s < 60 ? $"{s} seconds" : $"{s / 60} minute{(s / 60 == 1 ? "" : "s")}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private sealed record Choice(int Value, string Label) { public override string ToString() => Label; }
    private sealed record AudioChoice(string? Id, string Label) { public override string ToString() => Label; }
}
