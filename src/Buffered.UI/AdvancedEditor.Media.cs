using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Buffered.UI;

/// <summary>One thing in the media panel that can be put on the timeline.</summary>
public sealed class MediaEntry
{
    public required string Path { get; init; }
    public required string Label { get; init; }
    public required ItemKind Kind { get; init; }
    public string Detail { get; init; } = "";
    public ImageSource? Thumb { get; init; }
    /// <summary>How long it will be on the timeline when dropped (known for library clips, a guess for the rest until it is read).</summary>
    public double Seconds { get; init; } = 5;
    public string Glyph => Thumb is not null ? "" : Kind switch { ItemKind.Image => "", ItemKind.Audio => "", _ => "" };
}

/// <summary>The advanced editor's media panel (your clips and imported files), text, and the picture's text and image layer.</summary>
public partial class AdvancedEditor
{
    /// <summary>Gives the clips in the library, for the media panel.</summary>
    public Func<IEnumerable<ClipItem>>? Library { get; set; }

    private readonly ObservableCollection<MediaEntry> _entries = new();
    private readonly List<MediaEntry> _imported = new();
    private static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif" };
    private static readonly string[] AudioExt = { ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".aac", ".opus" };

    private void InitMedia()
    {
        MediaList.ItemsSource = _entries;
        Tl.MediaDropped += (data, lane, time) => { if (data is MediaEntry e) _ = AddMediaAsync(e, time, lane); };
        foreach (var hex in new[] { "#FFFFFF", "#111111", "#FFD84D", "#FF6B6B", "#6BCB77", "#4D9DFF", "#C77DFF" })
        {
            var sw = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Tag = hex,
                                  Background = (Brush)new BrushConverter().ConvertFromString(hex)!, BorderBrush = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)), BorderThickness = new Thickness(1) };
            sw.MouseLeftButtonDown += (_, _) => ApplyToSelection(o => o with { Color = hex }, "colour");
            Swatches.Children.Add(sw);
        }
        foreach (var hex in new[] { "#000000", "#FFFFFF", "#FF6B6B", "#4D9DFF", "#FFD84D" })
            OutlineSwatches.Children.Add(Dot(hex, () => ApplyToSelection(o => o with { OutlineColor = hex, Outline = o.Outline <= 0 ? 0.06 : o.Outline }, "outline")));
        BoxSwatches.Children.Add(Dot(null, () => ApplyToSelection(o => o with { Box = null }, "box")));
        foreach (var hex in new[] { "#000000", "#FFFFFF", "#FF6B6B", "#4D9DFF", "#FFD84D", "#6BCB77" })
            BoxSwatches.Children.Add(Dot(hex, () => ApplyToSelection(o => o with { Box = hex }, "box")));
        foreach (var f in new[] { "Segoe UI", "Arial", "Arial Black", "Impact", "Comic Sans MS", "Consolas", "Georgia", "Times New Roman", "Trebuchet MS", "Verdana", "Bahnschrift" })
            FontBox.Items.Add(f);
    }

    // a round colour button; no colour draws a crossed-out dot ("none")
    private static Border Dot(string? hex, Action pick)
    {
        var sw = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, ToolTip = hex ?? "No box",
                              Background = hex is null ? Brushes.Transparent : (Brush)new BrushConverter().ConvertFromString(hex)!, BorderBrush = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)), BorderThickness = new Thickness(1),
                              Child = hex is null ? new TextBlock { Text = "∅", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Opacity = 0.7 } : null };
        sw.MouseLeftButtonDown += (_, _) => pick();
        return sw;
    }

    private void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_uiBusy || FontBox.SelectedItem is not string f) return;
        ApplyToSelection(o => o with { Font = f }, "font");
    }

    private void Style_Click(object sender, RoutedEventArgs e) =>
        ApplyToSelection(o => o with { Bold = BoldBtn.IsChecked == true, Italic = ItalicBtn.IsChecked == true }, "text style");

    private void Outline_Changed(object sender, RoutedEventArgs e)
    {
        OutlineValue.Text = OutlineSlider.Value < 0.5 ? "none" : $"{OutlineSlider.Value:0}%";
        if (_uiBusy) return;
        ApplyToSelection(o => o with { Outline = OutlineSlider.Value / 100 }, "outline");
    }

    private void FillMedia()
    {
        _entries.Clear();
        foreach (var c in (Library?.Invoke() ?? Enumerable.Empty<ClipItem>()).OrderByDescending(c => c.When))
            _entries.Add(new MediaEntry { Path = c.Path, Label = c.Label, Kind = ItemKind.Video, Detail = c.Subtitle, Thumb = c.Thumb, Seconds = ParseClock(c.Duration) });
        foreach (var e in _imported) _entries.Add(e);
    }

    /// <summary>"5:01" or "1:02:03" to seconds; 5 if it is not known yet.</summary>
    private static double ParseClock(string text)
    {
        var parts = (text ?? "").Split(':');
        double total = 0;
        foreach (var part in parts) { if (!double.TryParse(part, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return 5; total = total * 60 + v; }
        return total > 0 ? total : 5;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (MediaTab is null) return;
        bool media = TabMedia.IsChecked == true;
        MediaTab.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        TextTab.Visibility = media ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true, Title = "Import",
            Filter = "Videos, pictures, sound and subtitles|*.srt;*.mp4;*.mkv;*.mov;*.webm;*.avi;*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif;*.mp3;*.wav;*.m4a;*.flac;*.ogg;*.aac;*.opus|All files|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        ImportFiles(dlg.FileNames);
    }

    /// <summary>True for a video, picture or sound file the editor can use.</summary>
    public static bool IsMediaFile(string path)
    {
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ImageExt.Contains(ext) || AudioExt.Contains(ext) || ext is ".mp4" or ".mkv" or ".mov" or ".webm" or ".avi" or ".srt";
    }

    /// <summary>Turns a subtitle file into text items (one per line of subtitles) at the times the file gives.</summary>
    private void ImportSubtitles(string path)
    {
        List<Subtitles.Cue> cues;
        try { cues = Subtitles.ParseSrt(File.ReadAllText(path)); }
        catch (IOException) { StatusText.Text = "Couldn't read that subtitle file"; return; }
        if (cues.Count == 0) { StatusText.Text = "No subtitles found in that file"; return; }
        var p = _h.Current;
        foreach (var c in cues)
        {
            var props = new ItemProps(Text: c.Text, TextSize: 0.06, PosY: 0.88, Outline: 0.08);
            p = PlaceOrCreate(p, TrackKind.Text, new TimelineItem(Guid.NewGuid(), ItemKind.Text, "", 0, c.End - c.Start, c.Start, props, null, c.Text.Replace('\n', ' ')), true, "Subtitles");
        }
        _h.Apply(p);
        AfterEdit();
        StatusText.Text = $"Added {cues.Count} subtitles from {System.IO.Path.GetFileName(path)}";
    }

    /// <summary>Adds files (from the Import button or dropped on the window) to the media list.</summary>
    public void ImportFiles(IEnumerable<string> files)
    {
        TabMedia.IsChecked = true;
        files = files.ToList();
        foreach (var srt in files.Where(f => System.IO.Path.GetExtension(f).Equals(".srt", StringComparison.OrdinalIgnoreCase))) ImportSubtitles(srt);
        foreach (var f in files.Where(f => !System.IO.Path.GetExtension(f).Equals(".srt", StringComparison.OrdinalIgnoreCase)).Where(f => IsMediaFile(f) && !_imported.Any(i => string.Equals(i.Path, f, StringComparison.OrdinalIgnoreCase))))
        {
            string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
            var kind = ImageExt.Contains(ext) ? ItemKind.Image : AudioExt.Contains(ext) ? ItemKind.Audio : ItemKind.Video;
            ImageSource? thumb = null;
            if (kind == ItemKind.Image)
            {
                try { var b = new BitmapImage(); b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelWidth = 112; b.UriSource = new Uri(f); b.EndInit(); b.Freeze(); thumb = b; }
                catch (Exception ex) when (ex is IOException or NotSupportedException) { }
            }
            var entry = new MediaEntry { Path = f, Label = System.IO.Path.GetFileNameWithoutExtension(f), Kind = kind, Thumb = thumb, Detail = kind == ItemKind.Image ? "Picture" : kind == ItemKind.Audio ? "Sound" : "Video" };
            _imported.Add(entry); _entries.Add(entry);
        }
    }

    // dragging out of the list
    private Point _dragFrom; private MediaEntry? _dragEntry;
    private void MediaList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(MediaList);
        _dragEntry = (ItemsControl.ContainerFromElement(MediaList, (DependencyObject)e.OriginalSource) as ListBoxItem)?.DataContext as MediaEntry;
    }

    private void MediaList_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragEntry is not { } entry) return;
        var p = e.GetPosition(MediaList);
        if (Math.Abs(p.X - _dragFrom.X) < 8 && Math.Abs(p.Y - _dragFrom.Y) < 8) return;
        _dragEntry = null;
        ShowDragGhost(entry);
        try { DragDrop.DoDragDrop(MediaList, new DataObject("buffered.media", entry), DragDropEffects.Copy); }
        finally { HideDragGhost(); }
    }

    // A picture that follows the mouse while something is dragged out of the list (the system drag shows only a cursor).
    private System.Windows.Controls.Primitives.Popup? _ghost;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    private void ShowDragGhost(MediaEntry entry)
    {
        var card = new Border
        {
            Width = 150, Padding = new Thickness(6), CornerRadius = new CornerRadius(6), Opacity = 0.92,
            Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), BorderBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xD3, 0x70)), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.5 },
        };
        var stack = new StackPanel();
        var pic = new Border { Height = 64, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)), ClipToBounds = true };
        pic.Child = entry.Thumb is not null ? new Image { Source = entry.Thumb, Stretch = Stretch.UniformToFill } : new TextBlock { Text = entry.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 24, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(pic);
        stack.Children.Add(new TextBlock { Text = entry.Label, Margin = new Thickness(0, 5, 0, 0), Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        card.Child = stack;
        _ghost = new System.Windows.Controls.Primitives.Popup { AllowsTransparency = true, Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute, IsHitTestVisible = false, StaysOpen = true, Child = card };
        MoveGhost();
        _ghost.IsOpen = true;
        MediaList.GiveFeedback -= Drag_Feedback; MediaList.GiveFeedback += Drag_Feedback;
    }

    private void Drag_Feedback(object sender, GiveFeedbackEventArgs e) => MoveGhost();

    private void MoveGhost()
    {
        if (_ghost is null || !GetCursorPos(out var px)) return;
        var src = PresentationSource.FromVisual(this);
        var dip = src?.CompositionTarget is { } ct ? ct.TransformFromDevice.Transform(new Point(px.X, px.Y)) : new Point(px.X, px.Y);
        _ghost.HorizontalOffset = dip.X + 14; _ghost.VerticalOffset = dip.Y + 14;
    }

    private void HideDragGhost()
    {
        MediaList.GiveFeedback -= Drag_Feedback;
        if (_ghost is not null) { _ghost.IsOpen = false; _ghost = null; }
    }

    private void MediaList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((ItemsControl.ContainerFromElement(MediaList, (DependencyObject)e.OriginalSource) as ListBoxItem)?.DataContext is MediaEntry entry)
            _ = AddMediaAsync(entry, _t, -1);
    }

    // ---- putting things on the timeline

    /// <summary>The lanes of a kind, in the order they should be tried, and the first one the item fits on gets it.</summary>
    private static Project? PlaceOn(Project p, TrackKind kind, TimelineItem item, bool fromTop, int hintLane = -1, int nth = -1)
    {
        var lanes = Enumerable.Range(0, p.Tracks.Count).Where(i => p.Tracks[i].Kind == kind && !p.Tracks[i].Locked).ToList();
        var order = new List<int>();
        if (hintLane >= 0 && lanes.Contains(hintLane)) order.Add(hintLane);
        if (nth >= 0 && nth < lanes.Count) order.Add(lanes[nth]);
        order.AddRange(fromTop ? lanes : Enumerable.Reverse(lanes));
        foreach (var i in order.Distinct()) if (p.Add(i, item) is { } placed) return placed;
        return null;
    }

    /// <summary>As <see cref="PlaceOn"/>, but makes a new lane if there is no room anywhere.</summary>
    private static Project PlaceOrCreate(Project p, TrackKind kind, TimelineItem item, bool fromTop, string laneName, int hintLane = -1, int nth = -1)
    {
        if (PlaceOn(p, kind, item, fromTop, hintLane, nth) is { } placed) return placed;
        var grown = p.AddTrack(kind, laneName);
        return grown.Add(kind == TrackKind.Audio ? grown.Tracks.Count - 1 : 0, item) ?? p;
    }

    // ---- jobs: whenever something takes a while, a moving bar and what it is doing show beside the play button

    private sealed class JobScope : IDisposable
    {
        private readonly Action _end; private bool _done;
        public JobScope(Action end) => _end = end;
        public void Dispose() { if (_done) return; _done = true; _end(); }
    }

    private readonly List<string> _jobs = new();

    private IDisposable Job(string text)
    {
        _jobs.Add(text); ShowJobs();
        return new JobScope(() => { _jobs.Remove(text); ShowJobs(); });
    }

    private void ShowJobs()
    {
        JobPanel.Visibility = _jobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_jobs.Count > 0) JobText.Text = _jobs[^1] + (_jobs.Count > 1 ? $"  (+{_jobs.Count - 1} more)" : "");
    }

    private async Task AddMediaAsync(MediaEntry e, double time, int hintLane)
    {
        using var job = Job($"Adding {e.Label}…");
        var pendingId = Guid.NewGuid();
        Tl.AddPending(new TimelineView.PendingBlock(pendingId, LaneWithRoom(e, time, hintLane), time, e.Seconds, e.Label));
        try { await AddMediaCoreAsync(e, time, hintLane); }
        finally { Tl.RemovePending(pendingId); }
    }

    /// <summary>The lane the new item will really land on (the first one with room), for the placeholder; -1 if a new lane will be made.</summary>
    private int LaneWithRoom(MediaEntry e, double time, int hintLane)
    {
        var p = _h.Current;
        var kind = e.Kind == ItemKind.Audio ? TrackKind.Audio : TrackKind.Video;
        var probe = new TimelineItem(Guid.NewGuid(), e.Kind, e.Path, 0, e.Seconds, time, new ItemProps());
        var lanes = Enumerable.Range(0, p.Tracks.Count).Where(i => p.Tracks[i].Kind == kind && !p.Tracks[i].Locked).ToList();
        var order = new List<int>();
        if (hintLane >= 0 && lanes.Contains(hintLane)) order.Add(hintLane);
        order.AddRange(e.Kind == ItemKind.Image ? lanes : Enumerable.Reverse(lanes));   // pictures try the top lane first; videos the main lane; sound the first sound lane order used when placing
        foreach (var i in order.Distinct()) if (p.Add(i, probe) is not null) return i;
        return -1;
    }

    private async Task AddMediaCoreAsync(MediaEntry e, double time, int hintLane)
    {
        switch (e.Kind)
        {
            case ItemKind.Image:
            {
                // a GIF lasts as long as one run through it; a still picture lasts 5 seconds. Either can be stretched by dragging its edge: a GIF just starts over.
                double length = GifAnimation.IsGif(e.Path) && GifFor(e.Path) is { Total: > 0 } gif ? Math.Max(gif.Total, 0.5) : 5;
                var item = new TimelineItem(Guid.NewGuid(), ItemKind.Image, e.Path, 0, length, time, new ItemProps(), null, e.Label);
                _h.Apply(PlaceOrCreate(_h.Current, TrackKind.Video, item, true, "Overlay", hintLane));
                AfterEdit();
                break;
            }
            case ItemKind.Audio:
            {
                double dur = await Media.GetDurationSecondsAsync(e.Path) ?? 0;
                if (dur <= 0) { StatusText.Text = "Couldn't read that sound file"; return; }
                TimelineView.SourceLengths[e.Path] = dur;
                if (!_mixer.Has($"{e.Path}#0"))
                {
                    if (await Media.DecodeTracksAsync(e.Path, null, 1) is { } pcm)
                    { _pcmFiles.AddRange(pcm); _mixer.AddSource($"{e.Path}#0", pcm[0]); _ = DrawWaveAsync($"{e.Path}#0", pcm[0]); }
                    else StatusText.Text = $"{e.Label}: this sound can't be read by the video tools in this version of Buffered ({Path.GetExtension(e.Path)} files aren't supported yet)";
                }
                var item = new TimelineItem(Guid.NewGuid(), ItemKind.Audio, e.Path, 0, dur, time, new ItemProps(), null, e.Label);
                _h.Apply(PlaceOrCreate(_h.Current, TrackKind.Audio, item, false, "Sound", hintLane));
                AfterEdit();
                break;
            }
            default:
                await AddClipAsync(e.Path, e.Label, time, hintLane);
                break;
        }
    }

    /// <summary>A video: its picture on a picture lane and each of its sound tracks (if it has a companion file) on a sound lane, linked.</summary>
    private async Task AddClipAsync(string path, string label, double time, int hintLane)
    {
        double dur = await Media.GetDurationSecondsAsync(path) ?? 0;
        if (dur <= 0) { StatusText.Text = "Couldn't read that file's length"; return; }
        TimelineView.SourceLengths[path] = dur;
        _ = MakeThumbsAsync(path, _openId);
        string stems = ClipNames.StemsPath(path);
        var names = File.Exists(stems) ? await Media.ReadStemTracksAsync(stems) : new List<StemTrack>();
        string soundSource = names.Count > 0 ? stems : path;
        if (names.Count > 0) TimelineView.SourceLengths[stems] = dur;
        if (!_mixer.Has($"{soundSource}#0")) await PrepareSoundAsync(path, names.Count > 0 ? stems : null, Math.Max(1, names.Count), _openId);

        var link = Guid.NewGuid();
        var p = PlaceOrCreate(_h.Current, TrackKind.Video, new TimelineItem(Guid.NewGuid(), ItemKind.Video, path, 0, dur, time, new ItemProps(), link, label), false, "Video", hintLane);
        for (int i = 0; i < Math.Max(1, names.Count); i++)
            p = PlaceOrCreate(p, TrackKind.Audio, new TimelineItem(Guid.NewGuid(), ItemKind.Audio, soundSource, 0, dur, time, new ItemProps(), link, $"{label} - {(names.Count > 0 ? names[i].Name : "Sound")}", i), false, "Sound", -1, i);
        _h.Apply(p);
        AfterEdit();
    }

    private void AddHeading_Click(object sender, RoutedEventArgs e) => AddText("Your heading", 0.12, 0.42);
    private void AddSubtitle_Click(object sender, RoutedEventArgs e) => AddText("Your subtitle", 0.07, 0.58);
    private void AddBody_Click(object sender, RoutedEventArgs e) => AddText("Some text", 0.045, 0.85);

    private void AddText(string text, double size, double down)
    {
        var item = new TimelineItem(Guid.NewGuid(), ItemKind.Text, "", 0, 3, _t, new ItemProps(Text: text, TextSize: size, PosY: down), null, text);
        _h.Apply(PlaceOrCreate(_h.Current, TrackKind.Text, item, true, "Text"));
        AfterEdit();
        Tl.SetSelection(new[] { item.Id }); _selected = item.Id; ShowProps(); RenderOverlay();
    }

    // ---- the picture's own size and the text and images drawn over it

    private double _aspect = 16.0 / 9;
    private bool _aspectSet;
    private readonly Dictionary<Guid, Border> _layer = new();
    private readonly Dictionary<string, BitmapImage?> _pictures = new(StringComparer.OrdinalIgnoreCase);

    private void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => FitStage();

    private void FitStage()
    {
        double w = StageHost.ActualWidth, h = StageHost.ActualHeight;
        if (w < 20 || h < 20) return;
        double sw = Math.Min(w, h * _aspect);
        Stage.Width = sw; Stage.Height = sw / _aspect;
        RenderOverlay();
    }

    private readonly Dictionary<string, GifAnimation?> _gifs = new(StringComparer.OrdinalIgnoreCase);

    private GifAnimation? GifFor(string path)
    {
        if (!_gifs.TryGetValue(path, out var g)) _gifs[path] = g = GifAnimation.Load(path);
        return g;
    }

    private BitmapImage? PictureFor(string path)
    {
        if (_pictures.TryGetValue(path, out var have)) return have;
        BitmapImage? img = null;
        try { img = new BitmapImage(); img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.DecodePixelWidth = 1280; img.UriSource = new Uri(path); img.EndInit(); img.Freeze(); }
        catch (Exception ex) when (ex is IOException or NotSupportedException) { img = null; }
        _pictures[path] = img;
        return img;
    }

    private void RenderOverlay() => RenderOverlay(_h.Current, _t);

    /// <summary>Shows the text and pictures that are under the playhead, over the video, with their opacity, fades, place and size.</summary>
    private void RenderOverlay(Project p, double t)
    {
        double W = Stage.Width, H = Stage.Height;
        var active = p.Tracks.Where(tr => !tr.Hidden).SelectMany(tr => tr.Items)
            .Where(i => i.Kind is ItemKind.Text or ItemKind.Image && t >= i.Start - 1e-9 && t < i.End).ToList();
        foreach (var id in _layer.Keys.Where(k => active.All(a => a.Id != k)).ToList()) { Overlay.Children.Remove(_layer[id]); _layer.Remove(id); }
        foreach (var it in active)
        {
            double at = t - it.Start;
            double scale = it.Props.ValueAt(KeyProp.Scale, at), posX = it.Props.ValueAt(KeyProp.PosX, at), posY = it.Props.ValueAt(KeyProp.PosY, at);
            if (!_layer.TryGetValue(it.Id, out var box))
            {
                box = new Border { Tag = it.Id, Cursor = Cursors.SizeAll, BorderBrush = Brushes.White, Background = Brushes.Transparent };
                box.MouseLeftButtonDown += Layer_Down;
                _layer[it.Id] = box; Overlay.Children.Add(box);
            }
            if (it.Kind == ItemKind.Text)
            {
                if (box.Child is not OutlinedText tb)
                    box.Child = tb = new OutlinedText { Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.75 } };
                tb.Style(it.Props, H * it.Props.ValueAt(KeyProp.TextSize, at) * scale);
            }
            else
            {
                if (box.Child is not Image img) box.Child = img = new Image { Stretch = Stretch.Uniform };
                // an animated GIF shows the picture for this moment and starts over when it has run through
                img.Source = GifAnimation.IsGif(it.Source) && GifFor(it.Source) is { } gif ? gif.Frames[gif.IndexAt((t - it.Start) * it.Props.GifSpeed)] : PictureFor(it.Source);
                img.Width = W * 0.4 * scale;
            }
            double into = t - it.Start, left = it.End - t, env = 1;
            var (fadeIn, fadeOut) = it.Fades;
            if (fadeIn > 0 && into < fadeIn) env = Math.Min(env, into / fadeIn);
            if (fadeOut > 0 && left < fadeOut) env = Math.Min(env, left / fadeOut);
            box.Opacity = Math.Clamp(it.Props.ValueAt(KeyProp.Opacity, at) * env, 0, 1);
            box.BorderThickness = new Thickness(Tl.Selection.Contains(it.Id) ? 1.5 : 0);
            box.RenderTransformOrigin = new Point(0.5, 0.5);
            double turn = it.Props.ValueAt(KeyProp.Rotation, at);
            box.RenderTransform = Math.Abs(turn) < 0.01 ? Transform.Identity : new RotateTransform(turn);
            box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (it.Kind == ItemKind.Image && box.Child is Image shown)
            {
                var c = it.Props;
                double iw = box.DesiredSize.Width - 2 * box.BorderThickness.Left, ih = box.DesiredSize.Height - 2 * box.BorderThickness.Top;
                shown.Clip = c.HasCrop ? new RectangleGeometry(new Rect(c.CropL * iw, c.CropT * ih, iw * (1 - c.CropL - c.CropR), ih * (1 - c.CropT - c.CropB))) : null;
            }
            Canvas.SetLeft(box, posX * W - box.DesiredSize.Width / 2);
            Canvas.SetTop(box, posY * H - box.DesiredSize.Height / 2);
            if (it.Id == _selected) PlaceScaleHandle(it, box);
        }
        if (_scaleHandle is not null && (_selected is not { } sid || active.All(a => a.Id != sid))) { Overlay.Children.Remove(_scaleHandle); _scaleHandle = null; }
    }

    // a square on the corner of the selected text or picture: drag it to make it bigger or smaller
    private Border? _scaleHandle;
    private Guid? _scalingId; private double _scaleStart, _scaleStartDistance;

    private void PlaceScaleHandle(TimelineItem it, Border box)
    {
        if (_scaleHandle is null)
        {
            _scaleHandle = new Border { Width = 13, Height = 13, Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Cursor = Cursors.SizeNWSE };
            _scaleHandle.MouseLeftButtonDown += ScaleHandle_Down;
            Overlay.Children.Add(_scaleHandle);
        }
        Canvas.SetLeft(_scaleHandle, Canvas.GetLeft(box) + box.DesiredSize.Width - 7);
        Canvas.SetTop(_scaleHandle, Canvas.GetTop(box) + box.DesiredSize.Height - 7);
        Panel.SetZIndex(_scaleHandle, 10);
    }

    private (double X, double Y) CenterOf(TimelineItem it) => (it.Props.ValueAt(KeyProp.PosX, _t - it.Start) * Stage.Width, it.Props.ValueAt(KeyProp.PosY, _t - it.Start) * Stage.Height);

    private void ScaleHandle_Down(object sender, MouseButtonEventArgs e)
    {
        if (_selected is not { } id || _h.Current.Find(id) is not { } it) return;
        var c = CenterOf(it); var m = e.GetPosition(Overlay);
        _scalingId = id; _scaleStart = it.Props.ValueAt(KeyProp.Scale, _t - it.Start); _scaleStartDistance = Math.Max(8, Math.Sqrt((m.X - c.X) * (m.X - c.X) + (m.Y - c.Y) * (m.Y - c.Y)));
        Overlay.CaptureMouse();
        e.Handled = true;
    }

    // dragging text and pictures in the preview
    private Guid? _layerId; private Point _layerFrom; private (double X, double Y) _layerStart;

    private void Layer_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: Guid id } || _h.Current.Find(id) is not { } item) return;
        Tl.SetSelection(new[] { id }); _selected = id; ShowProps(); RenderOverlay();
        _layerId = id; _layerFrom = e.GetPosition(Overlay); _layerStart = (item.Props.ValueAt(KeyProp.PosX, _t - item.Start), item.Props.ValueAt(KeyProp.PosY, _t - item.Start));
        Overlay.CaptureMouse();
        e.Handled = true;
    }

    private void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_scalingId is { } sid && e.LeftButton == MouseButtonState.Pressed && _h.Current.Find(sid) is { } sit)
        {
            var c = CenterOf(sit); var m = e.GetPosition(Overlay);
            double dist = Math.Sqrt((m.X - c.X) * (m.X - c.X) + (m.Y - c.Y) * (m.Y - c.Y));
            double scale = Math.Clamp(_scaleStart * dist / _scaleStartDistance, 0.05, 6);
            _h.Apply(WithValue(_h.Current, sid, KeyProp.Scale, scale), "scale:" + sid + (_h.Current.Find(sid)!.Props.HasKeys(KeyProp.Scale) ? ":" + Math.Round(_t, 2) : ""));
            Tl.Project = _h.Current;
            UndoButton.IsEnabled = _h.CanUndo; RedoButton.IsEnabled = _h.CanRedo;
            RenderOverlay(); ShowProps();
            return;
        }
        if (_layerId is not { } id || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(Overlay);
        double nx = Math.Clamp(_layerStart.X + (pos.X - _layerFrom.X) / Stage.Width, 0, 1), ny = Math.Clamp(_layerStart.Y + (pos.Y - _layerFrom.Y) / Stage.Height, 0, 1);
        _h.Apply(WithValue(WithValue(_h.Current, id, KeyProp.PosX, nx), id, KeyProp.PosY, ny), "place:" + id + (_h.Current.Find(id)!.Props.HasKeys(KeyProp.PosX) || _h.Current.Find(id)!.Props.HasKeys(KeyProp.PosY) ? ":" + Math.Round(_t, 2) : ""));
        Tl.Project = _h.Current;
        UndoButton.IsEnabled = _h.CanUndo; RedoButton.IsEnabled = _h.CanRedo;
        RenderOverlay(); ShowProps();
    }

    private void Overlay_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _layerId = null; _scalingId = null;
        Overlay.ReleaseMouseCapture();
    }

    // ---- text and placement controls

    private void WordsBox_Changed(object sender, TextChangedEventArgs e)
    {
        if (_uiBusy) return;
        ApplyToSelection(o => o with { Text = WordsBox.Text }, "words");
    }

    /// <summary>Changes the same property of every selected item (the others keep their own values).</summary>
    private void ApplyToSelection(Func<ItemProps, ItemProps> change, string key)
    {
        if (_selected is not { } primary) return;
        var next = _h.Current;
        foreach (var id in Tl.Selection.DefaultIfEmpty(primary)) next = next.SetProps(id, change);
        _h.Apply(next, $"props:{primary}:{key}");
        Tl.Project = _h.Current; PushLanes();
        UndoButton.IsEnabled = _h.CanUndo; RedoButton.IsEnabled = _h.CanRedo;
        RenderOverlay(); Sync(false);
    }
}
