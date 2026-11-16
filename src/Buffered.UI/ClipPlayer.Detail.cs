using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Buffered.UI;

/// <summary>The editor's own settings: what the timeline shows. They are reachable from the gear in the editor and from Settings.</summary>
public partial class ClipPlayer
{
    private static readonly (string Id, string Name, string Sub)[] DetailChoices =
    {
        ("wave", "Waveform", "See where the loud moments are"),
        ("film", "Filmstrip", "Little pictures along the timeline"),
        ("both", "Both", "Pictures with the sound over them"),
        ("none", "Plain", "Just the timeline and times"),
    };

    /// <summary>The Editor section of Settings was asked for.</summary>
    public event Action? OpenEditorSettingsRequested;
    /// <summary>The timeline detail was changed here (so Settings can show the same choice).</summary>
    public event Action? TimelineDetailChanged;

    private string CurrentDetail => _appSettings?.TimelineDetail is { Length: > 0 } d && DetailChoices.Any(c => c.Id == d) ? d : "wave";

    // What has been (or is being) made for the clip that is open; each is made once, the first time the setting asks for it.
    private bool _peaksTried, _stripTried;

    private void ResetDetail()
    {
        _peaksTried = _stripTried = false;
        Bar.Peaks = null; Bar.Strip = null;
        Bar.Detail = CurrentDetail;
    }

    /// <summary>Makes the sound and pictures the timeline is set to show (in the background, so the editor never waits for them).</summary>
    private async void EnsureDetail()
    {
        var clip = _clip;
        if (clip is null || _duration <= 0) return;
        Bar.Detail = CurrentDetail;
        double duration = _duration;

        if (Bar.WantsWave && !_peaksTried)
        {
            _peaksTried = true;
            var peaks = await Media.GetPeaksAsync(clip.Path);
            if (_clip == clip) Bar.Peaks = peaks;
        }
        if (Bar.WantsFilm && !_stripTried)
        {
            _stripTried = true;
            var strip = await Media.GetFilmstripAsync(clip.Path, duration, Bar.StripTiles);
            if (_clip == clip) Bar.Strip = strip;
        }
    }

    private void Gear_Click(object sender, RoutedEventArgs e)
    {
        BuildGear();
        GearPopup.IsOpen = !GearPopup.IsOpen;
    }

    private void GearMore_Click(object sender, RoutedEventArgs e)
    {
        GearPopup.IsOpen = false;
        OpenEditorSettingsRequested?.Invoke();
    }

    private void BuildGear()
    {
        GearOptions.Children.Clear();
        foreach (var (id, name, sub) in DetailChoices)
        {
            bool on = id == CurrentDetail;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = name, FontSize = 13 },
                    new TextBlock { Text = sub, FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 1, 0, 0) },
                },
            });
            var tick = new TextBlock
            {
                FontFamily = (FontFamily)FindResource("IconFont"), Text = "", FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("AccentTextFillColorPrimaryBrush"), Visibility = on ? Visibility.Visible : Visibility.Collapsed,
            };
            Grid.SetColumn(tick, 1); grid.Children.Add(tick);

            var button = new Button
            {
                Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 7, 12, 7), Margin = new Thickness(0, 0, 0, 3), MinHeight = 0, Tag = id,
                Background = on ? (Brush)FindResource("ControlFillColorSecondaryBrush") : Brushes.Transparent,
                BorderBrush = on ? (Brush)FindResource("ControlStrokeColorDefaultBrush") : Brushes.Transparent,
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"Timeline detail: {name}");
            string chosen = id;
            button.Click += (_, _) => { SetDetail(chosen); BuildGear(); };
            GearOptions.Children.Add(button);
        }
    }

    private void SetDetail(string id)
    {
        if (_appSettings is null || _appSettings.TimelineDetail == id) return;
        _appSettings.TimelineDetail = id;
        try { _appSettings.Save(); } catch { }
        EnsureDetail();
        TimelineDetailChanged?.Invoke();
    }
}
