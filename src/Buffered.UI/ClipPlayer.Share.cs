using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace Buffered.UI;

/// <summary>
/// "Share": makes a smaller copy of the clip (the part that is selected, with the audio as it is now), copies the original, or sends it
/// to one of the Discord webhooks the user has set up.
/// </summary>
public partial class ClipPlayer
{
    // Mb = 0 means the clip as it is, with no compression.
    private static readonly (int Mb, string Label, string Note)[] ShareSizes =
    {
        (10, "10 MB", "Good for Discord on a free server."),
        (20, "20 MB", "The most a Discord webhook accepts."),
        (25, "25 MB", "Good for email and most chat apps."),
        (50, "50 MB", "Good for Discord Nitro Basic and boosted servers."),
        (0, "Original", ""),
    };
    private bool ShareIsOriginal => _shareMb == 0;
    private bool _shareSyncing;

    private int _shareMb = 10;
    private string? _shareOut;
    private bool _shareBusy;
    private readonly List<ToggleButton> _shareButtons = new();

    // ---- settings and webhooks -----------------------------------------------------

    private Settings? _appSettings;
    private string _hookId = "";
    private string? _linkMessage, _linkApp;

    /// <summary>The app's settings: the webhooks are kept there.</summary>
    public Settings? AppSettings
    {
        get => _appSettings;
        set
        {
            bool first = _appSettings is null;
            _appSettings = value;
            if (value is null) return;
            ShareEditor.Attach(value);
            if (first) WebhookEditor.ListChanged += () => { RefreshHooks(); };
        }
    }

    private WebhookTarget? SelectedHook => _appSettings?.Webhooks.FirstOrDefault(h => h.Id == _hookId);

    private static readonly Brush DarkInk = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x1A));

    private static readonly ControlTemplate PillTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ToggleButton'>" +
        "<Border CornerRadius='17' Padding='{TemplateBinding Padding}' BorderThickness='1' BorderBrush='{TemplateBinding BorderBrush}' Background='{TemplateBinding Background}'>" +
        "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border></ControlTemplate>");

    private static SolidColorBrush Hex(string hex)
    {
        try { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; }
        catch { return new SolidColorBrush(Color.FromRgb(0x7F, 0xA6, 0xFF)); }
    }

    /// <summary>The "Send to" buttons, one per webhook in the user's order, and the colored send button for the one picked.</summary>
    private void RefreshHooks()
    {
        var hooks = _appSettings?.Webhooks ?? new List<WebhookTarget>();
        if (hooks.All(h => h.Id != _hookId))
            _hookId = hooks.FirstOrDefault(h => h.Id == _appSettings?.LastWebhook)?.Id ?? hooks.FirstOrDefault()?.Id ?? "";

        SharePills.Children.Clear();
        foreach (var h in hooks)
        {
            bool on = h.Id == _hookId;
            var color = Hex(h.Color);
            var text = new TextBlock { Text = h.Label, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190, VerticalAlignment = VerticalAlignment.Center };
            var dot = new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center, Fill = on ? DarkInk : color };
            var pill = new ToggleButton
            {
                Template = PillTemplate, Height = 34, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 8, 8), IsChecked = on, Cursor = Cursors.Hand,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, text } },
                Background = on ? color : (Brush)FindResource("ControlFillColorDefaultBrush"),
                BorderBrush = on ? color : (Brush)FindResource("ControlStrokeColorDefaultBrush"),
                Foreground = on ? DarkInk : (Brush)FindResource("TextFillColorPrimaryBrush"),
            };
            text.Foreground = pill.Foreground;
            System.Windows.Automation.AutomationProperties.SetName(pill, $"Send to {h.Label}");
            var id = h.Id;
            pill.Click += (_, _) => { if (_shareBusy) { RefreshHooks(); return; } _hookId = id; RefreshHooks(); ResetShareResult(); };
            SharePills.Children.Add(pill);
        }

        bool any = hooks.Count > 0;
        ShareToLabel.Visibility = SharePills.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        ShareNoHooks.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        var sel = SelectedHook;
        ShareSend.Visibility = sel is null ? Visibility.Collapsed : Visibility.Visible;
        if (sel is not null)
        {
            var color = Hex(sel.Color);
            ShareSend.Background = color; ShareSend.BorderBrush = color; ShareSend.Foreground = DarkInk;
            ShareSend.Content = new TextBlock { Text = "Send to " + sel.Label, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = DarkInk };
        }
        // With nothing to send to, making a copy is the main action.
        if (sel is null) ShareGo.Style = (Style)FindResource("PrimaryButton");
        else ShareGo.ClearValue(StyleProperty);
    }

    private void ShareMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = ShareMore.ContextMenu;
        if (menu is null) return;
        menu.PlacementTarget = ShareMore; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }

    private void ShareManage_Click(object sender, RoutedEventArgs e)
    {
        ShareManage.Visibility = ShareManage.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShareScrim_SizeChanged(object sender, SizeChangedEventArgs e) => ShareCard.MaxHeight = Math.Max(320, ShareScrim.ActualHeight - 60);

    // ---- opening the window -----------------------------------------------------

    /// <summary>Moves the share window out to the main window's top layer, so it dims everything (the side bar too), like the other dialogs.</summary>
    public void MoveShareTo(Grid host, UIElement above)
    {
        if (ShareScrim.Parent is Panel old) old.Children.Remove(ShareScrim);
        Grid.SetRowSpan(ShareScrim, 2); Grid.SetColumnSpan(ShareScrim, 2);
        host.Children.Insert(Math.Max(0, host.Children.IndexOf(above)), ShareScrim);
    }

    // Shared from the clip list instead of the editor: the whole clip, the sound as saved.
    private bool _shareFromList;
    private double _shareListLength;
    private double ShareStart => _shareFromList ? 0 : Bar.Start;
    private double ShareEnd => _shareFromList ? _shareListLength : Bar.End;
    private bool ShareHasTrim => !_shareFromList && HasTrim;
    private bool ShareAudioEdited => !_shareFromList && AudioChanged;

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (_clip is null || _duration <= 0) return;
        _shareFromList = false;
        ShowShare();
    }

    /// <summary>Opens the share window for a clip picked in the list, without opening the editor.</summary>
    public async Task ShareClipAsync(ClipItem clip)
    {
        var length = await Media.GetDurationSecondsAsync(clip.Path);
        if (length is not > 0) return;
        _clip = clip;
        _shareFromList = true; _shareListLength = length.Value;
        ShowShare();
    }

    private void ShowShare()
    {
        if (_shareButtons.Count == 0) BuildShareOptions();
        ShareManage.Visibility = Visibility.Collapsed;
        RefreshHooks();
        ResetShare();
        ShareScrim.Visibility = Visibility.Visible;
        (ShareSend.Visibility == Visibility.Visible ? ShareSend : (Control)ShareGo).Focus();
    }

    private void BuildShareOptions()
    {
        for (int i = 0; i < ShareSizes.Length; i++)
        {
            var (mb, label, _) = ShareSizes[i];
            var b = new ToggleButton
            {
                Content = label, Tag = mb, Height = 38, Margin = new Thickness(0, 0, i == ShareSizes.Length - 1 ? 0 : 6, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(b, mb == 0 ? "Original quality" : $"Under {mb} MB");
            b.Click += (_, _) => { if (_shareBusy) { b.IsChecked = (int)b.Tag == _shareMb; return; } _shareMb = mb; ResetShare(); };
            // Checked as well as Click, so assistive tools that toggle the button choose the size too.
            b.Checked += (_, _) => { if (_shareBusy || _shareSyncing || _shareMb == mb) return; _shareMb = mb; ResetShare(); };
            _shareButtons.Add(b);
            ShareOptions.Children.Add(b);
        }
    }

    /// <summary>Back to "ready": the options, the estimate, and the buttons that start the copy or the send.</summary>
    private void ResetShare()
    {
        _shareOut = null;
        _shareSyncing = true;
        foreach (var b in _shareButtons) b.IsChecked = (int)b.Tag == _shareMb;
        _shareSyncing = false;

        double secs = Math.Max(0.5, ShareEnd - ShareStart);
        long original = 0; try { original = new FileInfo(_clip!.Path).Length; } catch { }
        bool edited = ShareHasTrim || ShareAudioEdited;
        if (ShareIsOriginal)
        {
            ShareEstimate.Text = edited
                ? "You've changed this clip in the editor. Use Save copy first, then share the copy you saved."
                : $"Shares the clip exactly as it is: {original / 1048576.0:0} MB at full quality. Some apps won't take a file this big.";
            ShareGo.Content = "Copy file";
        }
        else
        {
            var plan = ShareExport.PlanFor(_shareMb, secs);
            string note = ShareSizes.First(s => s.Mb == _shareMb).Note;
            ShareEstimate.Text = $"{note} A {secs:0} second clip at about {plan.Height}p, {plan.Fps} fps. The original is {original / 1048576.0:0} MB."
                + (ShareHasTrim ? " Only the part you selected is included." : "");
            ShareGo.Content = "Make smaller copy";
        }
        ResetShareResult();
        ShareClose.IsEnabled = true;
    }

    private void ResetShareResult()
    {
        ShareBar.Visibility = Visibility.Collapsed;
        ShareResult.Visibility = Visibility.Collapsed;
        ShareLinks.Visibility = Visibility.Collapsed;
        _linkMessage = _linkApp = null;
    }

    // ---- the copy button ---------------------------------------------------------

    private async void ShareGo_Click(object sender, RoutedEventArgs e)
    {
        if (_shareBusy || _clip is null) return;
        if (ShareIsOriginal)
        {
            // No compression: the clip's own file goes on the clipboard, so it can be pasted straight into a chat.
            if (ShareHasTrim || ShareAudioEdited) return;   // the estimate text already says to save a copy first
            try
            {
                Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { _clip.Path });
                ShareResult.Text = "File copied. Paste it into your chat with Ctrl+V.";
            }
            catch (Exception ex) { ShareResult.Text = "Couldn't copy the file: " + ex.Message; }
            ShareResult.Visibility = Visibility.Visible;
            return;
        }
        if (_shareOut is not null)
        {
            // The copy is ready: put the file on the clipboard, so it can be pasted straight into a chat.
            try
            {
                Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { _shareOut });
                ShareResult.Text = "File copied. Paste it into your chat with Ctrl+V.";
            }
            catch (Exception ex) { ShareResult.Text = "Couldn't copy the file: " + ex.Message; }
            ShareResult.Visibility = Visibility.Visible;
            return;
        }

        _shareBusy = true;
        ShareGo.Content = "Compressing…";
        ShareClose.IsEnabled = false; ShareSend.IsEnabled = false;
        ShareBar.Value = 0; ShareBar.Visibility = Visibility.Visible;
        ShareResult.Visibility = Visibility.Collapsed;

        string clipPath = _clip.Path;
        string? preview = null;
        try
        {
            string input = clipPath;
            if (ShareAudioEdited && _stems is not null)   // volumes and muted tracks changed in the editor are part of the copy
            {
                preview = await Media.MakeBalancedPreviewAsync(clipPath, _stems, TrackDbs, TrackInMix);
                if (preview is not null) input = preview;
            }
            var progress = new Progress<double>(v => ShareBar.Value = v);
            var (ok, path, bytes, error) = await ShareExport.RunAsync(input, ShareStart, ShareEnd, clipPath, _clip.Title, _shareMb, progress);
            ShareBar.Visibility = Visibility.Collapsed;
            if (ok)
            {
                _shareOut = path;
                // In the library the copy shows up as "Original Clip Name (10 MB)", not under its date-and-game file name.
                string fileTitle = Path.GetFileNameWithoutExtension(path);
                ClipLibrary.Update(path, m => m.Name = fileTitle);
                ShareResult.Text = $"Saved a smaller copy: {bytes / 1048576.0:0.0} MB, next to the original.";
                ShareGo.Content = "Copy file";
            }
            else
            {
                ShareResult.Text = "Couldn't make the copy. " + error;
                ShareGo.Content = "Try again";
            }
            ShareResult.Visibility = Visibility.Visible;
        }
        finally
        {
            if (preview is not null) { try { File.Delete(preview); } catch { } }
            _shareBusy = false;
            ShareClose.IsEnabled = true; ShareSend.IsEnabled = true;
        }
    }

    // ---- sending to Discord ------------------------------------------------------

    private async void ShareSend_Click(object sender, RoutedEventArgs e)
    {
        var hook = SelectedHook;
        if (hook is null || _shareBusy || _clip is null) return;
        if (ShareIsOriginal && (ShareHasTrim || ShareAudioEdited))
        {
            ShareResult.Text = "You've changed this clip in the editor. Use Save copy first, then share the copy you saved.";
            ShareResult.Visibility = Visibility.Visible;
            return;
        }

        _shareBusy = true;
        ShareClose.IsEnabled = false; ShareGo.IsEnabled = false; ShareSend.IsEnabled = false;
        ShareLinks.Visibility = Visibility.Collapsed;
        ShareBar.Value = 0; ShareBar.Visibility = Visibility.Visible;
        ShareResult.Text = ShareIsOriginal ? $"Sending to {hook.Label}…" : "Compressing…";
        ShareResult.Visibility = Visibility.Visible;

        var clip = _clip;
        string sendPath = clip.Path;
        string? preview = null, temp = null;
        try
        {
            double from = 0;
            if (!ShareIsOriginal)
            {
                string input = clip.Path;
                if (ShareAudioEdited && _stems is not null)
                {
                    preview = await Media.MakeBalancedPreviewAsync(clip.Path, _stems, TrackDbs, TrackInMix);
                    if (preview is not null) input = preview;
                }
                // The smaller copy is only made for sending: it is removed afterwards, not left next to the original.
                var squeeze = new Progress<double>(v => ShareBar.Value = v * 0.5);
                var (ok, path, _, error) = await ShareExport.RunAsync(input, ShareStart, ShareEnd, clip.Path, clip.Title, _shareMb, squeeze, keepBesideOriginal: false);
                if (!ok) { ShowSendResult("Couldn't make the copy. " + error, false); return; }
                temp = path; sendPath = path; from = 0.5;
                ShareResult.Text = $"Sending to {hook.Label}…";
            }

            var up = new Progress<double>(v => ShareBar.Value = from + v * (1 - from));
            string fileName = SafeFileName(clip.Title) + ".mp4";
            string size = $"{new FileInfo(sendPath).Length / 1048576.0:0.#} MB";
            var details = new List<(string, string)>();
            // "Other" is what a clip is called when the program wasn't recognised, which tells the reader nothing.
            if (!string.IsNullOrWhiteSpace(clip.Label) && !clip.Label.Equals("Other", StringComparison.OrdinalIgnoreCase)) details.Add(("Game", clip.Label));
            details.Add(("Length", Clock(Math.Max(0.5, ShareEnd - ShareStart))));
            details.Add(("Size", size));
            var r = await DiscordClient.SendAsync(hook, sendPath, fileName, clip.Title, clip.Note, details, clip.When, up);

            if (r.Ok)
            {
                if (_appSettings is not null && _appSettings.LastWebhook != hook.Id)
                {
                    _appSettings.LastWebhook = hook.Id;
                    try { _appSettings.Save(); } catch { }
                }
                _linkMessage = r.MessageUrl; _linkApp = r.AppUrl;
                ShowSendResult($"Sent to {hook.Label}.", true);
                ShareLinks.Visibility = _linkMessage is null ? Visibility.Collapsed : Visibility.Visible;
            }
            else ShowSendResult(r.Error, false);
        }
        finally
        {
            if (preview is not null) { try { File.Delete(preview); } catch { } }
            if (temp is not null) { try { File.Delete(temp); } catch { } }
            _shareBusy = false;
            ShareClose.IsEnabled = true; ShareGo.IsEnabled = true; ShareSend.IsEnabled = true;
        }
    }

    private void ShowSendResult(string text, bool good)
    {
        ShareBar.Visibility = Visibility.Collapsed;
        ShareResult.Text = text;
        ShareResult.Visibility = Visibility.Visible;
        ShareResult.Foreground = good ? new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B)) : new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0xA0));
    }

    private static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 ? "clip" : name;
    }

    private void ShareOpenLink_Click(object sender, RoutedEventArgs e)
    {
        // The app's own address opens Discord straight at the message; the web address is the fallback if Discord isn't installed.
        foreach (var address in new[] { _linkApp, _linkMessage })
        {
            if (address is null) continue;
            try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); return; }
            catch { /* try the next one */ }
        }
    }

    private void ShareCopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (_linkMessage is null) return;
        try { Clipboard.SetText(_linkMessage); ShareResult.Text = ShareResult.Text.TrimEnd() + " Link copied."; }
        catch (Exception ex) { ShareResult.Text = "Couldn't copy the link: " + ex.Message; }
    }

    private bool _sharePressedOutside;

    /// <summary>Clicking on the dimmed area around the share window closes it (not while a copy is being made). The whole click is taken, so nothing underneath reacts.</summary>
    private void ShareScrim_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ShareScrim)) return;
        _sharePressedOutside = true;
        ShareScrim.CaptureMouse();
        e.Handled = true;
    }

    private void ShareScrim_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_sharePressedOutside) return;
        _sharePressedOutside = false;
        ShareScrim.ReleaseMouseCapture();
        e.Handled = true;
        var over = e.GetPosition(ShareCard);
        bool onCard = over.X >= 0 && over.Y >= 0 && over.X <= ShareCard.ActualWidth && over.Y <= ShareCard.ActualHeight;
        if (!onCard && !_shareBusy) ShareScrim.Visibility = Visibility.Collapsed;
    }

    private void ShareClose_Click(object sender, RoutedEventArgs e) { if (!_shareBusy) ShareScrim.Visibility = Visibility.Collapsed; }

    private void Share_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !_shareBusy) { ShareScrim.Visibility = Visibility.Collapsed; e.Handled = true; }
    }
}
