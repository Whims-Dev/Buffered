using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Buffered.UI;

/// <summary>
/// Updates, started by the user: Check for updates, then Download, then Restart to update. Nothing is checked, downloaded or installed
/// by itself. The download is checked against the hash GitHub publishes for the file before it can be installed.
/// </summary>
public partial class MainWindow
{
    private enum UpdState { Idle, Checking, Current, Available, Downloading, Ready, Installing, Error }

    private UpdState _upd = UpdState.Idle;
    private string _updTag = "", _updNotes = "", _updPage = "", _updUrl = "", _updHash = "", _updError = "", _updChecked = "";
    private long _updSize;
    private string? _updZip;
    private CancellationTokenSource? _updCts;
    private double _updPercent;
    private string _updProgress = "";

    private static string UpdateDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Buffered", "update");

    private void SetUpd(UpdState s) { _upd = s; RenderUpdate(); }

    private void RenderUpdate()
    {
        UpdateTitle.Text = $"Buffered {AppInfo.Version}";
        bool isNew = _upd is UpdState.Available or UpdState.Downloading;
        UpdatePill.Visibility = _upd is UpdState.Available or UpdState.Ready ? Visibility.Visible : Visibility.Collapsed;
        UpdatePillText.Text = _upd == UpdState.Ready ? "Verified" : "New";
        UpdateNotes.Visibility = _upd == UpdState.Available && _updNotes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateNotesText.Text = _updNotes;
        UpdateBar.Visibility = _upd is UpdState.Downloading or UpdState.Checking or UpdState.Installing ? Visibility.Visible : Visibility.Collapsed;
        UpdateBar.IsIndeterminate = _upd is UpdState.Checking or UpdState.Installing;
        UpdateBar.Value = _updPercent;
        UpdateSecondary.Visibility = Visibility.Collapsed;

        string version = _updTag.TrimStart('v', 'V');
        switch (_upd)
        {
            case UpdState.Idle:
                UpdateText.Text = "You're on this version. Buffered only checks GitHub when you ask, and never installs anything by itself.";
                SetUpdPrimary("Check for updates", true, false);
                break;
            case UpdState.Checking:
                UpdateText.Text = "Checking GitHub…";
                SetUpdPrimary("Check for updates", false, false);
                break;
            case UpdState.Current:
                UpdateText.Text = $"You have the latest version. Last checked {_updChecked}.";
                SetUpdPrimary("Check for updates", true, false);
                break;
            case UpdState.Available:
                UpdateText.Text = $"Version {version} is available ({FormatMb(_updSize)} download).";
                SetUpdPrimary("Download", true, true);
                SetUpdSecondary("View on GitHub");
                break;
            case UpdState.Downloading:
                UpdateText.Text = $"Downloading {version}… {_updProgress}";
                SetUpdPrimary("Cancel", true, false);
                break;
            case UpdState.Ready:
                UpdateText.Text = $"Version {version} is downloaded and checked. Buffered will close, update and open again. Your recorder restarts too, so the replay buffer starts over.";
                SetUpdPrimary("Restart to update", true, true);
                SetUpdSecondary("Discard");
                break;
            case UpdState.Installing:
                UpdateText.Text = $"Installing {version}… Buffered will open again in a moment.";
                SetUpdPrimary("Restart to update", false, true);
                break;
            case UpdState.Error:
                UpdateText.Text = _updError;
                SetUpdPrimary("Try again", true, false);
                if (_updPage.Length > 0) SetUpdSecondary("View on GitHub");
                break;
        }
        _ = isNew;
    }

    private static string FormatMb(long bytes) => bytes > 0 ? $"{Math.Max(1, bytes / 1048576)} MB" : "unknown size";

    private void SetUpdPrimary(string text, bool enabled, bool accent)
    {
        UpdatePrimaryText.Text = text;
        UpdatePrimary.IsEnabled = enabled;
        if (accent) UpdatePrimary.Style = (Style)FindResource("PrimaryButton"); else UpdatePrimary.ClearValue(StyleProperty);
    }

    private void SetUpdSecondary(string text) { UpdateSecondaryText.Text = text; UpdateSecondary.Visibility = Visibility.Visible; }

    private async void UpdatePrimary_Click(object sender, RoutedEventArgs e)
    {
        switch (_upd)
        {
            case UpdState.Idle: case UpdState.Current: case UpdState.Error: await CheckForUpdateAsync(); break;
            case UpdState.Available: await DownloadUpdateAsync(); break;
            case UpdState.Downloading: _updCts?.Cancel(); break;
            case UpdState.Ready: await InstallUpdateAsync(); break;
        }
    }

    private void UpdateSecondary_Click(object sender, RoutedEventArgs e)
    {
        if (_upd == UpdState.Ready)
        {
            try { if (_updZip is not null) File.Delete(_updZip); } catch { }
            _updZip = null;
            SetUpd(UpdState.Available);
        }
        else if (_updPage.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo(_updPage) { UseShellExecute = true });
    }

    // ---- how the last update ended -----------------------------------------------

    /// <summary>
    /// After an update the installer script leaves a note saying whether it worked. Shown once, the next time the window opens:
    /// a short notice if it worked, and the reason (with where the log is) if it didn't.
    /// </summary>
    private async void ShowUpdateResult()
    {
        try
        {
            string file = Path.Combine(UpdateDir, "result.txt");
            if (!File.Exists(file)) return;
            string text = (await File.ReadAllTextAsync(file)).Trim();
            File.Delete(file);
            int bar = text.IndexOf('|');
            string kind = bar >= 0 ? text[..bar] : text, detail = bar >= 0 ? text[(bar + 1)..] : "";
            if (detail.Length > 300) detail = detail[..300] + "…";
            if (kind == "ok") Dialogs.Toast($"Updated to version {(detail.Length > 0 ? detail : AppInfo.Version)}", "Buffered was updated and reopened.");
            else
                await Dialogs.MessageAsync("The last update didn't finish",
                    "Buffered tried to update itself, but it couldn't: " + (detail.Length > 0 ? detail : "unknown reason") +
                    "\n\nYour recordings and clips aren't affected. You can try again from Settings, then Updates, or download the new version from GitHub and unzip it over this folder." +
                    "\n\nDetails are in " + Path.Combine(UpdateDir, "update.log"));
        }
        catch { /* a note that can't be read isn't worth bothering anyone about */ }
    }

    // ---- check -----------------------------------------------------------------

    private async Task CheckForUpdateAsync()
    {
        SetUpd(UpdState.Checking);
        try
        {
            using var http = NewHttp(TimeSpan.FromSeconds(12));
            using var resp = await http.GetAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest");
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) { _updChecked = DateTime.Now.ToString("h:mm tt"); SetUpd(UpdState.Current); return; }
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            _updTag = root.GetProperty("tag_name").GetString() ?? "";
            _updPage = root.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : "";
            _updNotes = Summarize(root.TryGetProperty("body", out var b) ? b.GetString() : null);
            _updUrl = _updHash = ""; _updSize = 0;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    _updUrl = a.GetProperty("browser_download_url").GetString() ?? "";
                    _updSize = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                    string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) _updHash = digest[7..].Trim().ToLowerInvariant();
                    break;
                }
            _updChecked = DateTime.Now.ToString("h:mm tt");
            bool newer = Version.TryParse(_updTag.TrimStart('v', 'V'), out var latest) && Version.TryParse(AppInfo.Version, out var mine) && latest > mine;
            SetUpd(newer ? UpdState.Available : UpdState.Current);
        }
        catch (Exception ex)
        {
            _updError = ex is HttpRequestException or TaskCanceledException
                ? "Couldn't reach GitHub. Check your internet connection, then try again."
                : "Couldn't check for updates: " + ex.Message;
            _updPage = "";
            SetUpd(UpdState.Error);
        }
    }

    private static HttpClient NewHttp(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Buffered-update-check");   // GitHub requires a user agent; this says nothing about you
        return http;
    }

    /// <summary>The first few lines of a release's notes, without the markdown marks.</summary>
    private static string Summarize(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var lines = body.Replace("\r", "").Split('\n')
            .Select(l => l.Trim().TrimStart('#', '*', '-', ' ').Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase))
            .Take(7);
        var text = string.Join("\n", lines.Select(l => "•  " + l));
        return text.Length > 700 ? text[..700] + "…" : text;
    }

    // ---- download --------------------------------------------------------------

    private async Task DownloadUpdateAsync()
    {
        if (_updUrl.Length == 0 || !_updUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            _updError = "This release has no download Buffered can use. Use View on GitHub instead.";
            SetUpd(UpdState.Error); return;
        }
        if (_updHash.Length == 0)
        {
            _updError = "GitHub didn't give a way to check this download, so Buffered won't install it. Use View on GitHub to download it yourself.";
            SetUpd(UpdState.Error); return;
        }

        _updCts = new CancellationTokenSource();
        _updPercent = 0; _updProgress = "";
        SetUpd(UpdState.Downloading);
        string part = "";
        try
        {
            Directory.CreateDirectory(UpdateDir);
            foreach (var old in Directory.GetFiles(UpdateDir, "*.zip*")) { try { File.Delete(old); } catch { } }
            part = Path.Combine(UpdateDir, "Buffered-win-x64.zip.part");

            using var http = NewHttp(TimeSpan.FromMinutes(30));
            using var resp = await http.GetAsync(_updUrl, HttpCompletionOption.ResponseHeadersRead, _updCts.Token);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? _updSize;

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var net = await resp.Content.ReadAsStreamAsync(_updCts.Token))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buf = new byte[81920];
                long done = 0; var clock = Stopwatch.StartNew(); long lastTick = 0;
                int n;
                while ((n = await net.ReadAsync(buf, _updCts.Token)) > 0)
                {
                    await file.WriteAsync(buf.AsMemory(0, n), _updCts.Token);
                    sha.AppendData(buf, 0, n);
                    done += n;
                    if (clock.ElapsedMilliseconds - lastTick > 150)
                    {
                        lastTick = clock.ElapsedMilliseconds;
                        _updPercent = total > 0 ? done * 100.0 / total : 0;
                        double rate = done / Math.Max(0.5, clock.Elapsed.TotalSeconds);
                        string left = total > 0 && rate > 0 ? $" · about {Math.Max(1, (int)((total - done) / rate))} s left" : "";
                        _updProgress = $"{done / 1048576} MB of {FormatMb(total)}{left}";
                        RenderUpdate();
                    }
                }
            }

            string actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (!actual.Equals(_updHash, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(part); } catch { }
                _updError = "The download didn't match what GitHub published, so it was thrown away. Try again.";
                SetUpd(UpdState.Error); return;
            }
            _updZip = Path.Combine(UpdateDir, "Buffered-win-x64.zip");
            File.Move(part, _updZip, true);
            _updPercent = 100;
            SetUpd(UpdState.Ready);
        }
        catch (OperationCanceledException)
        {
            try { if (part.Length > 0) File.Delete(part); } catch { }
            SetUpd(UpdState.Available);
        }
        catch (Exception ex)
        {
            try { if (part.Length > 0) File.Delete(part); } catch { }
            _updError = ex is HttpRequestException or IOException ? "The download stopped: " + ex.Message : "Couldn't download: " + ex.Message;
            SetUpd(UpdState.Error);
        }
        finally { _updCts?.Dispose(); _updCts = null; }
    }

    // ---- install ---------------------------------------------------------------

    private async Task InstallUpdateAsync()
    {
        if (_updZip is null || !File.Exists(_updZip)) { SetUpd(UpdState.Available); return; }

        string dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        try
        {
            string probe = Path.Combine(dir, ".update-write-test");
            File.WriteAllText(probe, "x"); File.Delete(probe);
        }
        catch
        {
            _updError = "Buffered can't change the files in its own folder (it may be in a protected place such as Program Files). Extract the download somewhere you own, or run Buffered from such a folder.";
            SetUpd(UpdState.Error); return;
        }

        bool ok = await Dialogs.ConfirmAsync("Restart to update?",
            $"Buffered will close, install version {_updTag.TrimStart('v', 'V')}, and open again.\n\nYour recorder will restart too, so the replay buffer starts over and anything not saved yet is lost. Save any clip you want first.",
            "Restart and update", false, "Not now");
        if (!ok) return;

        bool recorderRunning = Process.GetProcessesByName("BufferedRecorder").Length > 0;
        SetUpd(UpdState.Installing);
        try
        {
            string script = Path.Combine(UpdateDir, "apply.ps1");
            File.WriteAllText(script, ApplyScript, new UTF8Encoding(true));
            var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                                      "-InstallDir", dir, "-Zip", _updZip, "-UiPid", Environment.ProcessId.ToString(), "-RestartRecorder", recorderRunning ? "1" : "0", "-Version", _updTag.TrimStart('v', 'V') })
                psi.ArgumentList.Add(a);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _updError = "Couldn't start the installer: " + ex.Message;
            SetUpd(UpdState.Error); return;
        }
        await Task.Delay(400);
        Application.Current.Shutdown();
    }

    /// <summary>Runs after Buffered has closed: waits, stops anything left, unpacks the new files over the old ones, and opens Buffered again.</summary>
    private const string ApplyScript = @"param([string]$InstallDir, [string]$Zip, [int]$UiPid, [int]$RestartRecorder, [string]$Version = '')
$ErrorActionPreference = 'Stop'
$work = Split-Path $Zip
$log = Join-Path $work 'update.log'
function Log($m) { Add-Content -Path $log -Value ('{0} {1}' -f (Get-Date -Format s), $m) }
$failed = $false
try {
    Log 'waiting for Buffered to close'
    try { Wait-Process -Id $UiPid -Timeout 30 -ErrorAction SilentlyContinue } catch { }
    Get-Process BufferedUI -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process BufferedRecorder -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    $tmp = Join-Path $work 'extract'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    Expand-Archive -Path $Zip -DestinationPath $tmp -Force
    $src = $tmp
    $items = @(Get-ChildItem $tmp)
    if ($items.Count -eq 1 -and $items[0].PSIsContainer) { $src = $items[0].FullName }
    # Replace the files one by one, keeping a copy of each old file first. If any of them can't be replaced (a program locks it, a virus
    # scanner holds it), everything already replaced is put back, so the folder never ends up half old and half new.
    $backup = Join-Path $work 'backup'
    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force }
    $cut = $src.TrimEnd([char]92).Length
    $restoreProblem = $false
    $touched = New-Object System.Collections.Generic.List[string]
    $added = New-Object System.Collections.Generic.HashSet[string]
    try {
        foreach ($f in (Get-ChildItem $src -Recurse -File)) {
            $rel = $f.FullName.Substring($cut).TrimStart([char]92)
            $dest = Join-Path $InstallDir $rel
            if (Test-Path $dest) {
                $b = Join-Path $backup $rel
                New-Item -ItemType Directory (Split-Path $b) -Force | Out-Null
                Copy-Item $dest $b -Force
            } else { [void]$added.Add($rel) }
            New-Item -ItemType Directory (Split-Path $dest) -Force | Out-Null
            $touched.Add($rel)
            $ok = $false
            for ($i = 0; $i -lt 5 -and -not $ok; $i++) {
                try { Copy-Item $f.FullName $dest -Force; $ok = $true }
                catch { Log ('copy attempt failed for ' + $rel + ': ' + $_); Start-Sleep -Seconds 2 }
            }
            if (-not $ok) { throw ('could not replace ' + $rel) }
        }
    } catch {
        Log ('replacing files failed, putting the old ones back: ' + $_)
        foreach ($rel in $touched) {
            $dest = Join-Path $InstallDir $rel
            $b = Join-Path $backup $rel
            if (Test-Path $b) { try { Copy-Item $b $dest -Force } catch { $restoreProblem = $true; Log ('could not restore ' + $rel + ': ' + $_) } }
            elseif ($added.Contains($rel)) { Remove-Item $dest -Force -ErrorAction SilentlyContinue }
        }
        # the backup is kept only if something could not be put back (so it can still be recovered by hand)
        if (-not $restoreProblem) { Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue }
        throw ('the new files could not be put in place (' + $_.Exception.Message + '), so the previous version was put back')
    }
    Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue
    Log 'files replaced'
    Set-Content -Path (Join-Path $work 'result.txt') -Value ('ok|' + $Version) -Encoding UTF8
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $Zip -Force -ErrorAction SilentlyContinue
} catch { Log ('failed: ' + $_); $failed = $true; Set-Content -Path (Join-Path $work 'result.txt') -Value ('failed|' + $_.Exception.Message) -Encoding UTF8 }
if ($RestartRecorder -eq 1) { try { Start-Process (Join-Path $InstallDir 'BufferedRecorder.exe') -ArgumentList '--background' } catch { Log ('recorder did not start: ' + $_) } }
try { Start-Process (Join-Path $InstallDir 'BufferedUI.exe') } catch { Log ('ui did not start: ' + $_) }
";
}
