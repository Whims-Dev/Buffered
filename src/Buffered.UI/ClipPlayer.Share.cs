using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Buffered.UI;

/// <summary>"Share": makes a smaller copy of the clip (the part that is selected, with the audio as it is now) that fits an upload limit.</summary>
public partial class ClipPlayer
{
    private static readonly (int Mb, string Label, string Note)[] ShareSizes =
    {
        (10, "Under 10 MB", "Discord, free accounts"),
        (25, "Under 25 MB", "Email, most chat apps"),
        (50, "Under 50 MB", "Discord Nitro Basic"),
    };

    private int _shareMb = 10;
    private string? _shareOut;
    private bool _shareBusy;
    private readonly List<ToggleButton> _shareButtons = new();

    /// <summary>Moves the share window out to the main window's top layer, so it dims everything (the side bar too), like the other dialogs.</summary>
    public void MoveShareTo(Grid host, UIElement above)
    {
        if (ShareScrim.Parent is Panel old) old.Children.Remove(ShareScrim);
        Grid.SetRowSpan(ShareScrim, 2); Grid.SetColumnSpan(ShareScrim, 2);
        host.Children.Insert(Math.Max(0, host.Children.IndexOf(above)), ShareScrim);
    }

    // Shared from the clip list instead of the editor: the whole clip, the sound as saved.
    private bool _shareFromList;
    private double _shareListLength;
    private double ShareStart => _shareFromList ? 0 : Bar.Start;
    private double ShareEnd => _shareFromList ? _shareListLength : Bar.End;
    private bool ShareHasTrim => !_shareFromList && HasTrim;
    private bool ShareAudioEdited => !_shareFromList && AudioChanged;

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null || _duration <= 0) return;
        _shareFromList = false;
        ShowShare();
    }

    /// <summary>Opens the share window for a clip picked in the list, without opening the editor.</summary>
    public async Task ShareClipAsync(ClipItem clip)
    {
        var length = await Media.GetDurationSecondsAsync(clip.Path);
        if (length is not > 0) return;
        _clip = clip;
        _shareFromList = true; _shareListLength = length.Value;
        ShowShare();
    }

    private void ShowShare()
    {
        if (_shareButtons.Count == 0) BuildShareOptions();
        ResetShare();
        ShareScrim.Visibility = Visibility.Visible;
        ShareGo.Focus();
    }

    private void BuildShareOptions()
    {
        foreach (var (mb, label, note) in ShareSizes)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var n = new TextBlock { Text = note, FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
            Grid.SetColumn(n, 1); grid.Children.Add(n);
            var b = new ToggleButton
            {
                Content = grid, Tag = mb, Height = 44, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(16, 0, 16, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            System.Windows.Automation.AutomationProperties.SetName(b, label);
            b.Click += (_, _) => { if (_shareBusy) { b.IsChecked = (int)b.Tag == _shareMb; return; } _shareMb = mb; ResetShare(); };
            _shareButtons.Add(b);
            ShareOptions.Children.Add(b);
        }
    }

    /// <summary>Back to "ready": the options, the estimate, and a button that starts the copy.</summary>
    private void ResetShare()
    {
        _shareOut = null;
        foreach (var b in _shareButtons) b.IsChecked = (int)b.Tag == _shareMb;
        double secs = Math.Max(0.5, ShareEnd - ShareStart);
        var plan = ShareExport.PlanFor(_shareMb, secs);
        long original = 0; try { original = new FileInfo(_clip!.Path).Length; } catch { }
        ShareEstimate.Text = $"A {secs:0} second clip at about {plan.Height}p, {plan.Fps} fps. The original is {original / 1048576.0:0} MB."
            + (ShareHasTrim ? " Only the part you selected is included." : "");
        ShareBar.Visibility = Visibility.Collapsed;
        ShareResult.Visibility = Visibility.Collapsed;
        ShareGo.Content = "Make smaller copy";
        ShareClose.IsEnabled = true;
    }

    private async void ShareGo_Click(object sender, RoutedEventArgs e)
    {
        if (_shareBusy || _clip is null) return;
        if (_shareOut is not null)
        {
            // The copy is ready: put the file on the clipboard, so it can be pasted straight into a chat.
            try
            {
                var list = new System.Collections.Specialized.StringCollection { _shareOut };
                Clipboard.SetFileDropList(list);
                ShareResult.Text = "File copied. Paste it into your chat with Ctrl+V.";
            }
            catch (Exception ex) { ShareResult.Text = "Couldn't copy the file: " + ex.Message; }
            ShareResult.Visibility = Visibility.Visible;
            return;
        }

        _shareBusy = true;
        ShareGo.Content = "Compressing…";
        ShareClose.IsEnabled = false;
        ShareBar.Value = 0; ShareBar.Visibility = Visibility.Visible;
        ShareResult.Visibility = Visibility.Collapsed;

        string input = _clip.Path, clipPath = _clip.Path;
        string? preview = null;
        try
        {
            // Volumes and muted tracks you changed in the editor are part of the copy.
            if (ShareAudioEdited && _stems is not null)
            {
                preview = await Media.MakeBalancedPreviewAsync(_clip.Path, _stems, TrackDbs, TrackInMix);
                if (preview is not null) input = preview;
            }
            var progress = new Progress<double>(v => ShareBar.Value = v);
            string shownName = _clip.Title;
            var (ok, path, bytes, error) = await ShareExport.RunAsync(input, ShareStart, ShareEnd, clipPath, shownName, _shareMb, progress);
            ShareBar.Visibility = Visibility.Collapsed;
            if (ok)
            {
                _shareOut = path;
                // In the library the copy shows up as "Original Clip Name (10 MB)", not under its date-and-game file name.
                string fileTitle = Path.GetFileNameWithoutExtension(path);
                ClipLibrary.Update(path, m => m.Name = fileTitle);
                ShareResult.Text = $"Saved a smaller copy: {bytes / 1048576.0:0.0} MB, next to the original.";
                ShareGo.Content = "Copy file";
            }
            else
            {
                ShareResult.Text = "Couldn't make the copy. " + error;
                ShareGo.Content = "Try again";
            }
            ShareResult.Visibility = Visibility.Visible;
        }
        finally
        {
            if (preview is not null) { try { File.Delete(preview); } catch { } }
            _shareBusy = false;
            ShareClose.IsEnabled = true;
        }
    }

    private void ShareClose_Click(object sender, RoutedEventArgs e) { if (!_shareBusy) ShareScrim.Visibility = Visibility.Collapsed; }

    private void Share_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_shareBusy) { ShareScrim.Visibility = Visibility.Collapsed; e.Handled = true; }
    }
}
