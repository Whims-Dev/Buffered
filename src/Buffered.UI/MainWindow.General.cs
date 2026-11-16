using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Buffered.UI;

/// <summary>The General settings rows: start with Windows, shortcuts, update check.</summary>
public partial class MainWindow
{
    // ---- start with Windows --------------------------------------------------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Buffered";
    private static string RecorderExe => Path.Combine(AppContext.BaseDirectory, "BufferedRecorder.exe");
    private static string StartupCommand => $"\"{RecorderExe}\" --background";

    private static bool StartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string v && v.Contains("BufferedRecorder.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private bool _refreshingGeneral;

    private void StartupBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshingGeneral) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (StartupBox.IsChecked == true) key.SetValue(RunValue, StartupCommand);
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch (Exception ex) { Dialogs.Toast("Couldn't change startup", ex.Message, error: true); }
        RefreshGeneral();
    }

    // ---- shortcuts -----------------------------------------------------------

    private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Buffered.lnk");
    private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Buffered.lnk");
    private static string UiExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BufferedUI.exe");

    /// <summary>True if the shortcut exists and opens this copy of Buffered.</summary>
    private static bool ShortcutPointsHere(string link)
    {
        if (!File.Exists(link)) return false;
        try
        {
            var shell = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic sh = Activator.CreateInstance(shell)!;
            try
            {
                dynamic l = sh.CreateShortcut(link);
                string target = l.TargetPath;
                return string.Equals(Path.GetFullPath(target), Path.GetFullPath(UiExe), StringComparison.OrdinalIgnoreCase);
            }
            finally { Marshal.ReleaseComObject(sh); }
        }
        catch { return true; }   // it exists; if it can't be read, don't pretend otherwise
    }

    private static void CreateShortcut(string link)
    {
        var shell = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic sh = Activator.CreateInstance(shell)!;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            dynamic l = sh.CreateShortcut(link);
            l.TargetPath = UiExe;
            l.WorkingDirectory = Path.GetDirectoryName(UiExe)!;
            l.IconLocation = UiExe + ",0";
            l.Description = "Buffered";
            l.Save();
        }
        finally { Marshal.ReleaseComObject(sh); }
    }

    private void DesktopShortcut_Click(object sender, RoutedEventArgs e) => ToggleShortcut(DesktopLink, "desktop");
    private void StartMenuShortcut_Click(object sender, RoutedEventArgs e) => ToggleShortcut(StartMenuLink, "Start menu");

    private void ToggleShortcut(string link, string where)
    {
        try
        {
            if (ShortcutPointsHere(link)) File.Delete(link);
            else CreateShortcut(link);
        }
        catch (Exception ex) { Dialogs.Toast($"Couldn't change the {where} shortcut", ex.Message, error: true); }
        RefreshGeneral();
    }

    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B)));
    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private void ShowShortcut(Button button, TextBlock glyph, TextBlock label, bool on, string offText, string onText, string offGlyph)
    {
        label.Text = on ? onText : offText;
        glyph.Text = on ? "" : offGlyph;
        if (on) { glyph.Foreground = Green; label.Foreground = Green; }
        else { glyph.ClearValue(TextBlock.ForegroundProperty); label.ClearValue(TextBlock.ForegroundProperty); }
        button.ToolTip = on ? "Click to remove this shortcut" : "Click to add this shortcut";
    }

    /// <summary>Reads the real state of everything in the General rows, so the switches and buttons always match the system.</summary>
    private void RefreshGeneral()
    {
        _refreshingGeneral = true;
        try
        {
            StartupBox.IsChecked = StartupEnabled();
            ShowShortcut(DesktopShortcutButton, DesktopGlyph, DesktopLabel, ShortcutPointsHere(DesktopLink), "Desktop", "Desktop added", "");
            ShowShortcut(StartMenuShortcutButton, StartMenuGlyph, StartMenuLabel, ShortcutPointsHere(StartMenuLink), "Start menu", "Start menu added", "");
            if (_upd == UpdState.Idle) RenderUpdate();
        }
        finally { _refreshingGeneral = false; }
    }
}

/// <summary>What this build is, and where its releases live.</summary>
internal static class AppInfo
{
    /// <summary>The GitHub repository whose latest release is checked. Change this if you fork the project.</summary>
    public const string Repo = "Whims-Dev/Buffered";

    public static string Version =>
        (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0");
}
