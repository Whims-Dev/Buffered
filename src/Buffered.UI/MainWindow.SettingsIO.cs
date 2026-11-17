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

/// <summary>Settings: filling in the page, reading it back, and applying it to the recorder.</summary>
public partial class MainWindow
{
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
        ClickEditorBox.IsChecked = _settings.ClipOpensEditor;
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
        _settings.ClipOpensEditor = ClickEditorBox.IsChecked == true;

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
}
