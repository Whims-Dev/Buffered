using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Buffered.UI;

/// <summary>Keyframes in the editor: the diamond beside each animatable slider, and setting values on animated properties.</summary>
public partial class AdvancedEditor
{
    private KeyProp? KeyPropOf(object? sender) =>
        sender == OpacitySlider ? KeyProp.Opacity : sender == VolumeSlider ? KeyProp.Volume : sender == ScaleSlider ? KeyProp.Scale :
        sender == PosXSlider ? KeyProp.PosX : sender == PosYSlider ? KeyProp.PosY : null;

    private static KeyProp? FromTag(object? sender) => (sender as FrameworkElement)?.Tag is string t && Enum.TryParse<KeyProp>(t, out var p) ? p : null;

    private static readonly Dictionary<KeyProp, string> Names = new()
    {
        [KeyProp.Opacity] = "opacity", [KeyProp.Volume] = "volume", [KeyProp.Scale] = "scale", [KeyProp.PosX] = "position across", [KeyProp.PosY] = "position down",
    };

    /// <summary>Sets a property of an item: as a keyframe at the playhead if the property is animated, otherwise as its plain value.</summary>
    private Project WithValue(Project p, Guid id, KeyProp prop, double value)
    {
        if (p.Find(id) is not { } it) return p;
        return it.Props.HasKeys(prop) ? p.SetKey(id, prop, _t, value) : p.SetProps(id, o => prop switch
        {
            KeyProp.Opacity => o with { Opacity = value }, KeyProp.Volume => o with { Volume = value }, KeyProp.Scale => o with { Scale = value },
            KeyProp.PosX => o with { PosX = value }, _ => o with { PosY = value },
        });
    }

    /// <summary>A filled diamond when there is a keyframe at the playhead, an outline one when the property is animated elsewhere, a dim outline when it is not animated.</summary>
    private void UpdateKeyButtons(TimelineItem it, double at)
    {
        foreach (var (button, prop) in new[] { (KeyOpacity, KeyProp.Opacity), (KeyVolume, KeyProp.Volume), (KeyScale, KeyProp.Scale), (KeyPosX, KeyProp.PosX), (KeyPosY, KeyProp.PosY) })
        {
            bool animated = it.Props.HasKeys(prop), here = animated && it.Props.FramesOf(prop).Any(f => Math.Abs(f.Time - at) < 0.03);
            button.Content = here ? "\u25C6" : "\u25C7";
            button.Foreground = animated ? new SolidColorBrush(Color.FromRgb(0xE6, 0xD3, 0x70)) : (Brush)FindResource("TextFillColorSecondaryBrush");
        }
    }

    private void Key_Click(object sender, RoutedEventArgs e)
    {
        if (FromTag(sender) is not { } prop || _selected is not { } id || _h.Current.Find(id) is not { } it) return;
        double at = _t - it.Start;
        if (at < -0.001 || at > it.Length + 0.001) { StatusText.Text = "Move the playhead onto the item to set a keyframe"; return; }
        var p = _h.Current;
        bool here = it.Props.FramesOf(prop).Any(f => Math.Abs(f.Time - at) < 0.03);
        foreach (var target in Tl.Selection.DefaultIfEmpty(id))
        {
            if (p.Find(target) is not { } t) continue;
            double tat = _t - t.Start;
            if (tat < -0.001 || tat > t.Length + 0.001) continue;
            p = here ? p.RemoveKey(target, prop, _t) : p.SetKey(target, prop, _t, t.Props.ValueAt(prop, tat));   // a new keyframe starts with the value it already has there
        }
        _h.Apply(p);
        AfterEdit();
    }

    private void Key_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (FromTag(sender) is not { } prop || _selected is not { } id || !(_h.Current.Find(id)?.Props.HasKeys(prop) ?? false)) return;
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = $"Remove the {Names[prop]} animation" };
        remove.Click += (_, _) =>
        {
            var p = _h.Current;
            foreach (var target in Tl.Selection.DefaultIfEmpty(id)) p = p.ClearKeys(target, prop);
            _h.Apply(p); AfterEdit();
        };
        menu.Items.Add(remove);
        menu.IsOpen = true;
        e.Handled = true;
    }
}
