using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text.Json;
using VideoDownloader.Data;

namespace VideoDownloader.Controllers;

[ApiController]
[Route("api/media")]
[Authorize]
public class MediaController : ControllerBase
{
    private readonly AppDbContext _db;

    private const string AntiBlockArgs =
        "--user-agent \"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36\" " +
        "--add-header \"Accept-Language:en-US,en;q=0.9\" " +
        "--add-header \"Sec-Fetch-Mode:navigate\" " +
        "--retries 3 --sleep-interval 1";

    public MediaController(AppDbContext db) { _db = db; }

    // ─── Analyze ──────────────────────────────────────────────────────────────
    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze([FromBody] MediaRequestDto dto)
    {
        try
        {
            await TrackRequest();
            var platform = DetectPlatform(dto.Url);

            if (platform == "spotify")  return await AnalyzeSpotify(dto.Url);
            if (platform == "unknown")  return BadRequest(new { message = "Unsupported URL" });

            if (platform is "soundcloud" or "bandcamp" or "audiomack" or "mixcloud" or "deezer")
                return await AnalyzeAudioPlatform(dto.Url, platform);

            var isPlaylist = IsPlaylist(dto.Url, platform);

            if (isPlaylist)
            {
                var result  = await RunYtDlp(
                    $"--flat-playlist -J {AntiBlockArgs} {GetPlatformArgs(platform)} \"{dto.Url}\"");
                var json    = JsonDocument.Parse(result).RootElement;
                var entries = json.GetProperty("entries").EnumerateArray().ToList();

                return Ok(new
                {
                    type      = "playlist",
                    platform,
                    title     = json.TryGetProperty("title", out var pt) ? pt.GetString() : "Playlist",
                    itemCount = entries.Count,
                    items     = entries.Select((e, i) => new
                    {
                        index     = i,
                        id        = e.TryGetProperty("id",        out var id) ? id.GetString()   : null,
                        title     = e.TryGetProperty("title",     out var t)  ? t.GetString()    : $"Video {i + 1}",
                        duration  = e.TryGetProperty("duration",  out var d)
                                    && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0,
                        thumbnail = e.TryGetProperty("thumbnail", out var tn) ? tn.GetString()   : null,
                        url       = e.TryGetProperty("webpage_url", out var wu) ? wu.GetString()
                                  : BuildVideoUrl(platform, e.TryGetProperty("id", out var vid) ? vid.GetString() : ""),
                    }),
                    availableQualities = GetQualities(platform)
                });
            }
            else
            {
                var result = await RunYtDlp(
                    $"-J --no-playlist {AntiBlockArgs} {GetPlatformArgs(platform)} \"{dto.Url}\"");
                var json   = JsonDocument.Parse(result).RootElement;

                var formats = json.TryGetProperty("formats", out var fmtArr)
                    ? fmtArr.EnumerateArray()
                        .Where(f => f.TryGetProperty("height", out var h)
                                    && h.ValueKind == JsonValueKind.Number)
                        .Select(f => f.GetProperty("height").GetInt32())
                        .Distinct()
                        .OrderByDescending(h => h)
                        .ToList()
                    : new List<int>();

                var hasSubs     = json.TryGetProperty("subtitles",          out var subs)
                                  && subs.ValueKind == JsonValueKind.Object
                                  && subs.EnumerateObject().Any();
                var hasAutoSubs = json.TryGetProperty("automatic_captions", out var autoCaps)
                                  && autoCaps.ValueKind == JsonValueKind.Object
                                  && autoCaps.EnumerateObject().Any();

                var title     = json.TryGetProperty("title",     out var jt) ? jt.GetString()  : "Unknown";
                var duration  = json.TryGetProperty("duration",  out var jd)
                                && jd.ValueKind == JsonValueKind.Number ? jd.GetDouble() : 0;
                var thumbnail = json.TryGetProperty("thumbnail", out var jtn) ? jtn.GetString() : null;
                var uploader  = json.TryGetProperty("uploader",  out var ju)  ? ju.GetString()  :
                                json.TryGetProperty("channel",   out var jc)  ? jc.GetString()  : null;

                return Ok(new
                {
                    type      = "video",
                    platform,
                    title,
                    duration,
                    thumbnail,
                    uploader,
                    subtitles = new
                    {
                        available        = hasSubs || hasAutoSubs,
                        hasManual        = hasSubs,
                        hasAutoGenerated = hasAutoSubs,
                        languages        = hasSubs
                                           ? subs.EnumerateObject().Select(s => s.Name).Take(10)
                                           : hasAutoSubs
                                           ? autoCaps.EnumerateObject().Select(s => s.Name).Take(10)
                                           : Enumerable.Empty<string>()
                    },
                    availableQualities = GetQualities(platform, formats)
                });
            }
        }
        catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
    }

    // ─── Download ─────────────────────────────────────────────────────────────
    [HttpPost("download")]
    public async Task<IActionResult> Download([FromBody] MediaDownloadDto dto)
    {
        try
        {
            await TrackRequest();
            var platform = DetectPlatform(dto.Url);
            if (platform == "spotify") return await DownloadSpotify(dto.Url);
            if (platform == "unknown") return BadRequest(new { message = "Unsupported URL" });
            return await DownloadSingleVideo(dto.Url, dto.Quality, platform);
        }
        catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost("playlist/download")]
    public async Task<IActionResult> PlaylistDownload([FromBody] MediaDownloadDto dto)
    {
        try
        {
            await TrackRequest();
            var platform = DetectPlatform(dto.Url);
            if (platform == "unknown") return BadRequest(new { message = "Unsupported URL" });
            return await DownloadSingleVideo(dto.Url, dto.Quality, platform);
        }
        catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
    }

    // ─── Subtitles ────────────────────────────────────────────────────────────
    [HttpPost("subtitles")]
    public async Task<IActionResult> GetSubtitles([FromBody] SubtitleRequestDto dto)
    {
        try
        {
            await TrackRequest();
            var lang    = dto.Language ?? "en";
            var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            await RunYtDlp($"--write-subs --sub-langs \"{lang}\" --skip-download --no-playlist " +
                           $"-o \"{tempDir}/sub\" \"{dto.Url}\"");

            var subFile = Directory.GetFiles(tempDir).FirstOrDefault();
            if (subFile == null)
            {
                await RunYtDlp($"--write-auto-subs --sub-langs \"{lang}\" --skip-download --no-playlist " +
                               $"-o \"{tempDir}/sub\" \"{dto.Url}\"");
                subFile = Directory.GetFiles(tempDir).FirstOrDefault();
            }

            if (subFile == null) return NotFound(new { message = $"No subtitles: {lang}" });

            var content = await System.IO.File.ReadAllTextAsync(subFile);
            var subExt  = Path.GetExtension(subFile).TrimStart('.');
            Directory.Delete(tempDir, true);

            return Ok(new { language = lang, format = subExt, content,
                            plainText = ExtractPlainText(content, subExt) });
        }
        catch (Exception ex) { return BadRequest(new { message = ex.Message }); }
    }

    // ─── Core download ────────────────────────────────────────────────────────
    private async Task<IActionResult> DownloadSingleVideo(string url, string quality, string platform)
    {
        var result = await DownloadSingleVideoAsBytes(url, quality, platform);
        return File(result.bytes, result.mimeType, result.fileName);
    }

    private async Task<(byte[] bytes, string mimeType, string fileName)> DownloadSingleVideoAsBytes(
        string url, string quality, string platform)
    {
        var isAudio = quality is "mp3" or "m4a";

        // Platforms that serve pre-muxed streams (no separate video+audio tracks)
        var isMuxedPlatform = platform is "tiktok" or "instagram" or "facebook"
                                       or "twitter" or "reddit" or "pinterest"
                                       or "snapchat" or "9gag" or "likee"
                                       or "triller" or "kwai" or "capcut";

        string format;
        if (isAudio)
        {
            format = quality == "mp3" ? "bestaudio[ext=mp3]/bestaudio" : "bestaudio[ext=m4a]/bestaudio";
        }
        else if (isMuxedPlatform)
        {
            format = quality switch
            {
                "2160" => "best[height<=2160][ext=mp4]/best[height<=2160]/best",
                "1080" => "best[height<=1080][ext=mp4]/best[height<=1080]/best",
                "720"  => "best[height<=720][ext=mp4]/best[height<=720]/best",
                "480"  => "best[height<=480][ext=mp4]/best[height<=480]/best",
                "360"  => "best[height<=360][ext=mp4]/best[height<=360]/best",
                _      => "best[ext=mp4]/best",
            };
        }
        else
        {
            format = quality switch
            {
                "2160" => "bestvideo[height<=2160][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=2160]+bestaudio/best[height<=2160]",
                "1080" => "bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=1080]+bestaudio/best[height<=1080]",
                "720"  => "bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=720]+bestaudio/best[height<=720]",
                "480"  => "bestvideo[height<=480][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=480]+bestaudio/best[height<=480]",
                "360"  => "bestvideo[height<=360][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<=360]+bestaudio/best[height<=360]",
                _      => "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best",
            };
        }

        var tempDir     = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var outTemplate = Path.Combine(tempDir, "output.%(ext)s");

        string ytArgs;
        if (isAudio)
        {
            ytArgs = $"-f \"{format}\" {AntiBlockArgs} {GetPlatformArgs(platform)} " +
                     $"-o \"{outTemplate}\" --no-playlist \"{url}\"";
        }
        else if (isMuxedPlatform)
        {
            ytArgs = $"-f \"{format}\" {AntiBlockArgs} {GetPlatformArgs(platform)} " +
                     $"-o \"{outTemplate}\" --no-playlist \"{url}\"";
        }
        else
        {
            ytArgs = $"-f \"{format}\" --merge-output-format mp4 {AntiBlockArgs} {GetPlatformArgs(platform)} " +
                     $"-N 4 -o \"{outTemplate}\" --no-playlist \"{url}\"";
        }

        await RunYtDlp(ytArgs);

        var outputFile = Directory.GetFiles(tempDir).FirstOrDefault()
            ?? throw new Exception("Download failed: no output file produced.");

        var actualExt = Path.GetExtension(outputFile).TrimStart('.');
        var fileName  = SanitizeFileName($"{quality}_{platform}.{actualExt}");
        var mimeType  = actualExt == "mp3" ? "audio/mpeg"
                      : actualExt == "m4a" ? "audio/mp4"
                      : "video/mp4";

        var bytes = await System.IO.File.ReadAllBytesAsync(outputFile);
        Directory.Delete(tempDir, true);

        return (bytes, mimeType, fileName);
    }

    // ─── Audio platform analyze ───────────────────────────────────────────────
    private async Task<IActionResult> AnalyzeAudioPlatform(string url, string platform)
    {
        var result = await RunYtDlp($"-J --no-playlist {AntiBlockArgs} {GetPlatformArgs(platform)} \"{url}\"");
        var json   = JsonDocument.Parse(result).RootElement;

        return Ok(new
        {
            type      = "audio",
            platform,
            title     = json.TryGetProperty("title",     out var t)  ? t.GetString()  : "Unknown",
            duration  = json.TryGetProperty("duration",  out var d)
                        && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0,
            thumbnail = json.TryGetProperty("thumbnail", out var tn) ? tn.GetString() : null,
            uploader  = json.TryGetProperty("uploader",  out var u)  ? u.GetString()  : null,
            subtitles = new { available = false, hasManual = false,
                              hasAutoGenerated = false, languages = Array.Empty<string>() },
            availableQualities = GetQualities(platform)
        });
    }

    // ─── Spotify ──────────────────────────────────────────────────────────────
    private async Task<IActionResult> AnalyzeSpotify(string url)
    {
        await RunProcess("spotdl", $"--print-errors save \"{url}\"");
        return Ok(new
        {
            type = "audio", platform = "spotify", url,
            availableQualities = new[]
            {
                new { label = "MP3 320kbps", value = "mp3", type = "audio", available = true },
                new { label = "M4A",         value = "m4a", type = "audio", available = true }
            }
        });
    }

    private async Task<IActionResult> DownloadSpotify(string url)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        await RunProcess("spotdl", $"download \"{url}\" --output \"{tempDir}\"");
        var file = Directory.GetFiles(tempDir).FirstOrDefault();
        if (file == null) return BadRequest(new { message = "Spotify download failed" });
        var bytes    = await System.IO.File.ReadAllBytesAsync(file);
        var fileName = Path.GetFileName(file);
        Directory.Delete(tempDir, true);
        return File(bytes, "audio/mpeg", fileName);
    }

    // ─── Platform detection ───────────────────────────────────────────────────
    private static string DetectPlatform(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "unknown";

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        if (host.StartsWith("m."))   host = host[2..];

        return host switch
        {
            "youtube.com" or "youtu.be" or "music.youtube.com"                 => "youtube",
            "tiktok.com" or "vm.tiktok.com" or "vt.tiktok.com"                => "tiktok",
            "instagram.com"                                                     => "instagram",
            "facebook.com" or "fb.watch" or "fb.com" or "web.facebook.com"    => "facebook",
            "twitter.com" or "x.com" or "t.co"                                 => "twitter",
            "reddit.com" or "redd.it" or "old.reddit.com"                      => "reddit",
            "vimeo.com"                                                         => "vimeo",
            "dailymotion.com" or "dai.ly"                                      => "dailymotion",
            "twitch.tv" or "clips.twitch.tv"                                   => "twitch",
            "bilibili.com" or "b23.tv"                                         => "bilibili",
            "rumble.com"                                                        => "rumble",
            "odysee.com" or "lbry.tv"                                          => "odysee",
            "kick.com"                                                          => "kick",
            "pinterest.com" or "pin.it" or "pinterest.co.uk"
                or "pinterest.fr" or "pinterest.de"                            => "pinterest",
            "linkedin.com"                                                      => "linkedin",
            "snapchat.com" or "snap.com"                                       => "snapchat",
            "telegram.org" or "telegram.me" or "t.me"                         => "telegram",
            "streamable.com"                                                    => "streamable",
            "9gag.com"                                                          => "9gag",
            "likee.video" or "l.likee.video"                                   => "likee",
            "triller.co"                                                        => "triller",
            "kwai.com" or "kw.ai"                                               => "kwai",
            "capcut.com"                                                        => "capcut",
            "ted.com"                                                           => "ted",
            "bbc.co.uk" or "bbc.com"                                           => "bbc",
            "cnn.com"                                                           => "cnn",
            "vk.com" or "vk.ru"                                                => "vk",
            "ok.ru"                                                             => "ok",
            "coub.com"                                                          => "coub",
            "open.spotify.com" or "spotify.com"                                => "spotify",
            "soundcloud.com" or "on.soundcloud.com"                            => "soundcloud",
            _ when host.EndsWith(".bandcamp.com") || host == "bandcamp.com"    => "bandcamp",
            "audiomack.com"                                                     => "audiomack",
            "mixcloud.com"                                                      => "mixcloud",
            "deezer.com" or "deezer.page.link"                                 => "deezer",
            "music.apple.com"                                                   => "applemusic",
            "tidal.com" or "listen.tidal.com"                                  => "tidal",
            _                                                                   => "generic"
        };
    }

    // ─── Per-platform yt-dlp args ─────────────────────────────────────────────
    // KEY FIX: TikTok requires --impersonate chrome-131 (needs curl_cffi installed)
    //          Facebook share/r/ URLs need --no-check-certificates + referer
    private static string GetPlatformArgs(string platform) => platform switch
    {
        "tiktok"    => "--impersonate chrome-131 --no-check-certificates",
        "instagram" => "--impersonate chrome-131 --add-header \"Referer:https://www.instagram.com/\"",
        "facebook"  => "--no-check-certificates " +
                       "--add-header \"Sec-Fetch-Site:same-origin\" " +
                       "--add-header \"Referer:https://www.facebook.com/\"",
        "twitter"   => "--add-header \"Referer:https://x.com/\"",
        "reddit"    => "--add-header \"Referer:https://www.reddit.com/\"",
        "bilibili"  => "--add-header \"Referer:https://www.bilibili.com/\"",
        "vk"        => "--add-header \"Referer:https://vk.com/\"",
        "pinterest" => "--add-header \"Referer:https://www.pinterest.com/\"",
        "twitch"    => "--no-check-certificates",
        "generic"   => "--no-check-certificates",
        _           => ""
    };

    // ─── Playlist detection ───────────────────────────────────────────────────
    private static bool IsPlaylist(string url, string platform)
    {
        var u = url.ToLowerInvariant();
        return platform switch
        {
            "youtube" => u.Contains("playlist?list=") ||
                         (u.Contains("list=") && !u.Contains("/watch") && !u.Contains("youtu.be")),
            "tiktok"  => u.Contains("/@") && !u.Contains("/video/"),
            _         => false,
        };
    }

    // ─── Qualities ────────────────────────────────────────────────────────────
    private static List<object> GetQualities(string platform, List<int>? heights = null)
    {
        if (platform is "spotify" or "soundcloud" or "bandcamp" or "audiomack"
                     or "mixcloud" or "deezer" or "applemusic" or "tidal")
            return new List<object>
            {
                new { label = "MP3", value = "mp3", type = "audio", available = true },
                new { label = "M4A", value = "m4a", type = "audio", available = true }
            };

        var all = new List<(string label, string value, string type)>
        {
            ("4K - 2160p", "2160", "video"),
            ("1080p",      "1080", "video"),
            ("720p",       "720",  "video"),
            ("480p",       "480",  "video"),
            ("360p",       "360",  "video"),
            ("MP3 Audio",  "mp3",  "audio"),
            ("M4A Audio",  "m4a",  "audio"),
        };

        return all
            .Where(q => q.type == "audio" || heights == null || heights.Count == 0 ||
                        heights.Any(h => h >= int.Parse(q.value)))
            .Select(q => (object)new
            {
                label     = q.label,
                value     = q.value,
                type      = q.type,
                available = heights == null || heights.Count == 0 || q.type == "audio" ||
                            heights.Any(h => h >= int.Parse(q.value))
            })
            .ToList();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────
    private static string ExtractPlainText(string content, string format)
    {
        var lines = content.Split('\n');
        if (format == "vtt")
            return string.Join(" ", lines
                .Where(l => !l.StartsWith("WEBVTT") && !l.Contains("-->")
                            && !string.IsNullOrWhiteSpace(l) && !l.Trim().All(char.IsDigit))
                .Select(l => l.Trim()).Distinct());
        if (format == "srt")
            return string.Join(" ", lines
                .Where(l => !l.Contains("-->") && !string.IsNullOrWhiteSpace(l)
                            && !l.Trim().All(char.IsDigit))
                .Select(l => l.Trim()));
        return content;
    }

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Split(Path.GetInvalidFileNameChars()));

    private static string BuildVideoUrl(string platform, string? id) =>
        platform switch
        {
            "youtube" => $"https://www.youtube.com/watch?v={id}",
            _         => id ?? ""
        };

    private async Task TrackRequest()
    {
        var claim = User.FindFirst("deviceId")?.Value;
        if (claim == null) return;
        var device = await _db.Devices.FindAsync(int.Parse(claim));
        if (device == null) return;
        device.RequestCount++;
        await _db.SaveChangesAsync();
    }

    private static Task<string> RunYtDlp(string args) => RunProcess("yt-dlp", args);

    private static async Task<string> RunProcess(string exe, string args,
        int timeoutSeconds = 300)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = Process.Start(psi)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask  = process.StandardError.ReadToEndAsync();

        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new Exception($"{exe} timed out after {timeoutSeconds}s");
        }

        var output = await outputTask;
        var error  = await errorTask;

        if (process.ExitCode != 0)
            throw new Exception(error.Length > 0 ? error : $"{exe} exited with code {process.ExitCode}");
        return output;
    }
}

public record MediaRequestDto(string Url);
public record MediaDownloadDto(string Url, string Quality);
public record SubtitleRequestDto(string Url, string? Language);