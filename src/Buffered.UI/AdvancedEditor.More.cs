using System.Windows;
using System.Windows.Controls;

namespace Buffered.UI;

/// <summary>The shape of the video, snapping, and copying, pasting and duplicating items.</summary>
public partial class AdvancedEditor
{
    private static readonly (string Label, double Aspect)[] Shapes =
    {
        ("Auto", 0), ("16:9 wide", 16.0 / 9), ("9:16 tall (phone)", 9.0 / 16), ("1:1 square", 1), ("4:5 portrait", 4.0 / 5), ("4:3", 4.0 / 3), ("21:9 cinema", 21.0 / 9),
    };
    private bool _aspectBoxBusy;

    private void InitMore()
    {
        _aspectBoxBusy = true;
        foreach (var s in Shapes) AspectBox.Items.Add(s.Label);
        AspectBox.SelectedIndex = 0;
        _aspectBoxBusy = false;
        StatusText.Text = Hint;
    }

    /// <summary>Shows which shape the video has (after an edit was opened or the first clip decided it).</summary>
    private void ShowAspect()
    {
        _aspectBoxBusy = true;
        int i = Array.FindIndex(Shapes, s => s.Aspect > 0 && Math.Abs(s.Aspect - _aspect) < 0.01);
        // "Auto" says what the shape is at the moment, for example "Auto (16:9)"
        string ratio = i > 0 ? Shapes[i].Label.Split(' ')[0] : $"{_aspect:0.##}:1";
        AspectBox.Items[0] = $"Auto ({ratio})";
        AspectBox.SelectedIndex = _aspectSet && i > 0 ? i : 0;
        _aspectBoxBusy = false;
    }

    private void Aspect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_aspectBoxBusy || AspectBox.SelectedIndex < 0 || !_hasSession) return;
        var (_, aspect) = Shapes[AspectBox.SelectedIndex];
        if (aspect <= 0)
        {
            // "the first clip's": the shape of the first video put on the timeline, or wide until there is one
            _aspectSet = false;
            _aspect = Video.NaturalVideoWidth > 0 && Video.NaturalVideoHeight > 0 ? (double)Video.NaturalVideoWidth / Video.NaturalVideoHeight : 16.0 / 9;
        }
        else { _aspect = aspect; _aspectSet = true; }
        FitStage();
        _savedProject = Project.Empty();   // the shape is saved with the edit, so changing it counts as an unsaved change
        Focus();
    }

    private void Snap_Click(object sender, RoutedEventArgs e) { Tl.SnapEnabled = SnapToggle.IsChecked == true; Focus(); }

    // ---- copy, paste, duplicate

    private List<(int Track, TimelineItem Item)> _clipboard = new();

    private void CopySelection()
    {
        if (Tl.Selection.Count == 0) { StatusText.Text = "Select something to copy first"; return; }
        _clipboard = _h.Current.Pick(Tl.Selection);
        StatusText.Text = $"Copied {_clipboard.Count} item{(_clipboard.Count == 1 ? "" : "s")}: Ctrl+V pastes at the playhead";
    }

    private void PasteAtPlayhead()
    {
        if (_clipboard.Count == 0) { StatusText.Text = "Nothing copied yet"; return; }
        PasteCopies(_clipboard, _t);
    }

    private void Duplicate()
    {
        if (Tl.Selection.Count == 0) { StatusText.Text = "Select something to duplicate first"; return; }
        var picked = _h.Current.Pick(Tl.Selection);
        PasteCopies(picked, picked.Max(x => x.Item.End));   // right after the last of them
    }

    private void PasteCopies(List<(int Track, TimelineItem Item)> items, double at)
    {
        var (next, ids) = _h.Current.Paste(items, at);
        if (ids.Count == 0) { StatusText.Text = "There was no room to paste"; return; }
        _h.Apply(next);
        Tl.SetSelection(ids); _selected = ids[^1];
        AfterEdit();
    }
}
