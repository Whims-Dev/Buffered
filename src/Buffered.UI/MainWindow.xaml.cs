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
    private bool _isPaused, _isStopped;
    private DateTime _restartUntil = DateTime.MinValue;

    public double CardWidth { get; private set; } = 260;
    public double CardHeight => CardWidth * 9 / 16;

    private string _pendingText = "Restarting…";

    public MainWindow(Settings settings, string? openClip = null, (double, double)? select = null, bool startedRecorder = false)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = this;
        StartZoom();
        BuildSettingsLayout();
        WebhookList.Attach(_settings);
        ColorPalette.Settings = _settings;
        Player.AppSettings = _settings;
        Player.MoveShareTo((Grid)Dialogs.Parent, Dialogs);

        if (startedRecorder)
        {
            // We just launched the recorder; give it a few seconds before calling it missing.
            _pendingText = "Starting recorder…";
            _restartUntil = DateTime.UtcNow.AddSeconds(12);
        }

        Player.Dialogs = Dialogs;
        Player.CloseRequested += () => { Hitch.State = "clips"; Player.Visibility = Visibility.Collapsed; ClipsPage.Visibility = Visibility.Visible; LoadClips(); };
        Player.OpenEditorSettingsRequested += () => { _settingsSection = "edi"; NavSettings.IsChecked = true; ShowSettings(); };
        Player.TimelineDetailChanged += () => { _loading = true; SelectTimeline(); SelectScrub(); _loading = false; };
        Player.FileReplaced += path => { _allClips.RemoveAll(c => c.Path == path); }; // drop the cached thumbnail and size

        _statusTimer.Tick += async (_, _) => await PollStatusAsync();
        _applyTimer.Tick += async (_, _) => { _applyTimer.Stop(); await ApplySettingsAsync(); };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); LoadClips(); };

        // Everything that reads from the system (audio devices, monitors, the clips folder) waits until the window has drawn its first
        // frame. Doing it before then keeps the empty window on screen, unpainted, while it works.
        void StartUp()
        {
            ContentRendered -= StartUpHandler;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                Hitch.StartWatchdog(Dispatcher);
                Hitch.Measure("fill in the settings", PopulateSettings);
                ShowUpdateResult();
                FixStaleStartupEntry();
                _ = Task.Run(() => { try { var (n, b) = InstallCleaner.Clean(AppContext.BaseDirectory, new[] { _settings.OutDir }.Concat(_settings.GameProfiles.Values.Select(g => g.Folder))); if (n > 0) Media.LogWindow($"removed {n} leftover files from older versions ({b / 1048576} MB)"); } catch { } });
                Hitch.Measure("remove orphaned companion files", CleanOrphanStems);
                LoadClips();
                WatchFolder();
                Hitch.Measure("size the cards", UpdateCardWidth);
                if (WindowState != WindowState.Minimized)   // a window that opens minimized never raises the state change below
                {
                    _statusTimer.Start();
                    _ = PollStatusAsync();
                }
                if (openClip is not null && File.Exists(openClip)) OpenPlayer(new ClipItem(openClip), select);
            }));
        }
        void StartUpHandler(object? s, EventArgs e) => StartUp();
        ContentRendered += StartUpHandler;
        // Don't poll while minimized.
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) _statusTimer.Stop();
            else { _statusTimer.Start(); _ = PollStatusAsync(); }   // catch up at once when it comes back
        };
    }

    // ---- pages --------------------------------------------------------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && ClipsPage is null) return;
        bool settings = NavSettings.IsChecked == true;
        Hitch.State = settings ? "settings" : "clips";
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
            foreach (var s in Directory.EnumerateFiles(_settings.OutDir, ".*.stems.mka", System.IO.SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(s);                               // ".Label_2026-...stems.mka"
                string clip = Path.Combine(Path.GetDirectoryName(s)!, name[1..^".stems.mka".Length] + ".mp4");
                if (!File.Exists(clip)) { try { File.Delete(s); } catch { } }
            }
        }
        catch { }
    }

    private void LoadClips() => Hitch.Measure("load clips", LoadClipsCore);

    private void LoadClipsCore()
    {
        var dir = _settings.OutDir;
        ClipLibrary.Use(dir);
        var known = _allClips.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
        var fresh = new List<ClipItem>();

        // Clips live in the folder itself or in one level of folders inside it.
        var places = new List<string> { dir };
        if (Directory.Exists(dir)) places.AddRange(ClipLibrary.Folders().Select(ClipLibrary.FolderPath));
        var scanStart = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var place in places.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(place, "*.mp4"))
            {
                if (Path.GetFileName(f).StartsWith('.')) continue;
                try
                {
                    if (known.TryGetValue(f, out var existing)) fresh.Add(existing);
                    else { var c = new ClipItem(f); fresh.Add(c); }   // its thumbnail loads when its card comes near the screen
                }
                catch (IOException) { /* still being written */ }
            }

        if (Hitch.Detailed) Hitch.Log($"  reading the clips folder: {System.Diagnostics.Stopwatch.GetElapsedTime(scanStart).TotalMilliseconds:0} ms for {fresh.Count} clips");
        _allClips.Clear();
        _allClips.AddRange(fresh.OrderByDescending(c => c.When));
        _sel.RemoveWhere(c => !_allClips.Contains(c));
        if (_view.StartsWith("dir:") && !ClipLibrary.Folders().Contains(_view[4..], StringComparer.OrdinalIgnoreCase)) _view = "all";
        RebuildFilters();
        Hitch.Measure("rebuild the folder list", RebuildRail);
        Hitch.Measure("show the clips", ApplyFilter);
        SelectionChanged();
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
        var inView = _view switch
        {
            "fav" => _allClips.Where(c => c.IsFavorite),
            "none" => _allClips.Where(c => c.Folder.Length == 0),
            var v when v.StartsWith("dir:") => _allClips.Where(c => c.Folder.Equals(v[4..], StringComparison.OrdinalIgnoreCase)),
            _ => _allClips.AsEnumerable(),
        };
        var shown = inView.Where(c => (_filter == "All" || c.Label == _filter) &&
                                      (q.Length == 0 || c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                       (c.Note?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))).ToList();
        _shown = shown;
        UpdateAccent();

        GroupList.ItemsSource = shown.GroupBy(c => c.When.Date).OrderByDescending(g => g.Key)
                                     .Select(g => new ClipGroup(ClipItem.FriendlyDay(g.Key), g.ToList())).ToList();

        long total = _allClips.Sum(c => c.Size);
        ClipMeta.Text = _allClips.Count == 0 ? "" : $"{_allClips.Count} clip{(_allClips.Count == 1 ? "" : "s")} · {ClipItem.SizeText(total)}";

        bool none = _allClips.Count == 0;
        EmptyState.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Scroller.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyTitle.Text = none ? "Save your first clip" : _view == "none" && q.Length == 0 ? "Everything is in a folder" : _view == "fav" && q.Length == 0 ? "No favorites yet" : _view.StartsWith("dir:") && q.Length == 0 ? "This folder is empty" : "No clips match";
        EmptyBody.Text = none
            ? $"Press {_settings.Hotkey} while you play to keep the last {FormatBuffer(_settings.BufferSeconds)}."
            : _view == "none" && q.Length == 0 ? "New clips land here until you move them into a folder."
            : _view == "fav" && q.Length == 0 ? "Star a clip to keep it here and protect it from delete."
            : _view.StartsWith("dir:") && q.Length == 0 ? "Drag clips onto the folder, or use Move to."
            : "Try a different search or filter.";
    }

    private void WatchFolder()
    {
        _watcher?.Dispose();
        Directory.CreateDirectory(_settings.OutDir);
        _watcher = new FileSystemWatcher(_settings.OutDir) { IncludeSubdirectories = true, EnableRaisingEvents = true };
        void Queue(object _, FileSystemEventArgs e)
        {
            // Clips and folders matter; the hidden library file, thumbnails and companion files don't.
            var name = e.Name ?? "";
            var leaf = Path.GetFileName(name);
            if (leaf.StartsWith('.') || !(leaf.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || !leaf.Contains('.'))) return;
            Dispatcher.BeginInvoke(() => { _refreshTimer.Stop(); _refreshTimer.Start(); });
        }
        _watcher.Created += Queue; _watcher.Deleted += Queue; _watcher.Renamed += (s, e) => Queue(s, e);
    }

    private void OpenPlayer(ClipItem clip, (double Start, double End)? select = null)
    {
        ClipsPage.Visibility = Visibility.Collapsed; // the player replaces the clip list while it is open
        Player.Visibility = Visibility.Visible;
        Hitch.State = "opening a clip"; Hitch.Measure("open a clip in the editor", () => Player.Open(clip, select));
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
        if (((FrameworkElement)sender).DataContext is ClipItem c) await DeleteAsync(new List<ClipItem> { c });
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
            Dialogs.Toast("Couldn't save a clip", _isStopped ? "Recording is stopped." : _isPaused ? "Recording is paused." : "Is the recorder running?", error: true);
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
            else if (s.Stopped)
            {
                SetOffline("Stopped", false);
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
                ShowButtons(paused: false, stopped: true);
                VramText.Text = "VRAM 0 MB";
            }
            else if (!s.Recording)
            {
                SetOffline(s.Starting ? "Starting… waiting for Windows to be ready" + (s.Error is { Length: > 0 } why ? $" (last try: {(why.Length > 90 ? why[..90] + "…" : why)})" : "") : s.Error is { } err ? $"Couldn't start: {err}" : "Recording is off", false);
            }
            else
            {
                StatusDot.Fill = new SolidColorBrush(s.Paused ? Color.FromRgb(0xE0, 0xA0, 0x30) : Color.FromRgb(0x2E, 0x9E, 0x5B));
                StatusText.Text = s.Paused ? "Paused" : "Recording";
                StartRecorderButton.Visibility = Visibility.Collapsed;
                ShowButtons(s.Paused, stopped: false);
                BufferText.Text = $"Buffer {FormatClock(s.BufferSeconds > 0 ? Math.Min(s.BufferedSeconds, s.BufferSeconds) : s.BufferedSeconds)} / {FormatClock(s.BufferSeconds)}";   // the buffer trims at a keyframe, so it can hold a moment more than its length: show it as full
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

    private void ShowButtons(bool paused, bool stopped)
    {
        _isPaused = paused;
        _isStopped = stopped;
        // Pause (keeps the buffer) only while recording is on; Stop turns into Start once stopped.
        PauseButton.Visibility = stopped ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Text = paused ? "" : ""; // play / pause
        PauseLabel.Text = paused ? "Resume" : "Pause";
        PauseButton.ToolTip = paused ? "Resume recording" : "Pause recording (keeps the buffer)";
        StopButton.Visibility = Visibility.Visible;
        StopGlyph.Text = stopped ? "" : ""; // play / stop
        StopLabel.Text = stopped ? "Start" : "Stop";
        StopButton.ToolTip = stopped ? "Start recording" : "Stop recording (empties the buffer)";
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        if (_isPaused) await Ipc.ResumeAsync(); else await Ipc.PauseAsync();
        await PollStatusAsync();
        PauseButton.IsEnabled = true;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        _pendingText = "Restarting…";
        _restartUntil = DateTime.UtcNow.AddSeconds(6);
        StatusText.Text = "Restarting…";
        if (_isStopped) await Ipc.StartAsync(); else await Ipc.StopAsync();
        StopButton.IsEnabled = true;
    }

    private void SetOffline(string text, bool canStart)
    {
        StatusDot.Fill = Brushes.Gray;
        StatusText.Text = text;
        PauseButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
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
        ShowOptionalKeys();
        RebuildClipKeys();
        HotkeyChip.Text = _settings.Hotkey;
        FolderText.Text = _settings.OutDir;
        VrBox.IsChecked = _settings.VrAutoSwitch;
        SoundBox.IsChecked = _settings.SaveSound;
        CompBox.IsChecked = _settings.GameVolumeComp;
        _games.Clear();
        _games.AddRange((_settings.WindowApps ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase));
        RebuildGameChips();
        KeepVisibleBox.IsChecked = _settings.KeepWindowVisible;
        CompressBox.IsChecked = _settings.CompressAudioMemory;
        RebuildAppChips();
        RefreshGeneral();
        TracksBox.IsChecked = _settings.SeparateTracks;
        GameGainSlider.Value = Vol.DbToPercent(_settings.GameGainDb);
        MicGainSlider.Value = Vol.DbToPercent(_settings.MicGainDb);
        UpdateGainTexts();
        SoftwareBox.IsChecked = _settings.UiSoftwareRendering;
        SplashBox.IsChecked = _settings.ShowSplash;
        FadeBox.IsChecked = _settings.FadeIn;
        ShowBorderHint();
        SelectZoomBox();
        TimelineBox.ItemsSource = TimelineChoices;
        TimelineBox.DisplayMemberPath = nameof(TimelineChoice.Name);
        ScrubBox.ItemsSource = ScrubChoices;
        ScrubBox.DisplayMemberPath = nameof(TimelineChoice.Name);
        SeekBox.ItemsSource = SeekChoices; SeekBox.DisplayMemberPath = nameof(SeekChoice.Name);
        SeekShiftBox.ItemsSource = SeekChoices; SeekShiftBox.DisplayMemberPath = nameof(SeekChoice.Name);
        SelectTimeline(); SelectScrub(); SelectSeek();
        UpdateRamHint();
        _hardKey = HardKey();
        _loading = false;
    }

    // Settings that need the recorder to restart (and so empty its buffer) versus ones it can take while running.
    private string _hardKey = "";
    private string HardKey() => string.Join("|", _settings.MonitorDevice, _settings.Height, _settings.Fps, _settings.BufferSeconds,
        _settings.GameAudioDevice, _settings.MicDevice, _settings.VrAutoSwitch, _settings.GameVolumeComp, _settings.OutDir, _settings.WindowApps, _settings.CompressAudioMemory, GameProfilesKey(), string.Join(",", _settings.AppTracks.Keys.OrderBy(k => k)));

    /// <summary>Shows a stored decibel value as the percentage the sliders use.</summary>
    private static string DbText(double db) => Vol.Text(Vol.DbToPercent(db));

    private void UpdateGainTexts()
    {
        GameGainText.Text = Vol.Text(GameGainSlider.Value);
        MicGainText.Text = Vol.Text(MicGainSlider.Value);
    }

    private void Gain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        _settings.GameGainDb = Vol.PercentToDb(GameGainSlider.Value);
        _settings.MicGainDb = Vol.PercentToDb(MicGainSlider.Value);
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
            bool selected = string.Equals(_selectedGame, name, StringComparison.OrdinalIgnoreCase);
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            var star = new TextBlock { Text = "*", Foreground = Gold, FontWeight = FontWeights.SemiBold, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                                       Visibility = IsGameEdited(name) ? Visibility.Visible : Visibility.Collapsed };
            var x = new Button
            {
                Content = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9,
                Padding = new Thickness(5), Margin = new Thickness(6, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetName(x, "Remove " + name);
            x.Click += (_, e) => { _games.Remove(name); RebuildGameChips(); Setting_Changed(x, e); };
            var chip = new Border
            {
                CornerRadius = new CornerRadius(15), Height = 30, Padding = new Thickness(14, 0, 4, 0), Margin = new Thickness(0, 2, 6, 2),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand, ToolTip = "Click to change this game's settings",
                BorderBrush = selected ? Gold : (Brush)FindResource("ControlStrokeColorDefaultBrush"),
                Background = selected ? new SolidColorBrush(Color.FromArgb(0x30, 0xE0, 0xB8, 0x4A)) : (Brush)FindResource("ControlFillColorDefaultBrush"),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, star, x } }
            };
            chip.MouseLeftButtonUp += (_, _) => SelectGame(name);
            GameChips.Children.Add(chip);
        }
        RefreshGamePanel();
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

    /// <summary>Windows draws a coloured border around what is being captured. Windows 11 lets an app turn it off if the user allows it; Windows 10 never does.</summary>
    private void ShowBorderHint()
    {
        bool win11 = Environment.OSVersion.Version.Build >= 22000;
        BorderHint.Text = win11
            ? "Windows draws a thin border around the screen while recording, and Buffered asks it not to. If you still see it, open Windows' settings and allow Buffered under \"Screenshot borders\" (Privacy & security)."
            : "Windows 10 draws a yellow border around the screen while anything records it, and it doesn't let apps hide it. Windows 11 does.";
        BorderButton.Visibility = win11 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BorderButton_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:privacy-graphicscapturewithoutborder") { UseShellExecute = true }); } catch { }
    }

    private sealed record TimelineChoice(string Id, string Name);
    private static readonly TimelineChoice[] TimelineChoices =
    {
        new("wave", "Waveform"), new("film", "Filmstrip"), new("both", "Waveform and filmstrip"), new("none", "Plain"),
    };

    private static readonly TimelineChoice[] ScrubChoices =
    {
        new("resume", "Pause, then keep playing"), new("stay", "Pause, and stay paused"), new("live", "Keep playing"),
    };

    private sealed record SeekChoice(double Value, string Name);
    private static readonly SeekChoice[] SeekChoices = new[] { 0.5, 1, 2, 3, 5, 10, 15, 30, 60 }.Select(v => new SeekChoice(v, v % 1 == 0 ? $"{v:0}" : $"{v:0.#}")).ToArray();   // just the number: the unit is written beside the boxes, so typing needs no deleting

    private static string SecondsLabel(double v) => v % 1 == 0 ? $"{v:0}" : $"{v:0.##}";

    private void ShowSeek(ComboBox box, double value)
    {
        var preset = SeekChoices.FirstOrDefault(c => Math.Abs(c.Value - value) < 1e-9);
        if (preset is not null) box.SelectedItem = preset;
        else { box.SelectedItem = null; box.Text = SecondsLabel(value); }   // a distance typed in by the user
    }

    private void SelectSeek()
    {
        ShowSeek(SeekBox, _settings.SeekSeconds > 0 ? _settings.SeekSeconds : 5);
        ShowSeek(SeekShiftBox, _settings.SeekShiftSeconds > 0 ? _settings.SeekShiftSeconds : 1);
    }

    /// <summary>Reads a distance from a box: the chosen item, or what was typed ("7", "7.5", "7,5 s"). Returns <paramref name="keep"/> if it makes no sense.</summary>
    private static double ReadSeek(ComboBox box, double keep)
    {
        if (box.SelectedItem is SeekChoice c && c.Name == (box.Text ?? "").Trim()) return c.Value;   // a picked item (the text may have been edited since: then it is read below)
        string t = (box.Text ?? "").Trim().ToLowerInvariant().Replace(',', '.');
        foreach (var suffix in new[] { "seconds", "second", "secs", "sec", "s" }) if (t.EndsWith(suffix)) { t = t[..^suffix.Length].Trim(); break; }
        return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0.1 && v <= 3600 ? Math.Round(v, 2) : keep;
    }

    /// <summary>A typed distance is taken when the box loses focus or Enter is pressed: tidied up (or put back, if it wasn't a number) and saved.</summary>
    private void Seek_Commit(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ComboBox box) return;
        bool shift = box == SeekShiftBox;
        double keep = shift ? _settings.SeekShiftSeconds : _settings.SeekSeconds;
        double value = ReadSeek(box, keep);
        _loading = true; ShowSeek(box, value); _loading = false;
        if (Math.Abs(value - keep) > 1e-9) { if (shift) _settings.SeekShiftSeconds = value; else _settings.SeekSeconds = value; Setting_Changed(box, e); }
    }

    private void Seek_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ComboBox box)
            box.Dispatcher.BeginInvoke(new Action(() => (box.Template.FindName("PART_EditableTextBox", box) as TextBox)?.SelectAll()), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Seek_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        Seek_Commit(sender, e);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void SelectScrub() =>
        ScrubBox.SelectedItem = ScrubChoices.FirstOrDefault(c => c.Id == _settings.ScrubMode) ?? ScrubChoices[0];

    private void SelectTimeline() =>
        TimelineBox.SelectedItem = TimelineChoices.FirstOrDefault(c => c.Id == _settings.TimelineDetail) ?? TimelineChoices[0];

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
        _settings.CompressAudioMemory = CompressBox.IsChecked == true;
        _settings.SeparateTracks = TracksBox.IsChecked == true;
        _settings.UiSoftwareRendering = SoftwareBox.IsChecked == true;
        _settings.ShowSplash = SplashBox.IsChecked == true;
        _settings.FadeIn = FadeBox.IsChecked == true;
        if (TimelineBox.SelectedItem is TimelineChoice tc) _settings.TimelineDetail = tc.Id;
        if (ScrubBox.SelectedItem is TimelineChoice sc) _settings.ScrubMode = sc.Id;
        _settings.SeekSeconds = ReadSeek(SeekBox, _settings.SeekSeconds);
        _settings.SeekShiftSeconds = ReadSeek(SeekShiftBox, _settings.SeekShiftSeconds);

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
        if (_settings.CompressAudioMemory) audio /= 2;   // mu-law holds each sample in 8 bits
        RamHint.Text = $"Uses up to about {video + audio:F0} MB of RAM, not VRAM";
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where clips are saved", InitialDirectory = _settings.OutDir };
        if (dlg.ShowDialog(this) != true) return;

        string oldDir = _settings.OutDir, newDir = dlg.FolderName;
        if (!SamePath(oldDir, newDir) && Directory.Exists(oldDir) && !IsInside(newDir, oldDir) && !IsInside(oldDir, newDir))
        {
            ClipLibrary.Use(oldDir);
            var items = LibraryItems(oldDir);
            if (items.Count > 0)
            {
                long bytes = items.Sum(SizeOf);
                bool move = await Dialogs.ConfirmAsync("Move your clips too?",
                    $"{_allClips.Count} clips ({FormatSize(bytes)}) are in the current folder. Move them, with their folders, favorites and notes, to the new one?\n\nIf you leave them, they stay where they are and the library will show only new clips.",
                    "Move clips", false, "Leave them");
                if (move)
                {
                    _watcher?.Dispose(); _watcher = null;
                    ApplyText.Text = "Moving clips…";
                    string? error = await Task.Run(() => MoveLibrary(items, oldDir, newDir));
                    if (error is not null) await Dialogs.MessageAsync("Couldn't move every clip", error + "\n\nWhatever couldn't be moved is still in the old folder.");
                    else Dialogs.Toast("Clips moved", newDir);
                }
            }
        }

        _settings.OutDir = newDir;
        FolderText.Text = _settings.OutDir;
        WatchFolder();
        LoadClips();
        ApplyText.Text = "Applying…";
        _applyTimer.Stop(); _applyTimer.Start();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="child"/> is the same as, or lives inside, <paramref name="parent"/>.</summary>
    private static bool IsInside(string child, string parent) =>
        (Path.GetFullPath(child).TrimEnd('\\') + "\\").StartsWith(Path.GetFullPath(parent).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string FormatSize(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    /// <summary>Everything Buffered keeps in the clips folder: clips, hidden companion files, the library file and the clip folders.</summary>
    private static List<string> LibraryItems(string dir)
    {
        var list = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var n = Path.GetFileName(f);
            if (n.StartsWith('.') || n.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) list.Add(f);
        }
        foreach (var name in ClipLibrary.Folders())
        {
            var p = ClipLibrary.FolderPath(name);
            if (Directory.Exists(p)) list.Add(p);
        }
        return list;
    }

    private static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            return Directory.EnumerateFiles(path, "*", System.IO.SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    /// <summary>Moves the items into the new folder (a rename on the same drive, copy then delete across drives). Never overwrites. Returns an error text, or null.</summary>
    private static string? MoveLibrary(List<string> items, string oldDir, string newDir)
    {
        string? error = null;
        try { Directory.CreateDirectory(newDir); } catch (Exception ex) { return ex.Message; }

        void MoveFile(string from, string to)
        {
            if (File.Exists(to)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            try { File.Move(from, to); }
            catch (IOException) when (File.Exists(from) && !File.Exists(to))
            {
                File.Copy(from, to);                       // across drives: copy, then remove the original
                File.Delete(from);
            }
        }

        foreach (var item in items)
        {
            try
            {
                string rel = Path.GetRelativePath(oldDir, item);
                if (File.Exists(item)) MoveFile(item, Path.Combine(newDir, rel));
                else
                {
                    foreach (var f in Directory.EnumerateFiles(item, "*", System.IO.SearchOption.AllDirectories).ToList())
                        MoveFile(f, Path.Combine(newDir, Path.GetRelativePath(oldDir, f)));
                    try { if (!Directory.EnumerateFileSystemEntries(item).Any()) Directory.Delete(item); } catch { }
                    Directory.CreateDirectory(Path.Combine(newDir, rel));   // keep empty folders too
                }
            }
            catch (Exception ex) { error ??= $"{Path.GetFileName(item)}: {ex.Message}"; }
        }
        return error;
    }

    private Button? _capturing;

    private static object KeyOf(Button b) => b.Tag;   // "save", "pause" or "stop", or the ClipKey an extra row edits

    private string GetKey(object which) => which switch { ClipKey ck => ck.Key, "pause" => _settings.PauseHotkey, "stop" => _settings.StopHotkey, _ => _settings.Hotkey };

    private void SetKey(object which, string value)
    {
        if (which is ClipKey ck) ck.Key = value;
        else if (which as string == "pause") _settings.PauseHotkey = value;
        else if (which as string == "stop") _settings.StopHotkey = value;
        else _settings.Hotkey = value;
    }

    // ---- extra clip hotkeys -------------------------------------------------

    private void RebuildClipKeys()
    {
        ClipKeysPanel.Children.Clear();
        foreach (var ck in _settings.ClipKeys) ClipKeysPanel.Children.Add(BuildClipKeyRow(ck));
    }

    private UIElement BuildClipKeyRow(ClipKey ck)
    {
        bool minutes = ck.Seconds >= 60 && ck.Seconds % 60 == 0;
        var number = new TextBox { Width = 70, Height = 34, Text = (minutes ? ck.Seconds / 60 : ck.Seconds).ToString(), HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var unit = new ComboBox { Width = 110, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        unit.Items.Add("seconds"); unit.Items.Add("minutes");
        unit.SelectedIndex = minutes ? 1 : 0;
        var key = new Button { Tag = ck, Width = 210, Height = 34, HorizontalContentAlignment = HorizontalAlignment.Left, Content = ck.Key.Length > 0 ? ck.Key : "None" };
        key.Click += HotkeyButton_Click; key.PreviewKeyDown += HotkeyButton_PreviewKeyDown; key.LostKeyboardFocus += HotkeyButton_LostFocus;
        var remove = new Button { Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Remove this hotkey", Content = new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), Text = "\uE711", FontSize = 12 } };
        System.Windows.Automation.AutomationProperties.SetName(remove, "Remove this hotkey");
        System.Windows.Automation.AutomationProperties.SetName(number, "Seconds or minutes to save");
        System.Windows.Automation.AutomationProperties.SetName(unit, "Unit");
        var note = new TextBlock { Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };

        void Update()
        {
            bool ok = int.TryParse(number.Text.Trim(), out int v) && v > 0 && v <= 100000;
            number.BorderBrush = ok ? (Brush)FindResource("ControlStrokeColorDefaultBrush") : Brushes.IndianRed;
            if (!ok) { note.Text = "Enter a number above 0."; note.Visibility = Visibility.Visible; return; }
            ck.Seconds = unit.SelectedIndex == 1 ? v * 60 : v;
            note.Text = ck.Seconds > _settings.BufferSeconds ? "Longer than the buffer: saves all of it." : "";
            note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Setting_Changed(number, new RoutedEventArgs());
        }
        number.TextChanged += (_, _) => { if (!_loading) Update(); };
        unit.SelectionChanged += (_, _) => { if (!_loading) Update(); };
        remove.Click += (_, _) => { _settings.ClipKeys.Remove(ck); RebuildClipKeys(); Setting_Changed(remove, new RoutedEventArgs()); };

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = "Save the last", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        line.Children.Add(number); line.Children.Add(unit); line.Children.Add(key); line.Children.Add(remove);
        if (ck.Seconds > _settings.BufferSeconds) { note.Text = "Longer than the buffer: saves all of it."; note.Visibility = Visibility.Visible; }
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(line); row.Children.Add(note);
        return row;
    }

    private void AddClipKey_Click(object sender, RoutedEventArgs e)
    {
        _settings.ClipKeys.Add(new ClipKey { Seconds = 30 });
        RebuildClipKeys();
        Setting_Changed(sender, e);
    }

    /// <summary>Shows each optional key (or "None") and only offers the clear button when one is set.</summary>
    private void ShowOptionalKeys()
    {
        PauseHotkeyButton.Content = _settings.PauseHotkey is { Length: > 0 } p ? p : "None";
        StopHotkeyButton.Content = _settings.StopHotkey is { Length: > 0 } t ? t : "None";
        PauseHotkeyClear.Visibility = _settings.PauseHotkey.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StopHotkeyClear.Visibility = _settings.StopHotkey.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var b = (Button)sender;
        if (_capturing is not null && _capturing != b) EndCapture();
        _capturingKey = true;
        _capturing = b;
        b.Content = "Press a key…";
        b.Focus();
    }

    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingKey || _capturing is null) return;
        e.Handled = true;
        var b = _capturing;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { EndCapture(); return; }
        // Ctrl, Alt, Shift and the Windows key alone are not a hotkey: wait for the key they go with.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None) return;

        string? name = key switch
        {
            >= Key.F1 and <= Key.F24 => key.ToString(),
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
            _ => null,
        };
        if (name is null) { b.Content = "Use F1–F24, a letter, or a digit"; return; }
        var mods = Keyboard.Modifiers;
        name = (mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (mods.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + name;

        object which = KeyOf(b);
        SetKey(which, name);
        if (which as string == "save") HotkeyChip.Text = name;
        EndCapture();
        Setting_Changed(b, e);
    }

    private void HotkeyButton_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (_capturingKey && sender == _capturing) EndCapture(); }

    private void EndCapture()
    {
        _capturingKey = false;
        if (_capturing is { } b) { object which = KeyOf(b); b.Content = GetKey(which) is { Length: > 0 } k ? k : "None"; }
        _capturing = null;
        ShowOptionalKeys();
    }

    private void HotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        var b = (Button)sender;
        SetKey(KeyOf(b), "");
        ShowOptionalKeys();
        Setting_Changed(b, e);
    }

    // ---- layout / lifetime --------------------------------------------------

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) { UpdateCardWidth(); UpdateSettingsNav(); }

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
