using System.Windows.Media;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace Buffered.UI;

/// <summary>Trimming and saving.</summary>
public partial class ClipPlayer
{
    // ---- trimming ----------------------------------------------------------

    private bool HasTrim => _duration > 0 && (Bar.Start > 0.05 || Bar.End < _duration - 0.05);
    /// <summary>Something would change if the clip were saved: a trim, a volume, or both.</summary>
    private bool HasChanges => HasTrim || AudioChanged;

    private void UpdateRange()
    {
        if (_duration <= 0)
        {
            StartBox.Text = EndBox.Text = "–:––"; KeepText.Text = ""; KeepBig.Text = "–:––"; KeepOf.Text = "loading…"; KeepNote.Visibility = Visibility.Collapsed; SetButtons(); return;
        }
        if (!StartBox.IsKeyboardFocused) StartBox.Text = Fmt(Bar.Start);
        if (!EndBox.IsKeyboardFocused) EndBox.Text = Fmt(Bar.End);

        KeepBig.Text = Fmt(Bar.End - Bar.Start);
        KeepOf.Text = $"kept of {Fmt(_duration)}";
        TrimState.Text = HasChanges ? "Edited" : "Whole clip";
        KeepCol0.Width = new GridLength(Math.Max(0, Bar.Start), GridUnitType.Star);
        KeepCol1.Width = new GridLength(Math.Max(0.01, Bar.End - Bar.Start), GridUnitType.Star);
        KeepCol2.Width = new GridLength(Math.Max(0, _duration - Bar.End), GridUnitType.Star);

        KeepText.Inlines.Clear();
        if (AudioChanged)
        {
            var parts = new List<string>();
            for (int i = 0; i < _tracks.Count; i++)
            {
                if (IsChanged(_sliders[i].Value)) parts.Add($"{_tracks[i].Name} {Vol.Text(_sliders[i].Value)}");
                if (TrackInMix[i] != _tracks[i].InMix) parts.Add($"{_tracks[i].Name} {(TrackInMix[i] ? "unmuted" : "muted")}");
            }
            KeepText.Inlines.Add(string.Join(", ", parts));
        }
        if (!HasChanges) KeepText.Inlines.Add("Drag the handles on the timeline, or type a start and end, to choose the part to keep.");
        KeepNote.Visibility = KeepText.Inlines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SetButtons();
    }

    private void SetButtons()
    {
        bool ok = HasChanges && !_busy;
        SaveCopyButton.IsEnabled = ok;
        SaveContent.Opacity = ok ? 1 : 0.45; // custom button content doesn't dim by itself
        ReplaceButton.IsEnabled = ok;
        ResetButton.IsEnabled = HasChanges && !_busy;
    }

    private void SetStart_Click(object sender, RoutedEventArgs e)
    {
        if (_duration <= 0) return;
        Bar.Start = Math.Clamp(Video.Position.TotalSeconds, 0, Bar.End - 0.1); UpdateRange();
    }

    private void SetEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_duration <= 0) return;
        Bar.End = Math.Clamp(Video.Position.TotalSeconds, Bar.Start + 0.1, _duration); UpdateRange();
    }

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitTime((TextBox)sender); Focus(); e.Handled = true; }
        else if (e.Key == Key.Escape) { UpdateRange(); Focus(); e.Handled = true; }
    }

    private void TimeBox_LostFocus(object sender, RoutedEventArgs e) => CommitTime((TextBox)sender);

    /// <summary>Applies a typed time ("1:23.4", "83.4") to the start or end handle, keeping the range valid.</summary>
    private void CommitTime(TextBox box)
    {
        if (_duration > 0 && TryParseTime(box.Text, out double t))
        {
            if (box == StartBox) { Bar.Start = Math.Clamp(t, 0, Bar.End - 0.1); SeekTo(Bar.Start); }
            else { Bar.End = Math.Clamp(t, Bar.Start + 0.1, _duration); SeekTo(Math.Max(0, Bar.End - 0.05)); }
        }
        UpdateRange();
        if (_duration > 0) box.Text = box == StartBox ? Fmt(Bar.Start) : Fmt(Bar.End);
    }

    private static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is 0 or > 3 || text.Trim().Length == 0) return false;
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }

    private void Method_Checked(object sender, RoutedEventArgs e) => UpdateMethodHint();

    private void UpdateMethodHint()
    {
        if (MethodHint is null) return;
        MethodHint.Text = FastRadio.IsChecked == true
            ? "Copies the video as it is, so it's instant and lossless, but the start can be up to 2 seconds early."
            : "Re-encodes the video on your GPU so the cut is on the exact frame. Audio is copied untouched.";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        Bar.Start = 0; Bar.End = _duration;
        if (AudioChanged)
        {
            ResetAudio();
            UpdateAudioText();
            _audioTimer.Stop(); _audioTimer.Start(); // back to the original sound
        }
        UpdateRange();
    }

    private void SetBusy(bool busy, string message = "")
    {
        _busy = busy;
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = message;
        BusyText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (SaveCopyButton is not null) SetButtons();
    }

    private string NewCopyPath()
    {
        // Same naming scheme as recorded clips, dated at the moment the trimmed clip starts, so it sorts and parses normally.
        var dir = Path.GetDirectoryName(_clip!.Path)!;
        var when = _clip.When.AddSeconds(Math.Max(1, Math.Round(Bar.Start)));
        while (true)
        {
            var p = Path.Combine(dir, $"{ClipNames.Sanitize(_clip.Label)}_{when:yyyy-MM-dd_HH-mm-ss}.mp4");
            if (!File.Exists(p)) return p;
            when = when.AddSeconds(1);
        }
    }

    private async void SaveCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null || !HasChanges) return;
        Video.Pause(); SetPlaying(false);
        string output = NewCopyPath();
        SetBusy(true, AudioChanged ? "Saving…" : "Trimming…");
        // Only the sound changed: copy the video instead of re-encoding it.
        bool fast = FastRadio.IsChecked == true || !HasTrim;
        var (ok, error) = await Trimmer.RunAsync(_clip.Path, Bar.Start, Bar.End, fast, output, _stems, CurrentEdit, _stemsOffset);
        SetBusy(false);
        if (ok)
            Dialogs?.Toast("Saved " + Path.GetFileName(output), (HasTrim ? "Trimmed copy" : "Copy with new volumes") + " · the original is untouched", "Show in folder",
                () => Process.Start("explorer.exe", $"/select,\"{output}\""));
        else
        {
            TryDelete(output);
            if (Dialogs is not null) await Dialogs.MessageAsync("Couldn't trim this clip", error);
        }
    }

    private async void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null || !HasChanges) return;
        if (Dialogs is not null)
        {
            bool yes = await Dialogs.ConfirmAsync("Replace the original with the edited clip?",
                "The original goes to the Recycle Bin, so you can get it back from there.", "Replace original");
            if (!yes) return;
        }

        Video.Pause(); SetPlaying(false);
        string original = _clip.Path;
        string temp = Path.Combine(Path.GetDirectoryName(original)!, "." + Path.GetFileNameWithoutExtension(original) + ".trim.mp4");
        double start = Bar.Start, end = Bar.End;
        var edit = CurrentEdit;
        string? stems = _stems;
        bool fast = FastRadio.IsChecked == true || !HasTrim;

        SetBusy(true, AudioChanged ? "Saving…" : "Trimming…");
        var (ok, error) = await Trimmer.RunAsync(original, start, end, fast, temp, stems, edit, _stemsOffset);
        if (!ok)
        {
            SetBusy(false);
            TryDelete(temp);
            if (Dialogs is not null) await Dialogs.MessageAsync("Couldn't trim this clip", error);
            return;
        }

        // The player has the file open; let go of it before it is moved.
        Release();
        try
        {
            FileSystem.DeleteFile(original, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            File.Move(temp, original);

            // The companion file follows the clip: the old one goes, the new one takes its place.
            string originalStems = ClipNames.StemsPath(original), tempStems = ClipNames.StemsPath(temp);
            TryDelete(originalStems);
            if (File.Exists(tempStems))
            {
                File.Move(tempStems, originalStems);
                try { File.SetAttributes(originalStems, FileAttributes.Hidden); } catch { }
            }
        }
        catch (Exception ex)
        {
            SetBusy(false);
            Open(_clip);
            if (Dialogs is not null) await Dialogs.MessageAsync("Couldn't replace the original", ex.Message);
            return;
        }

        SetBusy(false);
        FileReplaced?.Invoke(original);
        Open(new ClipItem(original));
        Dialogs?.Toast("Original replaced", "The original is in the Recycle Bin");
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private static string Fmt(double s)
    {
        if (s < 0) s = 0;
        var t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}" : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }
}
