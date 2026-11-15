using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Buffered.UI;

/// <summary>The Voice card in Settings: the on/off switch, the phrase list, sensitivity and the live status.</summary>
public partial class MainWindow
{
    private readonly List<string> _phrases = new();

    // Each sensitivity level is a pair: how sure the recognizer must be, and how strongly everyday speech competes with your phrases
    // (lower competition means a sound that could be either is more likely taken as your phrase).
    private static readonly (double Confidence, double Competition, double Fuzzy, double Forced) SensLowValue = (0.45, 0.05, 0.70, 0.50), SensMidValue = (0.55, 0.2, 0.80, 0.65), SensHighValue = (0.7, 0.5, 0.90, 0.80);

    private void PopulateVoice()
    {
        VoiceBox.IsChecked = _settings.VoiceClip;
        _phrases.Clear();
        _phrases.AddRange((_settings.VoicePhrases ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        RebuildPhraseChips();

        double c = _settings.VoiceConfidence;
        (c <= 0.5 ? SensLow : c >= 0.65 ? SensHigh : SensMid).IsChecked = true;
        UpdateVoiceEnabled();
    }

    /// <summary>Copies the voice controls into the settings object (called with the other settings when something changes).</summary>
    private void ReadVoice()
    {
        _settings.VoiceClip = VoiceBox.IsChecked == true;
        _settings.VoicePhrases = string.Join(", ", _phrases);
        var level = SensLow.IsChecked == true ? SensLowValue : SensHigh.IsChecked == true ? SensHighValue : SensMidValue;
        _settings.VoiceConfidence = level.Confidence;
        _settings.VoiceDictationWeight = level.Competition;
        _settings.VoiceFuzzy = level.Fuzzy;
        _settings.VoiceForcedConfidence = level.Forced;
    }

    private void UpdateVoiceEnabled()
    {
        bool on = VoiceBox.IsChecked == true;
        VoiceMore.IsEnabled = on;
        VoiceMore.Opacity = on ? 1 : 0.45;
    }

    private void Voice_Toggled(object sender, RoutedEventArgs e)
    {
        UpdateVoiceEnabled();
        Setting_Changed(sender, e);
    }

    private void Sens_Checked(object sender, RoutedEventArgs e) => Setting_Changed(sender, e);

    // ---- phrase chips and the add pill ---------------------------------------

    private void RebuildPhraseChips()
    {
        PhraseChips.Children.Clear();
        foreach (var p in _phrases)
        {
            var phrase = p;
            var label = new TextBlock { Text = phrase, VerticalAlignment = VerticalAlignment.Center };
            var x = new Button
            {
                Content = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Width = 22, Height = 22, MinHeight = 0, Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetName(x, "Remove " + phrase);
            x.Click += (_, e) => { _phrases.Remove(phrase); RebuildPhraseChips(); Setting_Changed(x, e); };
            PhraseChips.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(15), Height = 30, Padding = new Thickness(14, 0, 4, 0), Margin = new Thickness(0, 2, 6, 2),
                BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource("ControlStrokeColorDefaultBrush"),
                Background = (Brush)FindResource("ControlFillColorDefaultBrush"),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, x } }
            });
        }
    }

    private void AddPhrase_Click(object sender, RoutedEventArgs e)
    {
        AddPhraseButton.Visibility = Visibility.Collapsed;
        PhraseEditor.Visibility = Visibility.Visible;
        PhraseBox.Text = "";
        PhraseBox.Focus();
    }

    private void EndPhraseEdit(bool commit)
    {
        if (PhraseEditor.Visibility != Visibility.Visible) return;
        var text = PhraseBox.Text.Trim().ToLowerInvariant().Replace(",", " ").Replace(";", " ");
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        PhraseEditor.Visibility = Visibility.Collapsed;
        AddPhraseButton.Visibility = Visibility.Visible;
        if (commit && text.Length > 0 && !_phrases.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            _phrases.Add(text);
            RebuildPhraseChips();
            Setting_Changed(PhraseBox, new RoutedEventArgs());
        }
    }

    private void PhraseBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; EndPhraseEdit(true); }
        else if (e.Key == Key.Escape) { e.Handled = true; EndPhraseEdit(false); }
    }

    private void PhraseBox_Lost(object sender, KeyboardFocusChangedEventArgs e) => EndPhraseEdit(true);   // clicking away adds what was typed

    // ---- live status ---------------------------------------------------------

    /// <summary>The last part of a long caption, starting at a word, so the newest words are what you see.</summary>
    private static string Tail(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = text[^max..];
        int space = cut.IndexOf(' ');
        return "…" + (space > 0 && space < 15 ? cut[(space + 1)..] : cut);
    }

    private void ShowVoice(RecorderStatus? s)
    {
        string text = s is null ? (VoiceBox.IsChecked == true ? "Waiting for the recorder" : "Off")
                    : s.Voice.Length > 0 ? s.Voice
                    : VoiceBox.IsChecked == true ? "Starting…" : "Off";
        bool listening = s is not null && s.Voice.StartsWith("Listening");
        VoiceStatusText.Text = text;
        VoiceDot.Fill = new SolidColorBrush(listening ? Color.FromRgb(0x2E, 0x9E, 0x5B) : s is not null && s.Voice.Length > 0 && !listening && text != "Starting…" ? Color.FromRgb(0xE0, 0xA0, 0x30) : Colors.Gray);
        // Live captions: what is being heard right now, then what became of it.
        bool live = s is { VoiceLive.Length: > 0 };
        VoiceHeardLabel.Text = live ? "Hearing" : "Last heard";
        VoiceHeardText.Text = live ? "“…" + Tail(s!.VoiceLive, 60) + "”" : s is { VoiceHeard.Length: > 0 } ? Tail(s.VoiceHeard, 80) : "Nothing yet";
        VoiceHeardText.Opacity = live ? 0.65 : 1;
    }
}
