using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Buffered.UI;

/// <summary>
/// Talks to Discord's webhook address, and only when the user asks: checking a webhook when it is added, and posting a clip.
/// The webhook link is a secret, so it is never written to a log or into a message shown to the user.
/// </summary>
internal static class DiscordClient
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Buffered/0.1");   // Discord turns away requests with no name
        return c;
    }

    private static readonly Regex Pattern = new(@"^https://(?:www\.)?(?:discord|discordapp)\.com/api(?:/v\d+)?/webhooks/(\d+)/([\w-]+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsWebhookUrl(string url) => Pattern.IsMatch(url.Trim());

    /// <summary>The webhook's own id (digits), or null if the text isn't a webhook link.</summary>
    public static string? IdOf(string url) => Pattern.Match(url.Trim()) is { Success: true } m ? m.Groups[1].Value : null;

    public sealed record Info(string Name, string GuildId, string ChannelId);

    /// <summary>Asks Discord about a webhook (its name and where it lives). Posts nothing.</summary>
    public static async Task<(Info? Info, string Error)> GetInfoAsync(string url)
    {
        try
        {
            using var res = await Http.GetAsync(url.Trim());
            if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                return (null, "Discord doesn't know this webhook. It may have been deleted.");
            if (!res.IsSuccessStatusCode) return (null, $"Discord answered with an error ({(int)res.StatusCode}).");
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return (new Info(S("name"), S("guild_id"), S("channel_id")), "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "Couldn't reach Discord. Check your connection and try again.");
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public sealed record SendResult(bool Ok, string Error, string? MessageUrl, string? AppUrl);

    /// <summary>Posts a clip to a webhook as a message with an embed (name, note, details) and the video attached.</summary>
    public static async Task<SendResult> SendAsync(WebhookTarget hook, string filePath, string fileName, string title, string? note,
        IReadOnlyList<(string Name, string Value)> details, DateTime when, IProgress<double>? progress)
    {
        try
        {
            var embed = new Dictionary<string, object?>
            {
                ["title"] = Cut(title, 250),
                ["color"] = ColorValue(hook.Color),
                ["timestamp"] = when.ToUniversalTime().ToString("o"),
                ["footer"] = new { text = "Buffered" },
                ["fields"] = details.Where(d => !string.IsNullOrWhiteSpace(d.Value)).Select(d => new { name = d.Name, value = Cut(d.Value, 1000), inline = true }).ToArray(),
            };
            if (!string.IsNullOrWhiteSpace(note)) embed["description"] = Cut(note!, 2000);
            string payload = JsonSerializer.Serialize(new { username = "Buffered", embeds = new[] { embed }, attachments = new[] { new { id = 0, filename = fileName } } });

            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
            form.Add(new FileContent(filePath, progress), "files[0]", fileName);

            using var res = await Http.PostAsync(hook.Url.Trim().TrimEnd('/') + "?wait=true", form);
            string body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) return new SendResult(false, Explain(res.StatusCode, body), null, null);

            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string id = S("id"), channel = S("channel_id").Length > 0 ? S("channel_id") : hook.ChannelId, guild = S("guild_id").Length > 0 ? S("guild_id") : hook.GuildId;
            if (id.Length == 0 || channel.Length == 0 || guild.Length == 0) return new SendResult(true, "", null, null);
            string path = $"channels/{guild}/{channel}/{id}";
            return new SendResult(true, "", "https://discord.com/" + path, "discord://-/" + path);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new SendResult(false, "Couldn't reach Discord. Check your connection and try again.", null, null);
        }
        catch (Exception ex) { return new SendResult(false, ex.Message, null, null); }
    }

    private static string Explain(HttpStatusCode code, string body)
    {
        switch ((int)code)
        {
            case 413: return "Discord says the file is too big for this server. Try a smaller size.";
            case 404: case 401: return "This webhook no longer exists. Remove it and add a new one.";
            case 429:
                try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("retry_after", out var s)) return $"Discord asked to slow down. Try again in {Math.Ceiling(s.GetDouble())} seconds."; }
                catch { }
                return "Discord asked to slow down. Try again in a moment.";
            default:
                try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } text) return $"Discord said: {text}"; }
                catch { }
                return $"Discord answered with an error ({(int)code}).";
        }
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static int ColorValue(string hex)
    {
        hex = hex.TrimStart('#');
        return hex.Length == 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0x7FA6FF;
    }

    /// <summary>A file sent in pieces, reporting how far along it is.</summary>
    private sealed class FileContent : HttpContent
    {
        private readonly string _path;
        private readonly IProgress<double>? _progress;

        public FileContent(string path, IProgress<double>? progress)
        {
            _path = path; _progress = progress;
            Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await using var f = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long total = f.Length, sent = 0;
            int n;
            while ((n = await f.ReadAsync(buffer)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, n));
                sent += n;
                _progress?.Report(total > 0 ? Math.Min(1, (double)sent / total) : 1);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            try { length = new FileInfo(_path).Length; return true; }
            catch { length = 0; return false; }
        }
    }
}
