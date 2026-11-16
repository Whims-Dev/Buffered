using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Buffered.UI;

/// <summary>Lets a slider be dragged from anywhere on its bar, not only by its knob: a click jumps there and dragging carries on from it.</summary>
internal static class SliderDrag
{
    public static void Attach(Slider slider)
    {
        slider.IsMoveToPointEnabled = false;   // WPF's own version handles the press first, and then never drags
        slider.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsThumb(d)) return;   // the knob already drags itself
            slider.CaptureMouse();
            Set(slider, e);
            e.Handled = true;
        };
        slider.PreviewMouseMove += (_, e) => { if (slider.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed) Set(slider, e); };
        slider.PreviewMouseLeftButtonUp += (_, _) => { if (slider.IsMouseCaptured) slider.ReleaseMouseCapture(); };
    }

    private static bool IsThumb(DependencyObject d)
    {
        for (var c = d; c is not null && c is not Slider; c = System.Windows.Media.VisualTreeHelper.GetParent(c))
            if (c is System.Windows.Controls.Primitives.Thumb) return true;
        return false;
    }

    private static void Set(Slider s, MouseEventArgs e)
    {
        double w = s.ActualWidth; if (w <= 0) return;
        double fraction = Math.Clamp((e.GetPosition(s).X - 8) / Math.Max(1, w - 16), 0, 1);   // the knob is about 16 px wide
        double v = s.Minimum + fraction * (s.Maximum - s.Minimum);
        if (s.IsSnapToTickEnabled && s.TickFrequency > 0) v = s.Minimum + Math.Round((v - s.Minimum) / s.TickFrequency) * s.TickFrequency;
        s.Value = Math.Clamp(v, s.Minimum, s.Maximum);
    }
}
