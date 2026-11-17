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
        Player.CloseRequested += () =>
        {
            Hitch.State = "clips";
            Hitch.Measure("hide the editor", () => Player.Visibility = Visibility.Collapsed);
            Hitch.Measure("show the library", () => { ClipsPage.Visibility = Visibility.Visible; ClipsPage.UpdateLayout(); });
            LoadClips();
        };
        Player.AdvancedRequested += clip => ShowAdvanced(a => a.Open(clip));
        Advanced.Library = () => _allClips;
        Advanced.Dialogs = Dialogs;
        Closing += (_, e) =>
        {
            if (_closeConfirmed || !Advanced.IsDirty) return;
            e.Cancel = true;   // there is an unsaved edit: ask first, then close for real
            _ = ConfirmCloseAsync();
        };
        Advanced.CloseRequested += () => { NavClips.IsChecked = true; if (ClipsPage.Visibility != Visibility.Visible) { Advanced.Visibility = Visibility.Collapsed; ClipsPage.Visibility = Visibility.Visible; LoadClips(); } };
        StartMeters();
        foreach (var (id, label) in SortChoices) SortBox.Items.Add(new ComboBoxItem { Tag = id, Content = label });
        SortBox.SelectedIndex = Math.Max(0, Array.FindIndex(SortChoices, c => c.Id == _settings.ClipSort));
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

    private bool _closeConfirmed, _navFromCode;

    private async Task ConfirmCloseAsync()
    {
        if (!await Advanced.ConfirmLeaveAsync()) return;
        _closeConfirmed = true; Advanced.DiscardUnsaved();
        Close();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded && ClipsPage is null) return;
        if (_navFromCode) return;                       // the editor is being opened for something: the caller shows it
        bool settings = NavSettings.IsChecked == true, editor = NavEditor.IsChecked == true;
        Hitch.State = settings ? "settings" : editor ? "editor" : "clips";
        // Leaving the player closes it; the advanced editor keeps its edit while another page is shown.
        if (Player.Visibility == Visibility.Visible) { Player.Release(); Player.Visibility = Visibility.Collapsed; }
        if (!editor && Advanced.Visibility == Visibility.Visible) { Advanced.Pause(); Advanced.Visibility = Visibility.Collapsed; }
        ClipsPage.Visibility = !settings && !editor ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        if (editor) { Advanced.Visibility = Visibility.Visible; Advanced.ShowOrStart(); }
        if (!settings && !editor) LoadClips();
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
        Player.Release(); Advanced.Release();
        _statusTimer.Stop(); _watcher?.Dispose();
        Application.Current.Shutdown();
    }

    private static string FormatClock(double s) => $"{(int)s / 60}:{(int)s % 60:00}";
    private static string FormatBuffer(int s) => s < 60 ? $"{s} seconds" : $"{s / 60} minute{(s / 60 == 1 ? "" : "s")}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private sealed record Choice(int Value, string Label) { public override string ToString() => Label; }
    private sealed record AudioChoice(string? Id, string Label) { public override string ToString() => Label; }
}
