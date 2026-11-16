using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Buffered.UI;

/// <summary>Interface zoom: Ctrl and the mouse wheel, Ctrl plus, minus and 0, or the choice in Settings. Everything in the window is scaled together.</summary>
public partial class MainWindow
{
    private static readonly int[] ZoomSteps = { 70, 80, 90, 100, 110, 125, 150, 175, 200, 250 };
    private sealed record ZoomChoice(int Value, string Name);
    private static readonly ZoomChoice[] ZoomChoices = ZoomSteps.Select(z => new ZoomChoice(z, z + "%")).ToArray();
    private readonly DispatcherTimer _zoomPillTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private bool _zoomWired;

    /// <summary>When the window (as laid out, so zoom counts) is too narrow, the status bar drops its least useful items one by one instead of overlapping them.</summary>
    private void StatusBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = StatusStrip.ActualWidth;
        MetersHost.Visibility = w >= 820 ? Visibility.Visible : Visibility.Collapsed;
        CpuText.Visibility = w >= 700 ? Visibility.Visible : Visibility.Collapsed;
        QualityText.Visibility = w >= 620 ? Visibility.Visible : Visibility.Collapsed;
        VramText.Visibility = w >= 540 ? Visibility.Visible : Visibility.Collapsed;
    }

    private int CurrentZoom => Math.Clamp(_settings.UiZoom <= 0 ? 100 : _settings.UiZoom, ZoomSteps[0], ZoomSteps[^1]);

    /// <summary>Applies the saved zoom (called while the window is built, before it is shown).</summary>
    private void StartZoom()
    {
        if (!_zoomWired)
        {
            _zoomWired = true;
            _zoomPillTimer.Tick += (_, _) => { _zoomPillTimer.Stop(); ZoomPill.IsHitTestVisible = false; ZoomPill.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220))); };
        }
        _baseMinWidth = MinWidth; _baseMinHeight = MinHeight;
        ApplyZoomSize(CurrentZoom);
    }

    private double _baseMinWidth, _baseMinHeight;

    /// <summary>Scales the contents, and lets the window be smaller when zoomed out.</summary>
    private void ApplyZoomSize(int percent)
    {
        double f = percent / 100.0;
        ZoomScale.ScaleX = ZoomScale.ScaleY = f;
        // Zooming in never resizes the window (like a browser, it just has less room); zooming out lets it be made smaller.
        MinWidth = _baseMinWidth * Math.Min(f, 1.0);
        MinHeight = _baseMinHeight * Math.Min(f, 1.0);
    }

    private void SelectZoomBox()
    {
        ZoomBox.ItemsSource = ZoomChoices;
        ZoomBox.DisplayMemberPath = nameof(ZoomChoice.Name);
        ZoomBox.SelectedItem = ZoomChoices.FirstOrDefault(c => c.Value == CurrentZoom);
    }

    private void SetZoom(int percent, bool showPill)
    {
        percent = Math.Clamp(percent, ZoomSteps[0], ZoomSteps[^1]);
        _settings.UiZoom = percent;
        ApplyZoomSize(percent);
        bool was = _loading; _loading = true;
        ZoomBox.SelectedItem = ZoomChoices.FirstOrDefault(c => c.Value == percent);
        _loading = was;
        try { _settings.Save(); } catch { }

        if (!showPill) return;
        ZoomText.Text = percent + "%";
        ZoomPill.IsHitTestVisible = true;
        ZoomPill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        _zoomPillTimer.Stop(); _zoomPillTimer.Start();
    }

    private int NextZoom(int direction)
    {
        int now = CurrentZoom;
        if (direction > 0) return ZoomSteps.FirstOrDefault(z => z > now, ZoomSteps[^1]);
        return ZoomSteps.LastOrDefault(z => z < now, ZoomSteps[0]);
    }

    private void ZoomBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ZoomBox.SelectedItem is not ZoomChoice c) return;
        SetZoom(c.Value, true);
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(100, true);

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Delta != 0)
        {
            SetZoom(NextZoom(e.Delta > 0 ? 1 : -1), true);
            e.Handled = true;
            return;
        }
        base.OnPreviewMouseWheel(e);
    }

    /// <summary>Ctrl plus, Ctrl minus and Ctrl 0. Returns true when the key was used.</summary>
    private bool ZoomKey(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || (Keyboard.Modifiers & ModifierKeys.Alt) != 0) return false;
        switch (e.Key)
        {
            case Key.OemPlus: case Key.Add: SetZoom(NextZoom(1), true); return true;
            case Key.OemMinus: case Key.Subtract: SetZoom(NextZoom(-1), true); return true;
            case Key.D0: case Key.NumPad0: SetZoom(100, true); return true;
        }
        return false;
    }
}
