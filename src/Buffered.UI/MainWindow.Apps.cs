using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.CoreAudioApi;

namespace Buffered.UI;

/// <summary>"App on its own track" in the Audio settings: the picker, the chip, and the app's volume and mix switch.</summary>
public partial class MainWindow
{
    private string? _selectedApp;
    private bool _loadingApp;
    private List<OpenWindow> _runningApps = new();

    private static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "Buffered", "BufferedUI", "BufferedRecorder", "ShellExperienceHost",
        "SearchHost", "StartMenuExperienceHost", "LockApp", "Idle", "System", "svchost", "audiodg", "dwm",
    };

    private void RebuildAppChips()
    {
        AppChips.Children.Clear();
        foreach (var setting in _settings.AppTracks.Values)
        {
            var name = setting.Name;
            bool selected = string.Equals(_selectedApp, name, StringComparison.OrdinalIgnoreCase);
            var x = new Button
            {
                Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9,
                Padding = new Thickness(5), Margin = new Thickness(6, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetName(x, "Remove " + name);
            x.Click += (_, e) =>
            {
                _settings.AppTracks.Remove(Settings.GameKey(name));
                if (selected) _selectedApp = null;
                RebuildAppChips(); Setting_Changed(x, e);
            };
            var chip = new Border
            {
                CornerRadius = new CornerRadius(15), Height = 30, Padding = new Thickness(14, 0, 4, 0), Margin = new Thickness(0, 2, 6, 2),
                BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Click to change this app's settings",
                BorderBrush = selected ? Gold : (Brush)FindResource("ControlStrokeColorDefaultBrush"),
                Background = selected ? new SolidColorBrush(Color.FromArgb(0x30, 0xE0, 0xB8, 0x4A)) : (Brush)FindResource("ControlFillColorDefaultBrush"),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center }, x } }
            };
            chip.MouseLeftButtonUp += (_, _) => { _selectedApp = string.Equals(_selectedApp, name, StringComparison.OrdinalIgnoreCase) ? null : name; RebuildAppChips(); };
            AppChips.Children.Add(chip);
        }
        AddAppButton.Visibility = _settings.AppTracks.Count >= 1 ? Visibility.Collapsed : Visibility.Visible;   // one app for now
        if (_selectedApp is null && _settings.AppTracks.Count == 1) _selectedApp = _settings.AppTracks.Values.First().Name;
        RefreshAppPanel();
    }

    private void RefreshAppPanel()
    {
        var setting = _selectedApp is null ? null : _settings.AppTracks.GetValueOrDefault(Settings.GameKey(_selectedApp));
        if (setting is null) { AppPanel.Visibility = Visibility.Collapsed; return; }
        _loadingApp = true;
        try
        {
            AppPanel.Visibility = Visibility.Visible;
            AppPanelTitle.Inlines.Clear();
            AppPanelTitle.Inlines.Add(new System.Windows.Documents.Run("Settings for "));
            AppPanelTitle.Inlines.Add(new System.Windows.Documents.Run(setting.Name) { FontWeight = FontWeights.SemiBold });
            AppGainSlider.Value = setting.GainDb;
            AppGainText.Text = DbText(setting.GainDb);
            AppMixBox.IsChecked = setting.InMix;
        }
        finally { _loadingApp = false; }
    }

    private void AppGain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingApp || _selectedApp is null || !_settings.AppTracks.TryGetValue(Settings.GameKey(_selectedApp), out var s)) return;
        s.GainDb = e.NewValue; AppGainText.Text = DbText(e.NewValue);
        Setting_Changed(AppGainSlider, new RoutedEventArgs());
    }

    private void AppMix_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingApp || _selectedApp is null || !_settings.AppTracks.TryGetValue(Settings.GameKey(_selectedApp), out var s)) return;
        s.InMix = AppMixBox.IsChecked == true;
        Setting_Changed(AppMixBox, e);
    }

    /// <summary>Which apps are running: those with a window, plus any that Windows knows have made sound (a Discord minimized to the tray).</summary>
    private static List<OpenWindow> RunningApps()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
                using (p)
                    try { if (p.MainWindowHandle != IntPtr.Zero && p.MainWindowTitle.Length > 0 && !NotApps.Contains(p.ProcessName)) found.TryAdd(p.ProcessName, p.MainWindowTitle); } catch { }
        }
        catch { }
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (dev)
                {
                    var sessions = dev.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        try
                        {
                            uint pid = sessions[i].GetProcessID;
                            if (pid == 0) continue;
                            using var proc = Process.GetProcessById((int)pid);
                            if (!NotApps.Contains(proc.ProcessName)) found.TryAdd(proc.ProcessName, "makes sound");
                        }
                        catch { /* gone already */ }
                    }
                }
        }
        catch { }
        return found.Select(kv => new OpenWindow(kv.Key, kv.Value)).OrderBy(w => w.Process, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        _runningApps = RunningApps();
        AppSearch.Text = "";
        FillAppList();
        AppPopup.IsOpen = true;
        AppSearch.Focus();
    }

    private void FillAppList()
    {
        var q = AppSearch.Text.Trim();
        AppList.ItemsSource = _runningApps.Where(w => !_settings.AppTracks.ContainsKey(Settings.GameKey(w.Process)) &&
            (q.Length == 0 || w.ToString().Contains(q, StringComparison.OrdinalIgnoreCase))).Cast<object>().ToList();
    }

    private void AppSearch_Changed(object sender, TextChangedEventArgs e) { if (AppPopup.IsOpen) FillAppList(); }

    private void AppList_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (AppList.SelectedItem is not OpenWindow w) return;
        _settings.AppTracks[Settings.GameKey(w.Process)] = new AppTrackSetting { Name = w.Process };
        _selectedApp = w.Process;
        AppPopup.IsOpen = false;
        RebuildAppChips();
        Setting_Changed(AppList, e);
    }
}
