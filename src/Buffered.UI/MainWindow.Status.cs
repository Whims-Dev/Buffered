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

/// <summary>The recorder status bar and its Pause, Stop and Save buttons.</summary>
public partial class MainWindow
{
    // ---- save / status ------------------------------------------------------

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        string old = SaveLabel.Text;
        SaveLabel.Text = "Saving…";
        var path = await Ipc.SaveClipAsync();
        SaveLabel.Text = old;
        SaveButton.IsEnabled = true;
        if (path is null)
        {
            Dialogs.Toast("Couldn't save a clip", _isStopped ? "Recording is stopped." : _isPaused ? "Recording is paused." : "Is the recorder running?", error: true);
            return;
        }
        LoadClips();
        Dialogs.Toast("Clip saved", Path.GetFileName(path), "Show in folder", () => Process.Start("explorer.exe", $"/select,\"{path}\""));
    }

    private async Task PollStatusAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var s = await Ipc.GetStatusAsync();
            _recSource = s?.Source ?? "";
            if (s is null && DateTime.UtcNow < _restartUntil)
            {
                // The recorder restarts itself after a pause or settings change; it is back in about a second.
                SetOffline(_pendingText, false);
            }
            else if (s is null)
            {
                SetOffline("Recorder isn't running", true);
            }
            else if (s.Stopped)
            {
                SetOffline("Stopped", false);
                StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
                ShowButtons(paused: false, stopped: true);
                VramText.Text = "VRAM 0 MB";
                ShowMemory(s);
            }
            else if (!s.Recording)
            {
                SetOffline(s.Starting ? "Starting… waiting for Windows to be ready" + (s.Error is { Length: > 0 } why ? $" (last try: {(why.Length > 90 ? why[..90] + "…" : why)})" : "") : s.Error is { } err ? $"Couldn't start: {err}" : "Recording is off", false);
            }
            else
            {
                StatusDot.Fill = new SolidColorBrush(s.Paused ? Color.FromRgb(0xE0, 0xA0, 0x30) : Color.FromRgb(0x2E, 0x9E, 0x5B));
                StatusText.Text = s.Paused ? "Paused" : "Recording";
                StartRecorderButton.Visibility = Visibility.Collapsed;
                ShowButtons(s.Paused, stopped: false);
                BufferText.Text = $"Buffer {FormatClock(s.BufferSeconds > 0 ? Math.Min(s.BufferedSeconds, s.BufferSeconds) : s.BufferedSeconds)} / {FormatClock(s.BufferSeconds)}";   // the buffer trims at a keyframe, so it can hold a moment more than its length: show it as full
                BufferBar.Visibility = Visibility.Visible;
                BufferBar.Value = s.BufferSeconds > 0 ? Math.Min(100, s.BufferedSeconds / s.BufferSeconds * 100) : 0;
                VramText.Text = s.VramMb >= 0 ? $"VRAM {s.VramMb} MB" : "";
                ShowMemory(s);
                CpuText.Text = $"CPU {s.CpuPercent:F0}%";
                QualityText.Text = (s.Source is { Length: > 0 } src && src != "Monitor" ? $"{src} · " : "") + $"{s.Height}p · {s.Fps} fps";
                AudioMeters.Visibility = string.IsNullOrEmpty(s.Audio) ? Visibility.Collapsed : Visibility.Visible;
                StatusStrip.ToolTip = string.IsNullOrEmpty(s.Audio) ? "No audio is being recorded" : $"Audio: {s.Audio}";
            }
        }
        finally { _polling = false; }
    }

    private void ShowButtons(bool paused, bool stopped)
    {
        _isPaused = paused;
        _isStopped = stopped;
        // Pause (keeps the buffer) only while recording is on; Stop turns into Start once stopped.
        PauseButton.Visibility = stopped ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Text = paused ? "" : ""; // play / pause
        PauseLabel.Text = paused ? "Resume" : "Pause";
        PauseButton.ToolTip = paused ? "Resume recording" : "Pause recording (keeps the buffer)";
        StopButton.Visibility = Visibility.Visible;
        StopGlyph.Text = stopped ? "" : ""; // play / stop
        StopLabel.Text = stopped ? "Start" : "Stop";
        StopButton.ToolTip = stopped ? "Start recording" : "Stop recording (empties the buffer)";
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        PauseButton.IsEnabled = false;
        if (_isPaused) await Ipc.ResumeAsync(); else await Ipc.PauseAsync();
        await PollStatusAsync();
        PauseButton.IsEnabled = true;
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        _pendingText = "Restarting…";
        _restartUntil = DateTime.UtcNow.AddSeconds(6);
        StatusText.Text = "Restarting…";
        if (_isStopped) await Ipc.StartAsync(); else await Ipc.StopAsync();
        StopButton.IsEnabled = true;
    }

    /// <summary>The recorder's own memory (private bytes), read from its process.</summary>
    private static long RecorderRamMb()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("BufferedRecorder"))
                using (p) return p.PrivateMemorySize64 / 1048576;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return 0;
    }

    private long _scrubDiskMb; private DateTime _scrubDiskAt = DateTime.MinValue;

    /// <summary>The memory the recorder uses, in the bar, and the whole picture (this window, the replay buffer, the disk used by smooth-scrubbing copies) when the mouse rests on it.</summary>
    private void ShowMemory(RecorderStatus s)
    {
        long recorderMb = RecorderRamMb();
        RamText.Text = recorderMb > 0 ? $"RAM {recorderMb} MB" : "";
        if ((DateTime.UtcNow - _scrubDiskAt).TotalSeconds > 30)
        {
            _scrubDiskAt = DateTime.UtcNow;
            _ = Task.Run(() => ScrubCache.DiskMb()).ContinueWith(t => _scrubDiskMb = t.Result);
        }
        long windowMb = Process.GetCurrentProcess().PrivateMemorySize64 / 1048576;
        string nl = Environment.NewLine;
        RamText.ToolTip = $"The recorder's memory: {recorderMb} MB, of which about {s.BufferedMb:F0} MB is the replay buffer." + nl + $"This window: {windowMb} MB.";
        VramText.ToolTip = "Video memory the recorder uses. Kept small on purpose.";
        CpuText.ToolTip = "How much of the processor the recorder uses.";
        QualityText.ToolTip = "What is being recorded." + nl + $"Smooth-scrubbing copies on disk: {_scrubDiskMb} MB.";
    }

    private void SetOffline(string text, bool canStart)
    {
        StatusDot.Fill = Brushes.Gray;
        StatusText.Text = text;
        PauseButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
        StartRecorderButton.Visibility = canStart ? Visibility.Visible : Visibility.Collapsed;
        BufferText.Text = ""; BufferBar.Visibility = Visibility.Collapsed; VramText.Text = ""; RamText.Text = ""; CpuText.Text = ""; QualityText.Text = "";
        AudioMeters.Visibility = Visibility.Collapsed;
    }

    /// <summary>Maps a 0 to 1 peak to a meter on a decibel scale (silence at -60 dB), so quiet sound still shows.</summary>
    private static double ToMeter(double peak) => peak <= 0.001 ? 0 : Math.Clamp((20 * Math.Log10(peak) + 60) / 60 * 100, 0, 100);

    private void StartRecorderButton_Click(object sender, RoutedEventArgs e)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "BufferedRecorder.exe");
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }
}
