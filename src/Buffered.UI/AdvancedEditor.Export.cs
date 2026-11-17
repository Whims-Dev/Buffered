using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Buffered.UI;

/// <summary>Saving the edit as a video: asks how, draws the text, runs ffmpeg with a progress window, and says where the file went.</summary>
public partial class AdvancedEditor
{
    private bool _exporting;

    private void Export_Click(object sender, RoutedEventArgs e) => _ = ExportAsync();

    private async Task ExportAsync()
    {
        if (_exporting) return;
        var project = _h.Current;
        if (!project.Items.Any()) { StatusText.Text = "There is nothing to export yet"; return; }
        if (!Tools.FfmpegAvailable) { Dialogs?.Toast("Can't export", "ffmpeg wasn't found next to Buffered", error: true); return; }

        _exporting = true;
        try
        {
            using var job = Job("Checking the video tools…");
            var missing = await MissingFiltersAsync(ExportGraph.ExtraFiltersFor(project));
            if (missing.Count > 0)
            {
                if (Dialogs is not null)
                    await Dialogs.MessageAsync("This version can't export yet", "Buffered's video tools are missing a few things the export needs (" + string.Join(", ", missing.Take(6)) + "). They arrive with the next update of Buffered.");
                return;
            }

            // everything the sound needs must be read first
            var waiting = project.Items.Where(i => i.Kind == ItemKind.Audio && project.Tracks.Any(t => t.IsHeardIn(project) && t.Items.Contains(i)) && !_mixer.Has($"{i.Source}#{i.Stream}")).ToList();
            if (waiting.Count > 0) { StatusText.Text = "The sound is still being prepared: try again in a moment"; return; }

            // the name: what the edit is called (a clip's edit gets "(edit)" so it never looks like the original), in the folder used last time
            string baseName = SafeName(string.IsNullOrWhiteSpace(_projectName) ? TitleText.Text : _projectName);
            if (baseName.Length == 0) baseName = "My video";
            string name = _projectPath is null && _clipKey is not null && _clipKey != BlankKey ? baseName + " (edit)" : baseName;
            string folder = ExportDialog.LastFolder() is { } last && Directory.Exists(last) ? last
                : Path.GetDirectoryName(_clipKey ?? "") is { Length: > 0 } d && Directory.Exists(d) ? d : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            var choice = ExportDialog.Ask(Window.GetWindow(this), _aspect, name, folder);
            if (choice is null) return;
            await RunExportAsync(project, choice);
        }
        finally { _exporting = false; }
    }

    private static string SafeName(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim();

    private static async Task<List<string>> MissingFiltersAsync(IEnumerable<string> extra)
    {
        try
        {
            var psi = new ProcessStartInfo(Tools.Ffmpeg, "-hide_banner -filters") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            string text = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            var have = text.Split('\n').Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(w => w.Length > 1).Select(w => w[1]).ToHashSet();
            return ExportGraph.RequiredFilters.Concat(extra).Where(f => !have.Contains(f)).ToList();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { return ExportGraph.RequiredFilters.Concat(extra).ToList(); }
    }

    private async Task RunExportAsync(Project project, ExportChoice choice)
    {
        double aspect = _aspect;
        // the size names the shorter side: "1080p" is 1920 x 1080 for a wide video, 1080 x 1920 for a tall one, 1080 x 1080 for a square one
        int w, h;
        if (aspect >= 1) { h = choice.Height; w = Math.Max(2, (int)Math.Round(h * aspect / 2) * 2); }
        else { w = choice.Height; h = Math.Max(2, (int)Math.Round(w / aspect / 2) * 2); }
        var options = new ExportOptions(w, h, choice.Fps, choice.Cq, choice.Crf, true);

        string work = Path.Combine(Path.GetTempPath(), "Buffered", "export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var layers = new Dictionary<Guid, string>();
        foreach (var it in project.Items.Where(i => i.Kind == ItemKind.Text))
        {
            string png = Path.Combine(work, it.Id.ToString("N") + ".png");
            RenderTextLayer(it, h, png);
            layers[it.Id] = png;
        }

        // a moving picture (GIF) is drawn out as one picture for every frame of the export, so it plays and loops exactly as in the preview
        var sequences = new Dictionary<Guid, string>();
        foreach (var it in project.Items.Where(i => i.Kind == ItemKind.Image && GifAnimation.IsGif(i.Source)))
            if (GifFor(it.Source) is { } gif) sequences[it.Id] = WriteGifSequence(it, gif, choice.Fps, work);

        var plan = ExportGraph.Build(project, options, i => _mixer.PathOf($"{i.Source}#{i.Stream}"), i => layers.TryGetValue(i.Id, out var p) ? p : null, _ => null,
            i => sequences.TryGetValue(i.Id, out var s) ? s : null);
        string temp = Path.Combine(Path.GetDirectoryName(choice.Path)!, "." + Path.GetFileNameWithoutExtension(choice.Path) + ".part.mp4");

        var progress = new ExportProgress(Window.GetWindow(this));
        var cts = new CancellationTokenSource();
        progress.CancelRequested += () => cts.Cancel();
        var run = Task.Run(async () =>
        {
            foreach (bool gpu in new[] { true, false })
            {
                var (ok, err) = await EncodeAsync(ExportGraph.Arguments(plan, options, temp, gpu), plan.Duration, p => progress.Dispatcher.BeginInvoke(() => progress.Report(p)), cts.Token);
                if (cts.IsCancellationRequested) return (false, "cancelled");
                if (ok) return (true, "");
                if (!gpu) return (false, err);       // the graphics card could not encode it: try the processor
            }
            return (false, "The encoder failed.");
        });
        _ = run.ContinueWith(_ => progress.Dispatcher.BeginInvoke(progress.Close));
        progress.ShowDialog();
        var (done, error) = await run;

        try { Directory.Delete(work, true); } catch (IOException) { }
        if (done)
        {
            try { File.Move(temp, choice.Path, overwrite: true); }
            catch (IOException ex) { Dialogs?.Toast("Couldn't save the video", ex.Message, error: true); return; }
            Dialogs?.Toast("Exported", Path.GetFileName(choice.Path), "Show in folder", () => Process.Start("explorer.exe", $"/select,\"{choice.Path}\""));
        }
        else
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            if (error != "cancelled") Dialogs?.Toast("The export failed", error.Length > 140 ? error[..140] + "…" : error, error: true);
            else Dialogs?.Toast("Export cancelled");
        }
    }

    private static async Task<(bool Ok, string Error)> EncodeAsync(List<string> args, double length, Action<double> progress, CancellationToken cancel)
    {
        try
        {
            var psi = new ProcessStartInfo(Tools.Ffmpeg) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            ProcessPriority.TrySet(p, ProcessPriorityClass.BelowNormal);   // the export never takes the machine from a game
            var err = new StringBuilder();
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) err.AppendLine(e.Data); };
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line && line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out var us) && us > 0) progress(Math.Clamp(us / 1e6 / length, 0, 0.99));
            };
            p.BeginErrorReadLine(); p.BeginOutputReadLine();
            using var reg = cancel.Register(() => { try { p.Kill(true); } catch (InvalidOperationException) { } });
            await p.WaitForExitAsync();
            return p.ExitCode == 0 && !cancel.IsCancellationRequested ? (true, "") : (false, err.ToString().Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static string WriteGifSequence(TimelineItem it, GifAnimation gif, int fps, string work)
    {
        string dir = Path.Combine(work, "gif_" + it.Id.ToString("N"));
        Directory.CreateDirectory(dir);
        var written = new Dictionary<int, string>();   // each picture of the GIF is written once, and copied for the frames that show it
        int frames = (int)Math.Ceiling(it.Length * fps) + 1;
        for (int k = 0; k < frames; k++)
        {
            int index = gif.IndexAt(k / (double)fps);
            string file = Path.Combine(dir, $"{k + 1:00000}.png");
            if (written.TryGetValue(index, out var first)) File.Copy(first, file, overwrite: true);
            else
            {
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(gif.Frames[index]));
                using (var fs = File.Create(file)) enc.Save(fs);
                written[index] = file;
            }
        }
        return Path.Combine(dir, "%05d.png");
    }

    /// <summary>
    /// Draws a text item as a tight transparent picture of its words (with room for the shadow), at the biggest size the item ever has.
    /// The export scales it down and puts it where each moment needs, so the words can move and grow smoothly.
    /// </summary>
    private void RenderTextLayer(TimelineItem it, int H, string path)
    {
        double k = H / Math.Max(1, Stage.Height);
        double biggest = ExportGraph.TextRenderSize(it.Props);
        var tb = new OutlinedText { Effect = new DropShadowEffect { BlurRadius = 6 * k, ShadowDepth = 1 * k, Opacity = 0.75 } };
        tb.Style(it.Props, H * biggest);
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        int pad = (int)Math.Ceiling(10 * k * Math.Max(1, biggest * 12));
        int W = Math.Max(2, (int)Math.Ceiling(tb.DesiredSize.Width) + 2 * pad), Hh = Math.Max(2, (int)Math.Ceiling(tb.DesiredSize.Height) + 2 * pad);
        W += W % 2; Hh += Hh % 2;
        var canvas = new Canvas { Width = W, Height = Hh, Background = Brushes.Transparent };
        Canvas.SetLeft(tb, (W - tb.DesiredSize.Width) / 2); Canvas.SetTop(tb, (Hh - tb.DesiredSize.Height) / 2);
        canvas.Children.Add(tb);
        canvas.Measure(new Size(W, Hh)); canvas.Arrange(new Rect(0, 0, W, Hh)); canvas.UpdateLayout();
        var bmp = new RenderTargetBitmap(W, Hh, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(canvas);
        var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path); enc.Save(fs);
    }
}

public sealed record ExportChoice(int Height, int Fps, int Cq, int Crf, string Path);

/// <summary>Asks how to export: size, frame rate, quality and where to put the file.</summary>
internal static class ExportDialog
{
    private static string Remembered => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered", "export-folder.txt");

    /// <summary>The folder the last export went to.</summary>
    public static string? LastFolder()
    {
        try { return File.Exists(Remembered) ? File.ReadAllText(Remembered).Trim() : null; }
        catch (IOException) { return null; }
    }

    private static void Remember(string folder)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(Remembered)!); File.WriteAllText(Remembered, folder); }
        catch (IOException) { }
    }

    public static ExportChoice? Ask(Window? owner, double aspect, string suggestedName, string suggestedFolder)
    {
        var win = new Window { Title = "Export", Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        var stack = new StackPanel { Margin = new Thickness(22) };
        TextBlock Label(string t) => new() { Text = t, Margin = new Thickness(0, 12, 0, 4), Opacity = 0.75 };
        ComboBox Combo(params string[] items) { var c = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Height = 34 }; foreach (var i in items) c.Items.Add(i); c.SelectedIndex = 0; return c; }

        stack.Children.Add(new TextBlock { Text = "Export as a video", FontSize = 18, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(Label("Size"));
        var size = Combo("1080p (full HD)", "720p", "480p");
        stack.Children.Add(size);
        stack.Children.Add(Label("Frame rate"));
        var fps = Combo("60 per second (smooth)", "30 per second");
        stack.Children.Add(fps);
        stack.Children.Add(Label("Quality"));
        var quality = Combo("High (bigger file)", "Balanced", "Small file");
        stack.Children.Add(quality);
        stack.Children.Add(Label("Name"));
        var name = new TextBox { Text = suggestedName, Height = 34, VerticalContentAlignment = VerticalAlignment.Center };
        stack.Children.Add(name);
        stack.Children.Add(Label("Folder"));
        var folder = new TextBox { Text = suggestedFolder, Height = 34, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var browse = new Button { Content = "Choose…", Height = 34 };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Save the video in", InitialDirectory = Directory.Exists(folder.Text) ? folder.Text : null };
            if (dlg.ShowDialog() == true) folder.Text = dlg.FolderName;
        };
        var row = new DockPanel(); DockPanel.SetDock(browse, Dock.Right); row.Children.Add(browse); row.Children.Add(folder);
        stack.Children.Add(row);
        var where = new TextBlock { Margin = new Thickness(0, 8, 0, 0), Opacity = 0.6, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        string FullPath()
        {
            string n = string.Concat(name.Text.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim().TrimEnd('.');
            if (n.Length == 0) n = "My video";
            if (n.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
            return System.IO.Path.Combine(folder.Text.Trim(), n + ".mp4");
        }
        void Refresh() => where.Text = "Saves as  " + FullPath();
        name.TextChanged += (_, _) => Refresh(); folder.TextChanged += (_, _) => Refresh(); Refresh();
        stack.Children.Add(where);
        name.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };

        ExportChoice? result = null;
        var go = new Button { Content = "Export", Height = 36, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("PrimaryButton") };
        var cancel = new Button { Content = "Cancel", Height = 36, MinWidth = 84 };
        go.Click += (_, _) =>
        {
            string file = FullPath();
            if (!System.IO.Directory.Exists(folder.Text.Trim())) { MessageBox.Show(win, "Choose a folder that exists to save into.", "Export"); return; }
            if (System.IO.File.Exists(file) && MessageBox.Show(win, $"{System.IO.Path.GetFileName(file)} already exists. Replace it?", "Export", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            int[] heights = { 1080, 720, 480 }; int[] cq = { 20, 25, 31 }, crf = { 17, 21, 26 };
            result = new ExportChoice(heights[size.SelectedIndex], fps.SelectedIndex == 0 ? 60 : 30, cq[quality.SelectedIndex], crf[quality.SelectedIndex], file);
            Remember(folder.Text.Trim());
            win.DialogResult = true;
        };
        cancel.Click += (_, _) => win.DialogResult = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(go); buttons.Children.Add(cancel);
        stack.Children.Add(buttons);
        win.Content = stack;
        win.ShowDialog();
        return result;
    }
}

/// <summary>A small window with a progress bar and a Cancel button while an export runs.</summary>
internal sealed class ExportProgress : Window
{
    private readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 1 };
    private readonly TextBlock _text = new() { Margin = new Thickness(0, 10, 0, 0), Opacity = 0.75, Text = "Starting…" };
    public event Action? CancelRequested;
    private bool _finished;

    public ExportProgress(Window? owner)
    {
        Title = "Exporting"; Owner = owner; WindowStartupLocation = WindowStartupLocation.CenterOwner; Width = 420; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        var stack = new StackPanel { Margin = new Thickness(22) };
        stack.Children.Add(new TextBlock { Text = "Making your video…", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) });
        stack.Children.Add(_bar); stack.Children.Add(_text);
        var cancel = new Button { Content = "Cancel", Height = 34, MinWidth = 84, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        cancel.Click += (_, _) => { _text.Text = "Cancelling…"; CancelRequested?.Invoke(); };
        stack.Children.Add(cancel);
        Content = stack;
        Closing += (_, e) => { if (!_finished) { e.Cancel = true; _text.Text = "Cancelling…"; CancelRequested?.Invoke(); } };
    }

    public void Report(double fraction) { _bar.Value = fraction; _text.Text = $"{fraction * 100:0}%"; }

    public new void Close() { _finished = true; base.Close(); }
}
