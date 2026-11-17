using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Buffered.UI;

/// <summary>The per-game settings panel under "Record game windows".</summary>
public partial class MainWindow
{
    private string? _selectedGame;
    private bool _loadingGame;
    private string _gameComboKey = "";

    private sealed record OptChoice(int? Value, string Label) { public override string ToString() => Label; }

    private static readonly int[] GameHeights = { 720, 1080, 1440 };
    private static readonly int[] GameRates = { 30, 60, 120 };
    private static readonly int[] GameBuffers = { 30, 120, 300, 600 };
    private static readonly Brush Gold = new SolidColorBrush(Color.FromRgb(0xE0, 0xB8, 0x4A));

    private GameProfile? SelectedProfile => _selectedGame is null ? null : _settings.ProfileFor(_selectedGame);

    /// <summary>The selected game's settings, created if it has none yet.</summary>
    private GameProfile EditProfile()
    {
        var key = Settings.GameKey(_selectedGame!);
        if (!_settings.GameProfiles.TryGetValue(key, out var p)) _settings.GameProfiles[key] = p = new GameProfile();
        return p;
    }

    private bool IsGameEdited(string name) => _settings.ProfileFor(name)?.IsEdited == true;

    private void SelectGame(string name)
    {
        _selectedGame = string.Equals(_selectedGame, name, StringComparison.OrdinalIgnoreCase) ? null : name;
        RebuildGameChips();
    }

    /// <summary>Fills the panel from the selected game's settings (anything it has not changed shows the current default).</summary>
    private void RefreshGamePanel()
    {
        if (_selectedGame is null || !_games.Contains(_selectedGame, StringComparer.OrdinalIgnoreCase))
        {
            _selectedGame = null;
            GamePanel.Visibility = Visibility.Collapsed;
            return;
        }

        _loadingGame = true;
        try
        {
            var p = SelectedProfile ?? new GameProfile();
            GamePanel.Visibility = Visibility.Visible;
            GamePanelTitle.Inlines.Clear();
            GamePanelTitle.Inlines.Add(new Run("Settings for "));
            GamePanelTitle.Inlines.Add(new Run(_selectedGame) { FontWeight = FontWeights.SemiBold });

            // The lists mention the current default, so they are rebuilt only when that changes.
            string key = $"{_settings.Height}|{_settings.Fps}|{_settings.BufferSeconds}";
            if (key != _gameComboKey)
            {
                _gameComboKey = key;
                GameResBox.ItemsSource = new[] { new OptChoice(null, $"Current ({_settings.Height}p)") }.Concat(GameHeights.Select(h => new OptChoice(h, $"{h}p"))).ToList();
                GameFpsBox.ItemsSource = new[] { new OptChoice(null, $"Current ({_settings.Fps} fps)") }.Concat(GameRates.Select(r => new OptChoice(r, $"{r} fps"))).ToList();
                GameBufferBox.ItemsSource = new[] { new OptChoice(null, $"Current ({FormatBuffer(_settings.BufferSeconds)})") }.Concat(GameBuffers.Select(b => new OptChoice(b, FormatBuffer(b)))).ToList();
            }
            GameResBox.SelectedItem = ((IEnumerable<OptChoice>)GameResBox.ItemsSource).FirstOrDefault(c => c.Value == p.Height);
            GameFpsBox.SelectedItem = ((IEnumerable<OptChoice>)GameFpsBox.ItemsSource).FirstOrDefault(c => c.Value == p.Fps);
            GameBufferBox.SelectedItem = ((IEnumerable<OptChoice>)GameBufferBox.ItemsSource).FirstOrDefault(c => c.Value == p.BufferSeconds);

            GameQualityNow.Text = $"Current setting: {_settings.Height}p · {_settings.Fps} fps";
            GameBufferNow.Text = $"Current setting: {FormatBuffer(_settings.BufferSeconds)}";
            GameGainNow.Text = $"Current setting: {DbText(_settings.GameGainDb)}";
            GameMicNow.Text = $"Current setting: {DbText(_settings.MicGainDb)}";

            GameGainSlider2.Value = Vol.DbToPercent(p.GameGainDb ?? _settings.GameGainDb);
            GameGainSlider2.Opacity = p.GameGainDb.HasValue ? 1 : 0.55;
            GameGainValue.Text = p.GameGainDb is { } g ? DbText(g) : "Current";
            GameMicSlider2.Value = Vol.DbToPercent(p.MicGainDb ?? _settings.MicGainDb);
            GameMicSlider2.Opacity = p.MicGainDb.HasValue ? 1 : 0.55;
            GameMicValue.Text = p.MicGainDb is { } m ? DbText(m) : "Current";

            GameWindowOnlyBox.IsChecked = p.WindowOnly;
            if (GameFolderBox.Text != (p.Folder ?? "")) GameFolderBox.Text = p.Folder ?? "";

            Mark(GameWindowStar, GameWindowRevert, !p.WindowOnly);
            Mark(GameQualityStar, GameQualityRevert, p.Height.HasValue || p.Fps.HasValue);
            Mark(GameBufferStar, GameBufferRevert, p.BufferSeconds.HasValue);
            Mark(GameGainStar, GameGainRevert, p.GameGainDb.HasValue);
            Mark(GameMicStar, GameMicRevert, p.MicGainDb.HasValue);
            Mark(GameFolderStar, GameFolderRevert, !string.IsNullOrWhiteSpace(p.Folder));
            GameRevertAll.Visibility = p.IsEdited ? Visibility.Visible : Visibility.Hidden;
        }
        finally { _loadingGame = false; }
    }

    /// <summary>An edited setting shows a gold asterisk and a revert arrow; the arrow's space is kept so rows stay aligned.</summary>
    private static void Mark(TextBlock star, Button revert, bool edited)
    {
        star.Visibility = edited ? Visibility.Visible : Visibility.Collapsed;
        revert.Visibility = edited ? Visibility.Visible : Visibility.Hidden;
    }

    private void AfterGameEdit()
    {
        // A game whose settings all match the defaults again has no settings of its own.
        if (_selectedGame is not null && SelectedProfile is { IsEdited: false }) _settings.GameProfiles.Remove(Settings.GameKey(_selectedGame));
        RebuildGameChips();
        Setting_Changed(GamePanel, new RoutedEventArgs());
    }

    private void GameEdit_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingGame || _selectedGame is null) return;
        var p = EditProfile();
        p.WindowOnly = GameWindowOnlyBox.IsChecked == true;
        p.Height = (GameResBox.SelectedItem as OptChoice)?.Value;
        p.Fps = (GameFpsBox.SelectedItem as OptChoice)?.Value;
        p.BufferSeconds = (GameBufferBox.SelectedItem as OptChoice)?.Value;
        AfterGameEdit();
    }

    private void GameGain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingGame || _selectedGame is null) return;
        EditProfile().GameGainDb = Vol.PercentToDb(e.NewValue);
        AfterGameEdit();
    }

    private void GameMic_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingGame || _selectedGame is null) return;
        EditProfile().MicGainDb = Vol.PercentToDb(e.NewValue);
        AfterGameEdit();
    }

    private void GameFolder_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingGame || _selectedGame is null) return;
        var text = GameFolderBox.Text.Trim();
        EditProfile().Folder = text.Length == 0 ? null : text;
        AfterGameEdit();
    }

    private void GameRevert_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null || SelectedProfile is not { } p) return;
        switch ((string)((Button)sender).Tag)
        {
            case "window": p.WindowOnly = true; break;
            case "quality": p.Height = null; p.Fps = null; break;
            case "buffer": p.BufferSeconds = null; break;
            case "game": p.GameGainDb = null; break;
            case "mic": p.MicGainDb = null; break;
            case "folder": p.Folder = null; break;
        }
        AfterGameEdit();
    }

    private void GameRevertAll_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null) return;
        _settings.GameProfiles.Remove(Settings.GameKey(_selectedGame));
        AfterGameEdit();
    }

    /// <summary>The parts of every game's settings that need recording to restart when they change (volumes and folder do not).</summary>
    private string GameProfilesKey() => string.Join(",", _settings.GameProfiles.OrderBy(k => k.Key)
        .Select(kv => $"{kv.Key}:{kv.Value.WindowOnly}:{kv.Value.Height}:{kv.Value.Fps}:{kv.Value.BufferSeconds}"));
}
