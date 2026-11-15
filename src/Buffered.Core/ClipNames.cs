using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Buffered;

/// <summary>Clip files are named "{Label}_{yyyy-MM-dd_HH-mm-ss}.mp4", where the label is the app that was in front.</summary>
public static partial class ClipNames
{
    [GeneratedRegex(@"^(?<label>.*)_(?<stamp>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})$")]
    private static partial Regex Pattern();

    /// <summary>
    /// The hidden companion file next to a clip. It holds the Game and Mic as two separate tracks (already at the volume
    /// they were mixed at) so the editor can rebalance a finished clip.
    /// </summary>
    public static string StemsPath(string clipPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(clipPath))!, "." + Path.GetFileNameWithoutExtension(clipPath) + ".stems.mka");

    public static string Sanitize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return "Desktop";
        foreach (var c in Path.GetInvalidFileNameChars()) label = label.Replace(c, ' ');
        return label.Replace('_', ' ').Trim();
    }

    public static (string Label, DateTime? When) Parse(string filePath)
    {
        var m = Pattern().Match(Path.GetFileNameWithoutExtension(filePath));
        if (!m.Success) return ("Other", null);
        DateTime.TryParseExact(m.Groups["stamp"].Value, "yyyy-MM-dd_HH-mm-ss", null, System.Globalization.DateTimeStyles.None, out var when);
        return (m.Groups["label"].Value, when == default ? null : when);
    }

    /// <summary>Turns a process name into something readable, e.g. VALORANT-Win64-Shipping becomes VALORANT.</summary>
    public static string FriendlyName(string processName)
    {
        foreach (var junk in new[] { "-Win64-Shipping", "-Win64", "_x64", "-Shipping" })
            processName = processName.Replace(junk, "", StringComparison.OrdinalIgnoreCase);
        return processName.Trim();
    }
}

public static class ForegroundApp
{
    private static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "BufferedRecorder", "BufferedUI", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost",
        "LockApp", "TextInputHost", "ApplicationFrameHost", "dwm", "Taskmgr",
    };

    /// <summary>The friendly name of the app in front right now, or null if it is shell or Buffered itself.</summary>
    public static string? Current()
    {
        try
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            return Ignore.Contains(p.ProcessName) ? null : ClipNames.FriendlyName(p.ProcessName);
        }
        catch { return null; }
    }
}
