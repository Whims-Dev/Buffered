using System.IO;
using System.Text.Json;

namespace Buffered.UI;

/// <summary>What Buffered remembers about one clip besides the video itself.</summary>
public sealed class ClipMeta
{
    public bool Favorite { get; set; }
    public string? Name { get; set; }
    public string? Note { get; set; }
    public bool IsEmpty => !Favorite && string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Note);
}

/// <summary>
/// Favorites, names and notes, kept in a small hidden file in the clips folder (so they travel with the folder).
/// Clips are keyed by their path relative to that folder. Folders are real folders, one level deep.
/// </summary>
public static class ClipLibrary
{
    private const string FileName = ".buffered-library.json";
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static string _root = "";
    private static Dictionary<string, ClipMeta> _clips = new(StringComparer.OrdinalIgnoreCase);

    public static string Root => _root;

    /// <summary>Points the library at a clips folder and loads its file.</summary>
    public static void Use(string root)
    {
        lock (Gate)
        {
            if (string.Equals(_root, root, StringComparison.OrdinalIgnoreCase)) return;
            _root = root;
            _clips = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                var f = Path.Combine(root, FileName);
                if (File.Exists(f))
                {
                    var data = JsonSerializer.Deserialize<Dictionary<string, ClipMeta>>(File.ReadAllText(f), Json);
                    if (data is not null) _clips = new(data, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch { /* a damaged file just means starting fresh */ }
        }
    }

    /// <summary>The path relative to the clips folder, with forward slashes ("Funny/clip.mp4").</summary>
    public static string KeyOf(string path)
    {
        var rel = Path.GetRelativePath(_root, path);
        return rel.Replace('\\', '/');
    }

    /// <summary>The folder a clip is in, or "" for the top level.</summary>
    public static string FolderOf(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        var rel = Path.GetRelativePath(_root, dir);
        return rel == "." ? "" : rel.Split('\\', '/')[0];
    }

    public static ClipMeta Get(string path)
    {
        lock (Gate) return _clips.TryGetValue(KeyOf(path), out var m) ? m : new ClipMeta();
    }

    public static void Update(string path, Action<ClipMeta> change)
    {
        lock (Gate)
        {
            var key = KeyOf(path);
            if (!_clips.TryGetValue(key, out var m)) m = new ClipMeta();
            change(m);
            if (m.IsEmpty) _clips.Remove(key); else _clips[key] = m;
            Save();
        }
    }

    /// <summary>Carries a clip's details over when its file moves or is renamed.</summary>
    public static void Moved(string oldPath, string newPath)
    {
        lock (Gate)
        {
            var from = KeyOf(oldPath);
            if (!_clips.Remove(from, out var m)) return;
            _clips[KeyOf(newPath)] = m;
            Save();
        }
    }

    /// <summary>Keeps the details of the clips in a folder that was renamed.</summary>
    public static void FolderRenamed(string oldName, string newName)
    {
        lock (Gate)
        {
            var prefix = oldName + "/";
            foreach (var key in _clips.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var m = _clips[key];
                _clips.Remove(key);
                _clips[newName + "/" + key[prefix.Length..]] = m;
            }
            Save();
        }
    }

    public static void Forget(string path)
    {
        lock (Gate) { if (_clips.Remove(KeyOf(path))) Save(); }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(_root);
            var f = Path.Combine(_root, FileName);
            var tmp = f + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_clips, Json));
            File.Move(tmp, f, overwrite: true);
            File.SetAttributes(f, File.GetAttributes(f) | FileAttributes.Hidden);
        }
        catch { /* not being able to save a favorite should never break the app */ }
    }

    // ---- folders ------------------------------------------------------------

    /// <summary>The visible folders inside the clips folder.</summary>
    public static List<string> Folders()
    {
        try
        {
            if (!Directory.Exists(_root)) return new();
            return Directory.EnumerateDirectories(_root)
                .Select(d => Path.GetFileName(d)!)
                .Where(n => !n.StartsWith('.'))
                .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch { return new(); }
    }

    public static string FolderPath(string folder) => folder.Length == 0 ? _root : Path.Combine(_root, folder);

    /// <summary>Strips characters Windows doesn't allow in a folder name.</summary>
    public static string CleanFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, ' ');
        return name.Trim().TrimEnd('.');
    }

    /// <summary>Moves a clip (and its hidden companion file) into a folder; "" means the top level. Returns the new path.</summary>
    public static string MoveClip(string path, string folder)
    {
        var destDir = FolderPath(folder);
        Directory.CreateDirectory(destDir);
        if (string.Equals(Path.GetFullPath(Path.GetDirectoryName(path)!), Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase)) return path;

        var name = Path.GetFileNameWithoutExtension(path);
        var dest = Path.Combine(destDir, name + ".mp4");
        for (int n = 2; File.Exists(dest); n++) dest = Path.Combine(destDir, $"{name} ({n}).mp4");

        var stems = ClipNames.StemsPath(path);
        File.Move(path, dest);
        if (File.Exists(stems))
        {
            try { File.Move(stems, ClipNames.StemsPath(dest)); } catch { /* the clip still plays without it */ }
        }
        Moved(path, dest);
        return dest;
    }
}
