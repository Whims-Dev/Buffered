using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Buffered.UI;

/// <summary>
/// The list of Discord webhooks: a color, a name, reordering (by dragging or with the arrows), removing, and adding.
/// It is used in Settings and in the share window; both edit the same list, so a change in one shows in the other.
/// </summary>
public sealed class WebhookEditor : UserControl
{
    public static readonly string[] Palette = { "#7FA6FF", "#9B8CF0", "#5ED0A0", "#F0A56C", "#E07FB0", "#C9C46A", "#F0A0A0", "#8FD3D9" };

    /// <summary>Raised when the list changed anywhere, so every editor on screen (and the share window's buttons) refresh.</summary>
    public static event Action? ListChanged;
    public static void NotifyChanged() => ListChanged?.Invoke();

    private const string DragFormat = "Buffered.Webhook";

    private Settings? _s;
    private readonly StackPanel _rows = new();
    private readonly Button _addButton;
    private readonly Border _form;
    private TextBox _url = null!, _name = null!;
    private TextBlock _urlHint = null!, _nameHint = null!, _error = null!;
    private Button _formAdd = null!;
    private readonly Button _formColorButton = new();
    private string _formColor = Palette[0];
    private string? _editing;

    public WebhookEditor()
    {
        _addButton = new Button
        {
            Height = 34, Padding = new Thickness(14, 0, 14, 0), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Add webhook", VerticalAlignment = VerticalAlignment.Center },
                }
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(_addButton, "Add a Discord webhook");
        _addButton.Click += (_, _) => { _form.Visibility = _form.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; if (_form.Visibility == Visibility.Visible) _url.Focus(); };

        _form = BuildForm();
        Content = new StackPanel { Children = { _rows, _addButton, _form } };
    }

    private bool _attached;

    /// <summary>Connects the editor to the settings it edits. The editors live as long as the window, so this subscribes once.</summary>
    public void Attach(Settings settings)
    {
        _s = settings;
        if (!_attached) { _attached = true; ListChanged += Rebuild; }
        Rebuild();
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private static SolidColorBrush Hex(string hex)
    {
        try { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; }
        catch { return new SolidColorBrush(Color.FromRgb(0x7F, 0xA6, 0xFF)); }
    }

    private void Save()
    {
        try { _s?.Save(); } catch { /* the list still works for this session */ }
        NotifyChanged();
    }

    // ---- the list ---------------------------------------------------------------

    private void Rebuild()
    {
        if (_s is null) return;
        _rows.Children.Clear();
        for (int i = 0; i < _s.Webhooks.Count; i++) _rows.Children.Add(BuildRow(_s.Webhooks[i], i));
    }

    private Button IconButton(string glyph, string name, bool enabled = true)
    {
        var b = new Button
        {
            Width = 30, Height = 30, MinHeight = 0, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Content = glyph, ToolTip = name, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center,
            Opacity = enabled ? 1 : 0.3,
        };
        System.Windows.Automation.AutomationProperties.SetName(b, name);
        return b;
    }

    private UIElement BuildRow(WebhookTarget h, int index)
    {
        var grid = new Grid { Height = 46 };
        foreach (var w in new[] { 26.0, 28.0, -1.0, 0.0, 32.0, 32.0, 32.0 })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : w == 0 ? GridLength.Auto : new GridLength(w) });

        // the handle: drag from here
        var grip = new TextBlock { Text = "⋮⋮", FontSize = 14, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.SizeAll, ToolTip = "Drag to reorder", Background = Brushes.Transparent, Padding = new Thickness(4, 6, 4, 6) };
        grip.PreviewMouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragDrop.DoDragDrop(grip, new DataObject(DragFormat, h.Id), DragDropEffects.Move);
        };
        Grid.SetColumn(grip, 0); grid.Children.Add(grip);

        // the color
        var dot = new Button
        {
            Width = 22, Height = 22, MinHeight = 0, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = "Change color",
            Content = new Ellipse { Width = 16, Height = 16, Fill = Hex(h.Color), Stroke = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)), StrokeThickness = 2 },
        };
        System.Windows.Automation.AutomationProperties.SetName(dot, $"Change the color of {h.Label}");
        dot.Click += (_, _) => ShowPalette(dot, h);
        Grid.SetColumn(dot, 1); grid.Children.Add(dot);

        // the name: click to rename
        var label = new Border { Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Cursor = Cursors.IBeam, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Click to rename" };
        var text = new TextBlock { Text = h.Label, TextTrimming = TextTrimming.CharacterEllipsis };
        label.Child = text;
        label.MouseLeftButtonUp += (_, _) => BeginRename(label, text, h);
        Grid.SetColumn(label, 2); grid.Children.Add(label);

        // the end of its link, so two with the same name can be told apart (never the secret part)
        string tail = DiscordClient.IdOf(h.Url) is { Length: >= 4 } id ? $"…/{id[..4]}…" : "";
        var tailText = new TextBlock { Text = tail, FontSize = 11, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), FontFamily = new FontFamily("Consolas") };
        Grid.SetColumn(tailText, 3); grid.Children.Add(tailText);

        var up = IconButton("", $"Move {h.Label} up", index > 0);
        var down = IconButton("", $"Move {h.Label} down", index < (_s?.Webhooks.Count ?? 0) - 1);
        var del = IconButton("", $"Remove {h.Label}");
        up.Click += (_, _) => Move(h.Id, -1);
        down.Click += (_, _) => Move(h.Id, +1);
        del.Click += (_, _) => { _s?.Webhooks.Remove(h); if (_s?.LastWebhook == h.Id) _s.LastWebhook = ""; Save(); };
        Grid.SetColumn(up, 4); Grid.SetColumn(down, 5); Grid.SetColumn(del, 6);
        grid.Children.Add(up); grid.Children.Add(down); grid.Children.Add(del);

        // the line that shows where a dragged webhook will land
        var top = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, Background = Res("AccentFillColorDefaultBrush"), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        var bottom = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, Background = Res("AccentFillColorDefaultBrush"), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        Grid.SetColumnSpan(top, 7); Grid.SetColumnSpan(bottom, 7);
        grid.Children.Add(top); grid.Children.Add(bottom);

        var root = new Border { Child = grid, AllowDrop = true, BorderThickness = new Thickness(0, 0, 0, 1), Background = Brushes.Transparent };
        root.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        bool Below(DragEventArgs e) => e.GetPosition(root).Y > root.ActualHeight / 2;
        void Hide() { top.Visibility = bottom.Visibility = Visibility.Collapsed; }
        root.DragOver += (_, e) =>
        {
            e.Handled = true;
            if (!e.Data.GetDataPresent(DragFormat) || (string)e.Data.GetData(DragFormat)! == h.Id) { e.Effects = DragDropEffects.None; Hide(); return; }
            e.Effects = DragDropEffects.Move;
            bool below = Below(e);
            top.Visibility = below ? Visibility.Collapsed : Visibility.Visible;
            bottom.Visibility = below ? Visibility.Visible : Visibility.Collapsed;
        };
        root.DragLeave += (_, _) => Hide();
        root.Drop += (_, e) =>
        {
            e.Handled = true; Hide();
            if (_s is null || !e.Data.GetDataPresent(DragFormat) || (string)e.Data.GetData(DragFormat)! is not { } dragged || dragged == h.Id) return;
            var item = _s.Webhooks.FirstOrDefault(x => x.Id == dragged);
            if (item is null) return;
            bool below = Below(e);
            _s.Webhooks.Remove(item);
            int at = _s.Webhooks.FindIndex(x => x.Id == h.Id);
            _s.Webhooks.Insert(Math.Max(0, at) + (below ? 1 : 0), item);   // the line shows exactly this spot
            Save();
        };
        return root;
    }

    private void Move(string id, int step)
    {
        if (_s is null) return;
        int i = _s.Webhooks.FindIndex(x => x.Id == id), j = i + step;
        if (i < 0 || j < 0 || j >= _s.Webhooks.Count) return;
        (_s.Webhooks[i], _s.Webhooks[j]) = (_s.Webhooks[j], _s.Webhooks[i]);
        Save();
    }

    private void BeginRename(Border host, TextBlock text, WebhookTarget h)
    {
        if (_editing == h.Id) return;
        _editing = h.Id;
        var box = new TextBox { Text = h.Label, MinHeight = 0, Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0) };
        host.Child = box; host.Cursor = Cursors.Arrow;
        box.Focus(); box.SelectAll();
        bool cancelled = false;
        void Finish()
        {
            _editing = null;
            string v = box.Text.Trim();
            if (!cancelled && v.Length > 0 && v != h.Label) { h.Label = v; Save(); }
            else { host.Child = text; host.Cursor = Cursors.IBeam; }
        }
        box.LostFocus += (_, _) => Finish();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Keyboard.ClearFocus(); host.Focus(); }
            else if (e.Key == Key.Escape) { e.Handled = true; cancelled = true; Keyboard.ClearFocus(); host.Focus(); }
        };
    }

    private void ShowPalette(Button target, WebhookTarget h) =>
        ColorPalette.Show(target, $"Color for {h.Label}", h.Color, allowNone: false, picked => { if (picked is not null) { h.Color = picked; Save(); } });

    // ---- adding one -------------------------------------------------------------

    private Border BuildForm()
    {
        (Grid, TextBox, TextBlock) Field(string hint)
        {
            var box = new TextBox { Height = 34, VerticalContentAlignment = VerticalAlignment.Center };
            var tb = new TextBlock { Text = hint, IsHitTestVisible = false, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0) };
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8), Children = { box, tb } };
            box.TextChanged += (_, _) => tb.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            return (g, box, tb);
        }
        var (urlGrid, url, urlHint) = Field("https://discord.com/api/webhooks/…");
        var (nameGrid, name, nameHint) = Field("A name for it, like Valorant squad (optional)");
        _url = url; _urlHint = urlHint; _name = name; _nameHint = nameHint;
        System.Windows.Automation.AutomationProperties.SetName(_url, "Webhook link");
        System.Windows.Automation.AutomationProperties.SetName(_name, "Webhook name");

        _error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0xA0)), TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 2, 0, 6), MinHeight = 16 };
        var cancel = new Button { Content = "Cancel", Height = 34, MinWidth = 84, Margin = new Thickness(0, 0, 8, 0) };
        _formAdd = new Button { Content = "Add", Height = 34, MinWidth = 84, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        cancel.Click += (_, _) => { _form!.Visibility = Visibility.Collapsed; };
        _formAdd.Click += async (_, _) => await AddAsync();

        var swatchRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
        swatchRow.Children.Add(new TextBlock { Text = "Color", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        swatchRow.Children.Add(_formColorButton);
        _formColorButton.Height = 30; _formColorButton.Padding = new Thickness(10, 0, 12, 0);
        System.Windows.Automation.AutomationProperties.SetName(_formColorButton, "Choose a color");
        _formColorButton.Click += (_, _) => ColorPalette.Show(_formColorButton, "Webhook color", _formColor, allowNone: false, picked => { if (picked is not null) { _formColor = picked; PaintFormSwatches(); } });
        PaintFormSwatches();

        var card = new Border
        {
            Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "In Discord: Server Settings, Integrations, Webhooks, New Webhook, then Copy Webhook URL.", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) },
                    urlGrid, nameGrid, swatchRow, _error,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _formAdd } },
                }
            },
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        return card;
    }

    /// <summary>Shows the chosen color on the form's color button (a dot and its code).</summary>
    private void PaintFormSwatches()
    {
        _formColorButton.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new Ellipse { Width = 16, Height = 16, Fill = Hex(_formColor), Stroke = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)), StrokeThickness = 1, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = _formColor.ToUpperInvariant(), FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center },
            },
        };
    }

    private async Task AddAsync()
    {
        if (_s is null) return;
        string url = _url.Text.Trim();
        if (!DiscordClient.IsWebhookUrl(url)) { _error.Text = "That doesn't look like a Discord webhook link."; return; }
        string? id = DiscordClient.IdOf(url);
        if (_s.Webhooks.Any(w => DiscordClient.IdOf(w.Url) == id)) { _error.Text = "That webhook is already in the list."; return; }

        _formAdd.IsEnabled = false; _error.Text = "Checking with Discord…";
        var (info, error) = await DiscordClient.GetInfoAsync(url);
        _formAdd.IsEnabled = true;
        if (info is null) { _error.Text = error; return; }

        string label = _name.Text.Trim();
        if (label.Length == 0) label = info.Name.Length > 0 ? info.Name : "Discord";
        _s.Webhooks.Add(new WebhookTarget { Label = label, Color = _formColor, Url = url, GuildId = info.GuildId, ChannelId = info.ChannelId });
        _url.Clear(); _name.Clear(); _error.Text = "";
        _formColor = Palette[_s.Webhooks.Count % Palette.Length]; PaintFormSwatches();
        _form.Visibility = Visibility.Collapsed;
        Save();
    }
}
