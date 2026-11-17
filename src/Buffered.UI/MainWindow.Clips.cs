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

/// <summary>The clip list: loading, sorting, filtering, opening a clip.</summary>
public partial class MainWindow
{
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
        // The newest long clips are the ones most likely to be opened next: get their smooth-scrubbing copies ready quietly (a clip over about 30 MB is a minute or more).
        ScrubCache.PrepareInBackground(_allClips.Where(c => c.Size >= 30L * 1024 * 1024).OrderByDescending(c => c.When).Take(3).Select(c => c.Path).ToList());
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

    private static readonly (string Id, string Label)[] SortChoices =
    {
        ("new", "Newest first"), ("old", "Oldest first"), ("name", "Name (A to Z)"), ("size", "Biggest first"), ("game", "By game"),
    };

    private void SortBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SortBox.SelectedItem is not ComboBoxItem { Tag: string id } || id == _settings.ClipSort) return;
        _settings.ClipSort = id;
        _settings.TrySave();
        if (IsLoaded) ApplyFilter();
    }

    /// <summary>Puts the clips in the chosen order: by day for newest and oldest, otherwise one list (or one list per game).</summary>
    private List<ClipGroup> BuildGroups(List<ClipItem> shown)
    {
        switch (_settings.ClipSort)
        {
            case "old":
                return shown.GroupBy(c => c.When.Date).OrderBy(g => g.Key).Select(g => new ClipGroup(ClipItem.FriendlyDay(g.Key), g.OrderBy(c => c.When).ToList())).ToList();
            case "name":
                return new() { new ClipGroup("A to Z", shown.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToList()) };
            case "size":
                return new() { new ClipGroup("Biggest first", shown.OrderByDescending(c => c.Size).ToList()) };
            case "game":
                return shown.GroupBy(c => c.Label, StringComparer.CurrentCultureIgnoreCase).OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                            .Select(g => new ClipGroup(g.Key, g.OrderByDescending(c => c.When).ToList())).ToList();
            default:
                return shown.GroupBy(c => c.When.Date).OrderByDescending(g => g.Key).Select(g => new ClipGroup(ClipItem.FriendlyDay(g.Key), g.ToList())).ToList();
        }
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

        var groups = BuildGroups(shown);
        _shown = groups.SelectMany(g => g.Clips).ToList();   // the order on screen, so the keyboard and selection follow it
        GroupList.ItemsSource = groups;

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

    /// <summary>Shows the advanced editor in place of whatever page is open.</summary>
    private async void ShowAdvanced(Action<AdvancedEditor> open)
    {
        if (!await Advanced.ConfirmLeaveAsync()) return;   // the edit that is open now has unsaved changes: ask first
        if (Player.Visibility == Visibility.Visible) { Player.Release(); Player.Visibility = Visibility.Collapsed; }
        _navFromCode = true; NavEditor.IsChecked = true; _navFromCode = false;
        ClipsPage.Visibility = Visibility.Collapsed; SettingsPage.Visibility = Visibility.Collapsed;
        Advanced.Visibility = Visibility.Visible;
        open(Advanced);
    }

    private static string? DroppedEdit(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files ? files.FirstOrDefault(f => f.EndsWith(".bfedit", StringComparison.OrdinalIgnoreCase)) : null;

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var edit = DroppedEdit(e);
        e.Effects = edit is not null ? DragDropEffects.Copy : DragDropEffects.None;
        if (edit is not null)
        {
            DropName.Text = System.IO.Path.GetFileNameWithoutExtension(edit);
            DropTitle.Text = Advanced.HasSession && Advanced.IsDirty ? "Drop to open this edit (you'll be asked about the unsaved one)" : "Drop to open this edit";
            DropOverlay.Visibility = Visibility.Visible;
            _dropHide ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _dropHide.Tick -= HideDropOverlay; _dropHide.Tick += HideDropOverlay;
            _dropHide.Stop(); _dropHide.Start();   // dragging over keeps raising DragOver; if it stops (the pointer left) the overlay goes away by itself
        }
        e.Handled = true;
    }

    private System.Windows.Threading.DispatcherTimer? _dropHide;
    private void HideDropOverlay(object? sender, EventArgs e) { _dropHide?.Stop(); DropOverlay.Visibility = Visibility.Collapsed; }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        // leaving one control for another inside the window also raises this: only hide when the pointer has really gone out
        var p = e.GetPosition(this);
        if (p.X <= 1 || p.Y <= 1 || p.X >= ActualWidth - 1 || p.Y >= ActualHeight - 1) DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (DroppedEdit(e) is { } path) ShowAdvanced(a => _ = a.OpenFileAsync(path));
        e.Handled = true;
    }

    private void OpenPlayer(ClipItem clip, (double Start, double End)? select = null, bool editor = false)
    {
        bool preview = select is null && !editor && !_settings.ClipOpensEditor;
        ClipsPage.Visibility = Visibility.Collapsed; // the player replaces the clip list while it is open
        Player.Visibility = Visibility.Visible;
        Hitch.State = "opening a clip"; Hitch.Measure("open a clip in the editor", () => Player.Open(clip, select, preview));
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
}
