using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Buffered.UI;

/// <summary>Saving, opening and protecting the advanced editor's edit: project files, autosave, and asking before anything unsaved is thrown away.</summary>
public partial class AdvancedEditor
{
    /// <summary>Where questions are asked (set by the main window).</summary>
    public DialogHost? Dialogs { get; set; }

    private const string Extension = ".bfedit";
    private static readonly string ProjectsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered", "projects");

    private string? _projectPath;                 // the file this edit is saved in, once it has been saved
    private string _projectName = "";
    private string _savedPool = "";               // the media pool as it was last saved or opened
    private Project? _savedProject;               // the edit as it was last saved or opened: anything else is unsaved
    private string? _clipKey;                     // the clip this editor was opened from (for its autosave)
    private readonly DispatcherTimer _autosaveTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private bool _leaving;

    private bool _hasSession;

    /// <summary>True while an edit is open (with or without unsaved changes).</summary>
    public bool HasSession => _hasSession;

    /// <summary>Shown when the Editor page is opened: the open edit if there is one, otherwise the start page.</summary>
    public void ShowOrStart()
    {
        StartPanel.Visibility = _hasSession ? Visibility.Collapsed : Visibility.Visible;
        if (_hasSession) { Focus(); return; }
        var recent = RecentEdits(10);
        RecentList.ItemsSource = recent.Select(f => new RecentEdit(f.FullName, Path.GetFileNameWithoutExtension(f.Name), $"{f.LastWriteTime:d MMM yyyy, HH:mm}")).ToList();
        RecentHeader.Visibility = recent.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentList.Visibility = RecentHeader.Visibility;
    }

    private sealed record RecentEdit(string Path, string Name, string When);

    /// <summary>Stops playing (when the page is left but the edit stays open).</summary>
    public void Pause() { if (_playing) TogglePlay(); }

    private void StartNew_Click(object sender, RoutedEventArgs e) => OpenBlank();
    private async void StartOpen_Click(object sender, RoutedEventArgs e) { if (PickEditFile() is { } path) await OpenFileAsync(path); }
    private async void Recent_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) { if (RecentList.SelectedItem is RecentEdit r) await OpenFileAsync(r.Path); }

    /// <summary>True when there is work that has not been saved.</summary>
    public bool IsDirty => _savedProject is not null && (!ReferenceEquals(_savedProject, _h.Current) || PoolKey() != _savedPool);

    private void InitProject()
    {
        _autosaveTimer.Tick += (_, _) => AutoSave();
        _autosaveTimer.Start();
        var dirtyWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        dirtyWatch.Tick += (_, _) => { bool d = IsDirty; DirtyDot.Visibility = d ? Visibility.Visible : Visibility.Collapsed; };
        dirtyWatch.Start();
    }

    private string AutoPath(string key) => Path.Combine(ProjectsDir, "autosave", Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16] + Extension);

    private ProjectFile Snapshot() =>
        new(1, string.IsNullOrWhiteSpace(_projectName) ? TitleText.Text : _projectName, _aspect, _h.Current, new Dictionary<string, double>(TimelineView.SourceLengths, StringComparer.OrdinalIgnoreCase), _imported.Select(e => e.Path).ToList());

    /// <summary>Called when a clip is opened in the editor: forgets the last edit's file and looks for an unsaved edit of this clip.</summary>
    private Task BeginProjectAsync(ClipItem clip) => BeginProjectCoreAsync(clip.Path, clip.Label, "this clip");

    private async Task BeginProjectCoreAsync(string key, string name, string what)
    {
        _projectPath = null; _projectName = name; _clipKey = key;
        _savedProject = _h.Current; _savedPool = PoolKey();
        if (File.Exists(AutoPath(key)) && ProjectFile.FromJson(await File.ReadAllTextAsync(AutoPath(key))) is { } auto && auto.Project.Items.Any() && Dialogs is not null)
        {
            var when = File.GetLastWriteTime(AutoPath(key));
            if (await Dialogs.ConfirmAsync("Restore your unsaved edit?", $"There is an edit of {what} from {when:g} that was never saved.", "Restore", cancelText: "Start fresh"))
                LoadProject(auto, null, unsaved: true);
            else DeleteAutoSave();
        }
    }

    private const string BlankKey = "new-edit";

    /// <summary>Opens the editor with an empty timeline: no clip needed, add things from the media panel.</summary>
    public void OpenBlank()
    {
        Release(); _hasSession = true; StartPanel.Visibility = Visibility.Collapsed;
        FillMedia();
        _mixer = new TimelineMixer(); _ = _mixer.PrepareAsync();
        _h = new EditHistory(Project.Empty((TrackKind.Video, "Overlay"), (TrackKind.Video, "Video"), (TrackKind.Audio, "Sound")));
        _aspect = 16.0 / 9; _aspectSet = false; FitStage(); ShowAspect();
        TitleText.Text = "New edit";
        _t = 0; _selected = null; Tl.SetSelection(Array.Empty<Guid>());
        Tl.Project = _h.Current; Tl.Playhead = 0; Tl.PixelsPerSecond = 40; Tl.ScrollTime = 0;
        AfterEdit();
        Focus();
        _ = BeginProjectCoreAsync(BlankKey, "New edit", "a new edit");
    }

    /// <summary>Opens a saved edit file straight into the editor, without any clip.</summary>
    public async Task OpenFileAsync(string path)
    {
        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (IOException ex) { Dialogs?.Toast("Couldn't open that file", ex.Message, error: true); return; }
        if (ProjectFile.FromJson(text) is not { } file) { Dialogs?.Toast("That isn't a Buffered edit", Path.GetFileName(path), error: true); return; }
        Release(); _hasSession = true; StartPanel.Visibility = Visibility.Collapsed;
        FillMedia();
        _h = new EditHistory(Project.Empty());
        _clipKey = file.Project.Items.FirstOrDefault(i => i.Kind == ItemKind.Video)?.Source ?? BlankKey;
        LoadProject(file, path, unsaved: false);
        Focus();
    }

    /// <summary>The most recently saved edits, newest first (not the automatic copies).</summary>
    public static List<FileInfo> RecentEdits(int count)
    {
        try
        {
            return Directory.Exists(ProjectsDir) ? new DirectoryInfo(ProjectsDir).GetFiles("*" + Extension).OrderByDescending(f => f.LastWriteTime).Take(count).ToList() : new();
        }
        catch (IOException) { return new(); }
    }

    public static void ShowEditsFolder() { Directory.CreateDirectory(ProjectsDir); System.Diagnostics.Process.Start("explorer.exe", ProjectsDir); }

    /// <summary>Asks for a saved edit file. Null if cancelled.</summary>
    public static string? PickEditFile()
    {
        Directory.CreateDirectory(ProjectsDir);
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Open a saved edit", Filter = $"Buffered edit|*{Extension}", InitialDirectory = ProjectsDir };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void DeleteAutoSave() { try { if (_clipKey is not null) File.Delete(AutoPath(_clipKey)); } catch (IOException) { } }

    private void AutoSave()
    {
        if (!IsDirty || _clipKey is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutoPath(_clipKey))!);
            File.WriteAllText(AutoPath(_clipKey), Snapshot().ToJson());
        }
        catch (IOException) { }
    }

    // ---- save and open

    private void Project_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ProjectButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string text, Action act, bool enabled = true) { var m = new MenuItem { Header = text, IsEnabled = enabled }; m.Click += (_, _) => act(); menu.Items.Add(m); }
        Add("Save    Ctrl+S", () => SaveProject(false));
        Add("Save as…", () => SaveProject(true));
        menu.Items.Add(new Separator());
        Add("Save a portable copy with its media…", () => _ = SavePortableAsync());
        Add("Open a saved edit…", () => _ = OpenProjectAsync());
        foreach (var f in RecentEdits(5)) { var file = f; Add($"      {Path.GetFileNameWithoutExtension(file.Name)}   ·   {file.LastWriteTime:d MMM}", () => _ = OpenRecentAsync(file.FullName)); }
        Add("Show the folder with saved edits", () => { Directory.CreateDirectory(ProjectsDir); System.Diagnostics.Process.Start("explorer.exe", ProjectsDir); });
        menu.IsOpen = true;
    }

    /// <summary>Saves the edit. Returns false if the user cancelled the file dialog or it could not be written.</summary>
    private bool SaveProject(bool asNew)
    {
        string? path = _projectPath;
        if (asNew || path is null)
        {
            Directory.CreateDirectory(ProjectsDir);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save this edit", Filter = $"Buffered edit|*{Extension}", DefaultExt = Extension, InitialDirectory = ProjectsDir,
                FileName = string.Concat((string.IsNullOrWhiteSpace(_projectName) ? TitleText.Text : _projectName).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim(),
            };
            if (dlg.ShowDialog() != true) return false;
            path = dlg.FileName;
            _projectName = Path.GetFileNameWithoutExtension(path);
        }
        try { File.WriteAllText(path, Snapshot().ToJson()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs?.Toast("Couldn't save the edit", ex.Message, error: true);
            return false;
        }
        _projectPath = path;
        _savedProject = _h.Current;
        DeleteAutoSave();
        Dialogs?.Toast("Edit saved", Path.GetFileName(path));
        return true;
    }

    private async Task OpenProjectAsync()
    {
        if (!await ConfirmLeaveAsync()) return;
        if (PickEditFile() is { } path) await OpenRecentAsync(path, skipAsk: true);
    }

    private async Task OpenRecentAsync(string path, bool skipAsk = false)
    {
        if (!skipAsk && !await ConfirmLeaveAsync()) return;
        string text;
        try { text = await File.ReadAllTextAsync(path); }
        catch (IOException ex) { Dialogs?.Toast("Couldn't open that file", ex.Message, error: true); return; }
        if (ProjectFile.FromJson(text) is not { } file) { Dialogs?.Toast("That isn't a Buffered edit", Path.GetFileName(path), error: true); return; }
        LoadProject(file, path, unsaved: false);
    }

    /// <summary>Replaces what is on the timeline with a saved edit, and gets its pictures and sound ready again.</summary>
    private void LoadProject(ProjectFile file, string? path, bool unsaved)
    {
        if (path is not null) file = file.ResolveFrom(Path.GetDirectoryName(Path.GetFullPath(path))!);   // a portable edit finds its media next to itself
        // stop and clear what belonged to the old edit (the media panel stays)
        if (_playing) TogglePlay();
        _mixer.Dispose(); _mixer = new TimelineMixer(); _ = _mixer.PrepareAsync();
        foreach (var f in _pcmFiles) Media.TryDeleteFile(f);
        _pcmFiles.Clear();
        TimelineView.Thumbs.Clear(); TimelineView.Waveforms.Clear();
        foreach (var d in _thumbDirs) { try { Directory.Delete(d, true); } catch (IOException) { } }
        _thumbDirs.Clear();
        Overlay.Children.Clear(); _layer.Clear(); _scaleHandle = null;
        int open = ++_openId;

        foreach (var kv in file.SourceLengths) TimelineView.SourceLengths[kv.Key] = kv.Value;
        if (file.Pool is not null) SetPool(file.Pool);   // the media pool belongs to the edit (an edit saved before that keeps the pool as it is)
        _h = new EditHistory(file.Project);
        _aspect = file.Aspect > 0.1 ? file.Aspect : 16.0 / 9; _aspectSet = true; FitStage(); ShowAspect();
        _projectPath = path; _projectName = file.Name; TitleText.Text = file.Name;
        _savedPool = PoolKey();
        _savedProject = unsaved ? Project.Empty() : file.Project;   // an edit restored from the autosave counts as unsaved (a different object, so it shows as changed)
        _t = 0; Tl.SetSelection(Array.Empty<Guid>()); _selected = null;
        _deferSync = true;
        AfterEdit();
        // the page may have only just been shown and not laid out yet: fit the zoom and show the picture once it has been
        WhenShown(() =>
        {
            Tl.ZoomToFit(file.Project.Duration);
            _deferSync = false; _src = null; Sync(true);
        });

        var missing = file.MissingFiles().ToList();
        if (missing.Count > 0) StatusText.Text = $"{missing.Count} file{(missing.Count == 1 ? " is" : "s are")} missing: {Path.GetFileName(missing[0])}{(missing.Count > 1 ? " and others" : "")}";

        // thumbnails for the pictures, and the sound for each source
        foreach (var src in file.Project.Items.Where(i => i.Kind == ItemKind.Video).Select(i => i.Source).Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists))
        {
            _ = MakeThumbsAsync(src, open);
            _ = PrepareSmoothCopyAsync(src, open);
        }
        foreach (var g in file.Project.Items.Where(i => i.Kind == ItemKind.Audio).GroupBy(i => i.Source, StringComparer.OrdinalIgnoreCase).Where(g => File.Exists(g.Key)))
        {
            bool isStems = g.Key.EndsWith(".stems.mka", StringComparison.OrdinalIgnoreCase);
            if (isStems)
            {
                string name = Path.GetFileName(g.Key)[1..^".stems.mka".Length];
                string clip = Path.Combine(Path.GetDirectoryName(g.Key)!, name + ".mp4");
                _ = PrepareSoundAsync(clip, g.Key, g.Max(i => i.Stream) + 1, open);
            }
            else _ = PrepareSoundAsync(g.Key, null, 1, open);
        }
        _clipKey ??= file.Project.Items.FirstOrDefault(i => i.Kind == ItemKind.Video)?.Source;
    }

    /// <summary>Runs something once the editor page has really been laid out and shown (a picture cannot be opened before that).</summary>
    private void WhenShown(Action act)
    {
        if (Video.IsLoaded && Tl.ActualWidth > 100) { Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, act); return; }
        RoutedEventHandler? h = null;
        h = (_, _) => { Video.Loaded -= h; Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, act); };
        Video.Loaded += h;
    }

    /// <summary>
    /// Makes a folder with the edit and a copy of every file it uses, so it opens anywhere, even after the originals are moved or deleted.
    /// The edit that is open is not changed.
    /// </summary>
    private async Task SavePortableAsync()
    {
        var pf = Snapshot();
        var sources = pf.Project.Items.Where(i => i.Kind != ItemKind.Text && i.Source.Length > 0).Select(i => i.Source).Concat(pf.Pool ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = sources.Where(s => !File.Exists(s)).ToList();
        if (missing.Count > 0) { Dialogs?.Toast("Can't pack this edit", $"{Path.GetFileName(missing[0])} is missing" + (missing.Count > 1 ? $" (and {missing.Count - 1} more)" : ""), error: true); return; }
        Directory.CreateDirectory(ProjectsDir);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save a portable copy (a folder with the edit and its media)", Filter = $"Buffered edit|*{Extension}", DefaultExt = Extension, InitialDirectory = ProjectsDir,
            FileName = string.Concat((string.IsNullOrWhiteSpace(_projectName) ? TitleText.Text : _projectName).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim(),
        };
        if (dlg.ShowDialog() != true) return;
        string name = Path.GetFileNameWithoutExtension(dlg.FileName);
        string folder = Path.Combine(Path.GetDirectoryName(dlg.FileName)!, name);

        using var job = Job("Packing the edit with its media…");
        try
        {
            // each source folder gets its own folder inside "media", with the original file names, so a clip stays next to its sound file
            var dirs = sources.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            long total = sources.Sum(s => new FileInfo(s).Length);
            await Task.Run(() =>
            {
                foreach (var s in sources)
                {
                    string rel = Path.Combine("media", $"m{dirs.IndexOf(Path.GetDirectoryName(s)) + 1}", Path.GetFileName(s));
                    string dest = Path.Combine(folder, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(s).Length) File.Copy(s, dest, overwrite: true);
                    map[s] = rel;
                }
            });
            var portable = pf.MapSources(s => map.TryGetValue(s, out var r) ? r : s) with { Name = name };
            await File.WriteAllTextAsync(Path.Combine(folder, name + Extension), portable.ToJson());
            Dialogs?.Toast("Portable copy saved", $"{name}  ·  {total / 1048576} MB of media", "Show folder", () => System.Diagnostics.Process.Start("explorer.exe", folder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Dialogs?.Toast("Couldn't save the portable copy", ex.Message, error: true);
        }
    }

    // ---- leaving

    /// <summary>If there is unsaved work, asks what to do with it. Returns true when it is fine to go on (saved, or the user chose not to save), false to stay.</summary>
    public async Task<bool> ConfirmLeaveAsync()
    {
        if (!IsDirty || Dialogs is null) return true;
        int answer = await Dialogs.ChoiceAsync("Save your edit?", "You have changes that haven't been saved. They will be lost if you leave now.", "Save", "Don't save", "Keep editing");
        if (answer == 0) return false;
        if (answer == 1 && !SaveProject(false)) return false;
        return true;
    }

    /// <summary>Throws away the current edit's autosave (the user chose not to keep it).</summary>
    public void DiscardUnsaved() { _savedProject = _h.Current; _savedPool = PoolKey(); DeleteAutoSave(); }
}
