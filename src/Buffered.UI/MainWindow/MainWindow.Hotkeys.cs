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

/// <summary>Hotkeys: capturing a key press, the optional Pause and Stop keys, and the extra clip hotkeys.</summary>
public partial class MainWindow
{
    private Button? _capturing;

    private static object KeyOf(Button b) => b.Tag;   // "save", "pause" or "stop", or the ClipKey an extra row edits

    private string GetKey(object which) => which switch { ClipKey ck => ck.Key, "pause" => _settings.PauseHotkey, "stop" => _settings.StopHotkey, _ => _settings.Hotkey };

    private void SetKey(object which, string value)
    {
        if (which is ClipKey ck) ck.Key = value;
        else if (which as string == "pause") _settings.PauseHotkey = value;
        else if (which as string == "stop") _settings.StopHotkey = value;
        else _settings.Hotkey = value;
    }

    // ---- extra clip hotkeys -------------------------------------------------

    private void RebuildClipKeys()
    {
        ClipKeysPanel.Children.Clear();
        foreach (var ck in _settings.ClipKeys) ClipKeysPanel.Children.Add(BuildClipKeyRow(ck));
    }

    private UIElement BuildClipKeyRow(ClipKey ck)
    {
        bool minutes = ck.Seconds >= 60 && ck.Seconds % 60 == 0;
        var number = new TextBox { Width = 70, Height = 34, Text = (minutes ? ck.Seconds / 60 : ck.Seconds).ToString(), HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var unit = new ComboBox { Width = 110, Height = 34, Margin = new Thickness(0, 0, 8, 0) };
        unit.Items.Add("seconds"); unit.Items.Add("minutes");
        unit.SelectedIndex = minutes ? 1 : 0;
        var key = new Button { Tag = ck, Width = 210, Height = 34, HorizontalContentAlignment = HorizontalAlignment.Left, Content = ck.Key.Length > 0 ? ck.Key : "None" };
        key.Click += HotkeyButton_Click; key.PreviewKeyDown += HotkeyButton_PreviewKeyDown; key.LostKeyboardFocus += HotkeyButton_LostFocus;
        var remove = new Button { Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Remove this hotkey", Content = new TextBlock { FontFamily = (FontFamily)FindResource("IconFont"), Text = "\uE711", FontSize = 12 } };
        System.Windows.Automation.AutomationProperties.SetName(remove, "Remove this hotkey");
        System.Windows.Automation.AutomationProperties.SetName(number, "Seconds or minutes to save");
        System.Windows.Automation.AutomationProperties.SetName(unit, "Unit");
        var note = new TextBlock { Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };

        void Update()
        {
            bool ok = int.TryParse(number.Text.Trim(), out int v) && v > 0 && v <= 100000;
            number.BorderBrush = ok ? (Brush)FindResource("ControlStrokeColorDefaultBrush") : Brushes.IndianRed;
            if (!ok) { note.Text = "Enter a number above 0."; note.Visibility = Visibility.Visible; return; }
            ck.Seconds = unit.SelectedIndex == 1 ? v * 60 : v;
            note.Text = ck.Seconds > _settings.BufferSeconds ? "Longer than the buffer: saves all of it." : "";
            note.Visibility = note.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Setting_Changed(number, new RoutedEventArgs());
        }
        number.TextChanged += (_, _) => { if (!_loading) Update(); };
        unit.SelectionChanged += (_, _) => { if (!_loading) Update(); };
        remove.Click += (_, _) => { _settings.ClipKeys.Remove(ck); RebuildClipKeys(); Setting_Changed(remove, new RoutedEventArgs()); };

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = "Save the last", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        line.Children.Add(number); line.Children.Add(unit); line.Children.Add(key); line.Children.Add(remove);
        if (ck.Seconds > _settings.BufferSeconds) { note.Text = "Longer than the buffer: saves all of it."; note.Visibility = Visibility.Visible; }
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(line); row.Children.Add(note);
        return row;
    }

    private void AddClipKey_Click(object sender, RoutedEventArgs e)
    {
        _settings.ClipKeys.Add(new ClipKey { Seconds = 30 });
        RebuildClipKeys();
        Setting_Changed(sender, e);
    }

    /// <summary>Shows each optional key (or "None") and only offers the clear button when one is set.</summary>
    private void ShowOptionalKeys()
    {
        PauseHotkeyButton.Content = _settings.PauseHotkey is { Length: > 0 } p ? p : "None";
        StopHotkeyButton.Content = _settings.StopHotkey is { Length: > 0 } t ? t : "None";
        PauseHotkeyClear.Visibility = _settings.PauseHotkey.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StopHotkeyClear.Visibility = _settings.StopHotkey.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        var b = (Button)sender;
        if (_capturing is not null && _capturing != b) EndCapture();
        _capturingKey = true;
        _capturing = b;
        b.Content = "Press a key…";
        b.Focus();
    }

    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingKey || _capturing is null) return;
        e.Handled = true;
        var b = _capturing;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { EndCapture(); return; }
        // Ctrl, Alt, Shift and the Windows key alone are not a hotkey: wait for the key they go with.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None) return;

        string? name = key switch
        {
            >= Key.F1 and <= Key.F24 => key.ToString(),
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
            _ => null,
        };
        if (name is null) { b.Content = "Use F1–F24, a letter, or a digit"; return; }
        var mods = Keyboard.Modifiers;
        name = (mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (mods.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + name;

        object which = KeyOf(b);
        SetKey(which, name);
        if (which as string == "save") HotkeyChip.Text = name;
        EndCapture();
        Setting_Changed(b, e);
    }

    private void HotkeyButton_LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (_capturingKey && sender == _capturing) EndCapture(); }

    private void EndCapture()
    {
        _capturingKey = false;
        if (_capturing is { } b) { object which = KeyOf(b); b.Content = GetKey(which) is { Length: > 0 } k ? k : "None"; }
        _capturing = null;
        ShowOptionalKeys();
    }

    private void HotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        var b = (Button)sender;
        SetKey(KeyOf(b), "");
        ShowOptionalKeys();
        Setting_Changed(b, e);
    }
}
