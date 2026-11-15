using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualBasic.FileIO;

namespace Buffered.UI;

/// <summary>The clips page's selection, folders, favorites, names and notes.</summary>
public partial class MainWindow
{
    private readonly HashSet<ClipItem> _sel = new();
    private List<ClipItem> _shown = new();
    private ClipItem? _anchor;
    private string _view = "all";            // "all", "fav" or "dir:<folder>"

    public bool HasSelection => _sel.Count > 0;

    // ---- selection ----------------------------------------------------------

    private void SetSelection(IEnumerable<ClipItem> items)
    {
        _sel.Clear();
        foreach (var c in items) _sel.Add(c);
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        foreach (var c in _allClips) c.IsSelected = _sel.Contains(c);
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(HasSelection)));
        SelectionBar.Visibility = _sel.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = $"{_sel.Count} selected";
        SelFavoriteLabel.Text = _sel.Count > 0 && _sel.All(c => c.IsFavorite) ? "Unfavorite" : "Favorite";
    }

    private void Toggle(ClipItem c)
    {
        if (!_sel.Remove(c)) _sel.Add(c);
        _anchor = c;
        SelectionChanged();
    }

    private void SelClear_Click(object sender, RoutedEventArgs e) => SetSelection(Array.Empty<ClipItem>());

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled || ClipsPage.Visibility != Visibility.Visible || Dialogs.IsOpen || Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Escape && _sel.Count > 0) { SetSelection(Array.Empty<ClipItem>()); e.Handled = true; }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { SetSelection(_shown); e.Handled = true; }
        else if (e.Key == Key.Delete && _sel.Count > 0) { _ = DeleteAsync(_sel.ToList()); e.Handled = true; }
    }

    // ---- card clicks and dragging --------------------------------------------

    private ClipItem? _pressed;
    private Point _pressPoint;

    private static ClipItem? ClipOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipItem;

    private void Card_Down(object sender, MouseButtonEventArgs e)
    {
        _pressed = ClipOf(sender);
        _pressPoint = e.GetPosition(this);
    }

    private void Card_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed is null) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        // Drag the selection if this clip is part of it, otherwise just this clip. Dropping on a folder moves it; dropping
        // in Explorer, Discord or a browser gives them a copy of the file.
        var items = _sel.Contains(_pressed) ? _sel.ToList() : new List<ClipItem> { _pressed };
        var paths = items.Select(c => c.Path).Where(File.Exists).ToArray();
        _pressed = null;
        if (paths.Length == 0) return;

        var data = new DataObject(DataFormats.FileDrop, paths);
        data.SetData("Buffered.Clips", paths);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    private void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        var c = ClipOf(sender);
        if (c is null || _pressed != c) return;     // the press started somewhere else
        _pressed = null;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor is not null && _shown.Contains(_anchor))
        {
            int a = _shown.IndexOf(_anchor), b = _shown.IndexOf(c);
            if (b >= 0) SetSelection(_shown.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1));
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || _sel.Count > 0) Toggle(c);
        else if (File.Exists(c.Path)) OpenPlayer(c);
    }

    private void Check_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _pressed = null;
        if (ClipOf(sender) is { } c) Toggle(c);
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (ClipOf(sender) is { } c) SetFavorite(new[] { c }, !c.IsFavorite);
    }

    private void SetFavorite(IEnumerable<ClipItem> items, bool on)
    {
        foreach (var c in items) c.IsFavorite = on;
        SelectionChanged();
        RebuildRail();
        ApplyFilter();
    }

    private void SelFavorite_Click(object sender, RoutedEventArgs e)
    {
        var items = _sel.ToList();
        SetFavorite(items, !items.All(c => c.IsFavorite));
    }

    // ---- drag-select rectangle ----------------------------------------------

    private bool _bandPending, _bandActive;
    private Point _bandStart;                  // x in view, y in content coordinates
    private HashSet<ClipItem> _bandBase = new();

    private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static FrameworkElement? CardRootOf(DependencyObject? d)
    {
        while (d is not null)
        {
            if (d is FrameworkElement fe && fe.Name == "CardRoot") return fe;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private List<FrameworkElement> CardRoots()
    {
        var list = new List<FrameworkElement>();
        void Walk(DependencyObject o)
        {
            for (int i = 0, n = VisualTreeHelper.GetChildrenCount(o); i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(o, i);
                if (c is FrameworkElement fe && fe.Name == "CardRoot") list.Add(fe); else Walk(c);
            }
        }
        Walk(GroupList);
        return list;
    }

    private readonly System.Windows.Threading.DispatcherTimer _bandTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private List<(ClipItem Clip, Rect Bounds)> _bandCards = new();   // every card's position in the scrolled content, measured once per drag
    private bool _bandTimerHooked;

    private void Scroller_Down(object sender, MouseButtonEventArgs e)
    {
        if (CardRootOf(e.OriginalSource as DependencyObject) is not null || Ancestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null) return;
        var p = e.GetPosition(BandLayer);
        _bandPending = true; _bandActive = false;
        _bandStart = new Point(p.X, p.Y + Scroller.VerticalOffset);   // y is measured in the scrolled content, so it stays put while the list scrolls
        _bandBase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? new HashSet<ClipItem>(_sel) : new HashSet<ClipItem>();
    }

    private void Scroller_Move(object sender, MouseEventArgs e)
    {
        if (!_bandPending) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndBand(); return; }

        if (!_bandActive)
        {
            var p = e.GetPosition(BandLayer);
            if (Math.Abs(p.X - _bandStart.X) + Math.Abs(p.Y - (_bandStart.Y - Scroller.VerticalOffset)) < 5) return;
            _bandActive = true;
            Scroller.CaptureMouse();
            Band.Visibility = Visibility.Visible;

            // Measure every card once, in content coordinates, so the drag can be answered quickly and correctly while the list scrolls.
            double off = Scroller.VerticalOffset;
            _bandCards = CardRoots().Where(c => c.DataContext is ClipItem).Select(c =>
            {
                var r = c.TransformToVisual(BandLayer).TransformBounds(new Rect(0, 0, c.ActualWidth, c.ActualHeight));
                r.Offset(0, off);
                return ((ClipItem)c.DataContext, r);
            }).ToList();

            if (!_bandTimerHooked) { _bandTimer.Tick += (_, _) => UpdateBand(); _bandTimerHooked = true; }
            _bandTimer.Start();   // keeps scrolling while the pointer rests near an edge, even without mouse movement
        }
        UpdateBand();
    }

    /// <summary>Scrolls if the pointer is near (or past) the top or bottom edge, then redraws the band and selects what it covers.</summary>
    private void UpdateBand()
    {
        if (!_bandActive) return;
        var p = Mouse.GetPosition(BandLayer);
        double h = BandLayer.ActualHeight, w = BandLayer.ActualWidth;

        const double zone = 48;
        double dy = 0;
        if (p.Y < zone) dy = -Math.Min(2.5, (zone - p.Y) / zone) * 26;
        else if (p.Y > h - zone) dy = Math.Min(2.5, (p.Y - (h - zone)) / zone) * 26;
        if (dy != 0) { Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset + dy); Scroller.UpdateLayout(); }

        double off = Scroller.VerticalOffset;
        double cx = Math.Clamp(p.X, 0, w), cy = p.Y + off;                         // the pointer in content coordinates
        var rect = new Rect(new Point(Math.Min(_bandStart.X, cx), Math.Min(_bandStart.Y, cy)),
                            new Point(Math.Max(_bandStart.X, cx), Math.Max(_bandStart.Y, cy)));

        // Draw only the part of the band that is on screen.
        double vTop = Math.Clamp(rect.Top - off, 0, h), vBottom = Math.Clamp(rect.Bottom - off, 0, h);
        Canvas.SetLeft(Band, Math.Clamp(rect.Left, 0, w)); Canvas.SetTop(Band, vTop);
        Band.Width = Math.Max(0, Math.Clamp(rect.Right, 0, w) - Math.Clamp(rect.Left, 0, w)); Band.Height = Math.Max(0, vBottom - vTop);

        // Everything the band has covered since the drag began counts, including cards that have scrolled out of view.
        var hit = new HashSet<ClipItem>(_bandBase);
        foreach (var (clip, bounds) in _bandCards) if (bounds.IntersectsWith(rect)) hit.Add(clip);
        if (!hit.SetEquals(_sel)) SetSelection(hit);
    }

    private void Scroller_Up(object sender, MouseButtonEventArgs e)
    {
        bool plainClick = _bandPending && !_bandActive && !Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        EndBand();
        if (plainClick && _sel.Count > 0) SetSelection(Array.Empty<ClipItem>()); // a click on empty space deselects
    }

    private void EndBand()
    {
        _bandTimer.Stop();
        _bandPending = false; _bandActive = false;
        _bandCards = new();
        Band.Visibility = Visibility.Collapsed;
        if (Scroller.IsMouseCaptured) Scroller.ReleaseMouseCapture();
    }
    // ---- folder list --------------------------------------------------------

    private void RebuildRail()
    {
        RailTop.Children.Clear();
        RailFolders.Children.Clear();
        RailTop.Children.Add(RailItem("all", "", "All clips", _allClips.Count, null));
        RailTop.Children.Add(RailItem("fav", "", "Favorites", _allClips.Count(c => c.IsFavorite), null));
        RailTop.Children.Add(RailItem("none", "", "Unsorted", _allClips.Count(c => c.Folder.Length == 0), null));
        foreach (var f in ClipLibrary.Folders())
            RailFolders.Children.Add(RailItem("dir:" + f, "", f, _allClips.Count(c => c.Folder.Equals(f, StringComparison.OrdinalIgnoreCase)), f));
        if (RailFolders.Children.Count == 0)
            RailFolders.Children.Add(new TextBlock { Text = "Drag clips here after you make a folder.", Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 4, 6, 0) });
    }

    private ToggleButton RailItem(string view, string glyph, string name, int count, string? folder)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 10, 0), Opacity = 0.75 });
        var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 1); grid.Children.Add(label);
        var n = new TextBlock { Text = count.ToString(), Style = (Style)FindResource("Caption"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(n, 2); grid.Children.Add(n);

        var b = new ToggleButton { Style = (Style)FindResource("RailItem"), Content = grid, IsChecked = _view == view, Tag = view, ToolTip = name, AllowDrop = true };
        System.Windows.Automation.AutomationProperties.SetName(b, name);
        b.Click += (_, _) => { _view = view; foreach (var t in RailTop.Children.OfType<ToggleButton>().Concat(RailFolders.Children.OfType<ToggleButton>())) t.IsChecked = (string)t.Tag == _view; ApplyFilter(); };

        if (folder is not null || view == "none")   // dropping on Unsorted takes clips out of their folder
        {
            var target = folder ?? "";
            b.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("Buffered.Clips") ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            b.Drop += async (_, e) =>
            {
                e.Handled = true;
                if (e.Data.GetData("Buffered.Clips") is not string[] paths) return;
                var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                await MoveClipsAsync(_allClips.Where(c => set.Contains(c.Path)).ToList(), target);
            };
        }
        if (folder is not null) b.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowFolderMenu(b, folder); };
        return b;
    }

    private void ShowFolderMenu(FrameworkElement at, string folder)
    {
        var menu = new ContextMenu { PlacementTarget = at };
        var rename = new MenuItem { Header = "Rename folder…" };
        rename.Click += async (_, _) => await RenameFolderAsync(folder);
        var delete = new MenuItem { Header = "Delete folder…" };
        delete.Click += async (_, _) => await DeleteFolderAsync(folder);
        menu.Items.Add(rename); menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e) => await NewFolderAsync(null);

    /// <summary>Asks for a folder name and creates it. If clips are given, they are moved into it.</summary>
    private async Task NewFolderAsync(List<ClipItem>? thenMove)
    {
        var name = await Dialogs.PromptAsync("New folder", null, "", "Create");
        if (name is null) return;
        name = ClipLibrary.CleanFolderName(name);
        if (name.Length == 0) return;
        try { Directory.CreateDirectory(ClipLibrary.FolderPath(name)); }
        catch (Exception ex) { await Dialogs.MessageAsync("Couldn't create the folder", ex.Message); return; }
        if (thenMove is { Count: > 0 }) await MoveClipsAsync(thenMove, name);
        else { RebuildRail(); }
    }

    private async Task RenameFolderAsync(string folder)
    {
        var name = await Dialogs.PromptAsync("Rename folder", null, folder, "Rename");
        if (name is null) return;
        name = ClipLibrary.CleanFolderName(name);
        if (name.Length == 0 || name.Equals(folder, StringComparison.Ordinal)) return;
        try
        {
            var from = ClipLibrary.FolderPath(folder);
            var to = ClipLibrary.FolderPath(name);
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) { var tmp = from + ".renaming"; Directory.Move(from, tmp); Directory.Move(tmp, to); }
            else Directory.Move(from, to);
            ClipLibrary.FolderRenamed(folder, name);
            if (_view == "dir:" + folder) _view = "dir:" + name;
        }
        catch (Exception ex) { await Dialogs.MessageAsync("Couldn't rename the folder", ex.Message); }
        LoadClips();
    }

    private async Task DeleteFolderAsync(string folder)
    {
        var inside = _allClips.Where(c => c.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        bool yes = await Dialogs.ConfirmAsync($"Delete the folder \"{folder}\"?",
            inside.Count == 0 ? "The folder is empty." : $"Its {inside.Count} clip{(inside.Count == 1 ? "" : "s")} won't be deleted. They move back to All clips.",
            "Delete folder", danger: true);
        if (!yes) return;
        try
        {
            foreach (var c in inside) ClipLibrary.MoveClip(c.Path, "");
            var dir = ClipLibrary.FolderPath(folder);
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception ex) { await Dialogs.MessageAsync("Couldn't delete the folder", ex.Message); }
        if (_view == "dir:" + folder) _view = "all";
        LoadClips();
    }

    // ---- moving, renaming, notes, copy, delete -------------------------------

    private async Task MoveClipsAsync(List<ClipItem> items, string folder)
    {
        string? error = null;
        foreach (var c in items)
        {
            try { ClipLibrary.MoveClip(c.Path, folder); }
            catch (Exception ex) { error = ex.Message; }
        }
        SetSelection(Array.Empty<ClipItem>());
        LoadClips();
        if (error is not null) await Dialogs.MessageAsync("Couldn't move every clip", error);
    }

    private ContextMenu MoveMenu(List<ClipItem> items, FrameworkElement at)
    {
        var menu = new ContextMenu { PlacementTarget = at };
        var current = items.Select(c => c.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (current.Any(f => f.Length > 0))
        {
            var top = new MenuItem { Header = "Top level" };
            top.Click += async (_, _) => await MoveClipsAsync(items, "");
            menu.Items.Add(top);
        }
        foreach (var f in ClipLibrary.Folders())
        {
            var mi = new MenuItem { Header = f, IsEnabled = !(current.Count == 1 && current[0].Equals(f, StringComparison.OrdinalIgnoreCase)) };
            var target = f;
            mi.Click += async (_, _) => await MoveClipsAsync(items, target);
            menu.Items.Add(mi);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var nf = new MenuItem { Header = "New folder…" };
        nf.Click += async (_, _) => await NewFolderAsync(items);
        menu.Items.Add(nf);
        return menu;
    }

    private void SelMove_Click(object sender, RoutedEventArgs e)
    {
        var menu = MoveMenu(_sel.ToList(), SelMove);
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ClipOf(sender) is not { } c) return;
        if (!_sel.Contains(c)) SetSelection(Array.Empty<ClipItem>()); // a right-click on something unselected acts on just that clip
        var items = _sel.Contains(c) ? _sel.ToList() : new List<ClipItem> { c };
        var at = (FrameworkElement)sender;
        var menu = new ContextMenu { PlacementTarget = at };

        void Add(string header, Action act, bool danger = false)
        {
            var mi = new MenuItem { Header = header };
            if (danger) mi.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x5A));
            mi.Click += (_, _) => act();
            menu.Items.Add(mi);
        }

        if (items.Count == 1)
        {
            Add("Open", () => { if (File.Exists(c.Path)) OpenPlayer(c); });
            Add("Rename…", async () => await RenameClipAsync(c));
            Add(string.IsNullOrEmpty(c.Note) ? "Add note…" : "Edit note…", async () => await EditNoteAsync(c));
            menu.Items.Add(new Separator());
        }
        Add(items.All(i => i.IsFavorite) ? "Remove from favorites" : "Add to favorites", () => SetFavorite(items, !items.All(i => i.IsFavorite)));
        var move = new MenuItem { Header = "Move to" };
        var sub = MoveMenu(items, at);
        foreach (var it in sub.Items.Cast<Control>().ToList()) { sub.Items.Remove(it); move.Items.Add(it); }
        menu.Items.Add(move);
        Add(items.Count == 1 ? "Copy" : $"Copy {items.Count} clips", () => CopyToClipboard(items));
        if (items.Count == 1) Add("Show in folder", () => Process.Start("explorer.exe", $"/select,\"{c.Path}\""));
        menu.Items.Add(new Separator());
        Add(items.Count == 1 ? "Delete…" : $"Delete {items.Count} clips…", () => _ = DeleteAsync(items), danger: true);
        menu.IsOpen = true;
    }

    private void CopyToClipboard(List<ClipItem> items)
    {
        var list = new StringCollection();
        foreach (var c in items.Where(c => File.Exists(c.Path))) list.Add(c.Path);
        if (list.Count == 0) return;
        try { Clipboard.SetFileDropList(list); Dialogs.Toast(list.Count == 1 ? "Clip copied" : $"{list.Count} clips copied", "Paste it into a chat or a folder."); }
        catch (Exception ex) { Dialogs.Toast("Couldn't copy", ex.Message, error: true); }
    }

    private async Task RenameClipAsync(ClipItem c)
    {
        var name = await Dialogs.PromptAsync("Rename clip", "This changes the name shown in Buffered, not the file name.", c.Title, "Rename");
        if (name is null) return;
        c.Name = name.Equals(c.Label, StringComparison.Ordinal) ? null : name;
        ApplyFilter();
    }

    private async Task EditNoteAsync(ClipItem c)
    {
        var note = await Dialogs.PromptAsync("Note", null, c.Note ?? "", "Save", multiline: true);
        if (note is null) return;
        c.Note = note;
        ApplyFilter();
    }

    private void SelDelete_Click(object sender, RoutedEventArgs e) => _ = DeleteAsync(_sel.ToList());

    /// <summary>
    /// Sends clips to the Recycle Bin. Favorites are protected: they are only deleted if the user ticks the box.
    /// </summary>
    private async Task DeleteAsync(List<ClipItem> items)
    {
        if (items.Count == 0) return;
        var favorites = items.Where(c => c.IsFavorite).ToList();
        var plain = items.Where(c => !c.IsFavorite).ToList();
        List<ClipItem> toDelete;

        if (items.Count == 1)
        {
            var c = items[0];
            bool yes = await Dialogs.ConfirmAsync(c.IsFavorite ? "Delete this favorite?" : "Move this clip to the Recycle Bin?",
                $"\"{c.Title}\" ({ClipItem.SizeText(c.Size)}, {c.When:MMM d, h:mm tt}) will be moved to the Recycle Bin. You can restore it from there.",
                "Move to Recycle Bin", danger: true);
            if (!yes) return;
            toDelete = items;
        }
        else if (favorites.Count > 0)
        {
            string they = favorites.Count == 1 ? "is a favorite" : "are favorites";
            var (ok, withFavorites) = await Dialogs.ConfirmWithOptionAsync($"Move {items.Count} clips to the Recycle Bin?",
                $"{favorites.Count} of these {they}, so {(favorites.Count == 1 ? "it's" : "they're")} left out unless you choose otherwise. You can restore clips from the Recycle Bin.",
                "Move to Recycle Bin", "Delete favorites too", danger: true);
            if (!ok) return;
            toDelete = withFavorites ? items : plain;
            if (toDelete.Count == 0) { Dialogs.Toast("Nothing deleted", "Only favorites were selected."); return; }
        }
        else
        {
            bool yes = await Dialogs.ConfirmAsync($"Move {items.Count} clips to the Recycle Bin?",
                $"These {items.Count} clips ({ClipItem.SizeText(items.Sum(c => c.Size))}) will be moved to the Recycle Bin. You can restore them from there.",
                "Move to Recycle Bin", danger: true);
            if (!yes) return;
            toDelete = items;
        }

        string? error = null;
        foreach (var c in toDelete)
        {
            try
            {
                FileSystem.DeleteFile(c.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                var stems = ClipNames.StemsPath(c.Path); // the hidden companion file goes with its clip
                if (File.Exists(stems)) File.Delete(stems);
                ClipLibrary.Forget(c.Path);
            }
            catch (Exception ex) { error = ex.Message; }
        }
        SetSelection(Array.Empty<ClipItem>());
        LoadClips();
        if (error is not null) await Dialogs.MessageAsync("Couldn't delete every clip", error);
    }
}
