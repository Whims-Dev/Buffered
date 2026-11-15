using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Buffered.UI;

/// <summary>Dark, in-app replacements for the Windows message box, plus a small notice that fades in at the top.</summary>
public partial class DialogHost : UserControl
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private TaskCompletionSource<bool>? _result;
    private IInputElement? _previousFocus;
    private Action? _toastAction;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    public DialogHost()
    {
        InitializeComponent();
        _toastTimer.Tick += (_, _) => HideToast();
    }

    /// <summary>Shows a question and returns true if the user confirmed. Escape cancels.</summary>
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false, string cancelText = "Cancel")
    {
        await _one.WaitAsync();
        try
        {
            TitleText.Text = title;
            MessageText.Text = message;
            CancelButton.Content = cancelText;
            CancelButton.Visibility = Visibility.Visible;
            ConfirmButton.Content = confirmText;
            ConfirmButton.Style = (Style)FindResource(danger ? "DangerButton" : "PrimaryButton");
            return await ShowAsync();
        }
        finally { _one.Release(); }
    }

    /// <summary>Shows a message with a single OK button.</summary>
    public async Task MessageAsync(string title, string message, string okText = "OK")
    {
        await _one.WaitAsync();
        try
        {
            TitleText.Text = title;
            MessageText.Text = message;
            CancelButton.Visibility = Visibility.Collapsed;
            ConfirmButton.Content = okText;
            ConfirmButton.Style = (Style)FindResource("PrimaryButton");
            await ShowAsync();
        }
        finally { _one.Release(); }
    }

    private Task<bool> ShowAsync()
    {
        _result = new TaskCompletionSource<bool>();
        _previousFocus = Keyboard.FocusedElement;
        Scrim.Opacity = 0;
        Scrim.Visibility = Visibility.Visible;
        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        ConfirmButton.Focus();
        return _result.Task;
    }

    private void Close(bool result)
    {
        Scrim.Visibility = Visibility.Collapsed;
        var r = _result; _result = null;
        if (_previousFocus is UIElement prev) prev.Focus();
        r?.TrySetResult(result);
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => Close(true);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(false);

    private void Scrim_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(false); e.Handled = true; }
    }

    // ---- notice -------------------------------------------------------------

    public void Toast(string title, string? subtitle = null, string? actionText = null, Action? action = null, bool error = false)
    {
        ToastTitle.Text = title;
        ToastSub.Text = subtitle ?? "";
        ToastSub.Visibility = string.IsNullOrEmpty(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        ToastGlyph.Text = error ? "" : "";          // error / check mark
        ToastGlyph.Foreground = new SolidColorBrush(error ? Color.FromRgb(0xD1, 0x34, 0x38) : Color.FromRgb(0x2E, 0x9E, 0x5B));
        ToastAction.Content = actionText;
        ToastAction.Visibility = actionText is null ? Visibility.Collapsed : Visibility.Visible;
        _toastAction = action;

        ToastCard.Background = SolidThemeBackground();
        ToastCard.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => { ToastCard.BeginAnimation(OpacityProperty, null); ToastCard.Opacity = 1; };
        ToastCard.BeginAnimation(OpacityProperty, fade);
        _toastTimer.Stop(); _toastTimer.Start();
    }

    /// <summary>The window background as a fully opaque colour, so cards never show what is behind them.</summary>
    private Brush SolidThemeBackground()
    {
        if (TryFindResource("ApplicationBackgroundBrush") is SolidColorBrush b)
        {
            var c = b.Color;
            // Lift it slightly so the card stands out from the page it floats over.
            byte lift(byte v) => (byte)Math.Min(255, v + 12);
            return new SolidColorBrush(Color.FromRgb(lift(c.R), lift(c.G), lift(c.B)));
        }
        return (Brush)FindResource("ApplicationBackgroundBrush");
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => ToastCard.Visibility = Visibility.Collapsed;
        ToastCard.BeginAnimation(OpacityProperty, fade);
    }

    private void ToastAction_Click(object sender, RoutedEventArgs e) { var a = _toastAction; HideToast(); a?.Invoke(); }
    private void ToastClose_Click(object sender, RoutedEventArgs e) => HideToast();
}
