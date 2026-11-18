using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace Buffered.UI;

/// <summary>Choosing the clips folder and moving a library to a new one.</summary>
public partial class MainWindow
{
    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where clips are saved", InitialDirectory = _settings.OutDir };
        if (dlg.ShowDialog(this) != true) return;

        string oldDir = _settings.OutDir, newDir = dlg.FolderName;
        if (!SamePath(oldDir, newDir) && Directory.Exists(oldDir) && !IsInside(newDir, oldDir) && !IsInside(oldDir, newDir))
        {
            ClipLibrary.Use(oldDir);
            var items = LibraryItems(oldDir);
            if (items.Count > 0)
            {
                long bytes = items.Sum(SizeOf);
                bool move = await Dialogs.ConfirmAsync("Move your clips too?",
                    $"{_allClips.Count} clips ({FormatSize(bytes)}) are in the current folder. Move them, with their folders, favorites and notes, to the new one?\n\nIf you leave them, they stay where they are and the library will show only new clips.",
                    "Move clips", false, "Leave them");
                if (move)
                {
                    _watcher?.Dispose(); _watcher = null;
                    ApplyText.Text = "Moving clips…";
                    string? error = await Task.Run(() => MoveLibrary(items, oldDir, newDir));
                    if (error is not null) await Dialogs.MessageAsync("Couldn't move every clip", error + "\n\nWhatever couldn't be moved is still in the old folder.");
                    else Dialogs.Toast("Clips moved", newDir);
                }
            }
        }

        _settings.OutDir = newDir;
        FolderText.Text = _settings.OutDir;
        WatchFolder();
        LoadClips();
        ApplyText.Text = "Applying…";
        _applyTimer.Stop(); _applyTimer.Start();
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="child"/> is the same as, or lives inside, <paramref name="parent"/>.</summary>
    private static bool IsInside(string child, string parent) =>
        (Path.GetFullPath(child).TrimEnd('\\') + "\\").StartsWith(Path.GetFullPath(parent).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static string FormatSize(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0} MB";

    /// <summary>Everything Buffered keeps in the clips folder: clips, hidden companion files, the library file and the clip folders.</summary>
    private static List<string> LibraryItems(string dir)
    {
        var list = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var n = Path.GetFileName(f);
            if (n.StartsWith('.') || n.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) list.Add(f);
        }
        foreach (var name in ClipLibrary.Folders())
        {
            var p = ClipLibrary.FolderPath(name);
            if (Directory.Exists(p)) list.Add(p);
        }
        return list;
    }

    private static long SizeOf(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            return Directory.EnumerateFiles(path, "*", System.IO.SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    /// <summary>Moves the items into the new folder (a rename on the same drive, copy then delete across drives). Never overwrites. Returns an error text, or null.</summary>
    private static string? MoveLibrary(List<string> items, string oldDir, string newDir)
    {
        string? error = null;
        try { Directory.CreateDirectory(newDir); } catch (Exception ex) { return ex.Message; }

        void MoveFile(string from, string to)
        {
            if (File.Exists(to)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            try { File.Move(from, to); }
            catch (IOException) when (File.Exists(from) && !File.Exists(to))
            {
                File.Copy(from, to);                       // across drives: copy, then remove the original
                File.Delete(from);
            }
        }

        foreach (var item in items)
        {
            try
            {
                string rel = Path.GetRelativePath(oldDir, item);
                if (File.Exists(item)) MoveFile(item, Path.Combine(newDir, rel));
                else
                {
                    foreach (var f in Directory.EnumerateFiles(item, "*", System.IO.SearchOption.AllDirectories).ToList())
                        MoveFile(f, Path.Combine(newDir, Path.GetRelativePath(oldDir, f)));
                    try { if (!Directory.EnumerateFileSystemEntries(item).Any()) Directory.Delete(item); } catch { }
                    Directory.CreateDirectory(Path.Combine(newDir, rel));   // keep empty folders too
                }
            }
            catch (Exception ex) { error ??= $"{Path.GetFileName(item)}: {ex.Message}"; }
        }
        return error;
    }
}
