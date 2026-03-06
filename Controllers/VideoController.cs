using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Text.Json;
using VideoDownloader.Data;

namespace VideoDownloader.Controllers;

[ApiController]
[Route("api/video")]
[Authorize]
public class VideoController : ControllerBase
{
    private readonly AppDbContext _db;

    public VideoController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost("info")]
    public async Task<IActionResult> GetVideoInfo([FromBody] VideoRequestDto dto)
    {
        try
        {
            var deviceId = int.Parse(User.FindFirst("deviceId")!.Value);
            var device = await _db.Devices.FindAsync(deviceId);
            if (device != null)
            {
                device.RequestCount++;
                await _db.SaveChangesAsync();
            }

            var result = await RunYtDlp($"-J --no-playlist \"{dto.Url}\"");
            var json = JsonDocument.Parse(result);
            var root = json.RootElement;

            return Ok(new
            {
                title     = root.GetProperty("title").GetString(),
                duration  = root.GetProperty("duration").GetDouble(),
                thumbnail = root.GetProperty("thumbnail").GetString(),
                uploader  = root.GetProperty("uploader").GetString(),
                formats   = root.GetProperty("formats").EnumerateArray()
                    .Where(f => f.TryGetProperty("height", out var h)
                                && h.ValueKind == JsonValueKind.Number)
                    .Select(f => new
                    {
                        formatId = f.GetProperty("format_id").GetString(),
                        ext      = f.GetProperty("ext").GetString(),
                        height   = f.GetProperty("height").GetInt32(),
                        filesize = f.TryGetProperty("filesize", out var s)
                                   && s.ValueKind == JsonValueKind.Number
                                   ? s.GetInt64() : 0
                    })
                    .OrderByDescending(f => f.height)
                    .DistinctBy(f => f.height)
                    .Take(5)
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("download")]
    public async Task<IActionResult> GetDownloadLink([FromBody] VideoDownloadDto dto)
    {
        try
        {
            var deviceId = int.Parse(User.FindFirst("deviceId")!.Value);
            var device = await _db.Devices.FindAsync(deviceId);
            if (device != null)
            {
                device.RequestCount++;
                await _db.SaveChangesAsync();
            }

            var format = dto.Quality switch
            {
                "1080" => "bestvideo[height<=1080]+bestaudio/best[height<=1080]",
                "720"  => "bestvideo[height<=720]+bestaudio/best[height<=720]",
                "480"  => "bestvideo[height<=480]+bestaudio/best[height<=480]",
                "mp3"  => "bestaudio",
                _      => "best"
            };

            var result = await RunYtDlp($"-g -f \"{format}\" \"{dto.Url}\"");
            var links  = result.Trim().Split('\n');

            return Ok(new
            {
                videoUrl = links.Length > 0 ? links[0] : null,
                audioUrl = links.Length > 1 ? links[1] : null
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private async Task<string> RunYtDlp(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "yt-dlp",
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error  = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new Exception(error);

        return output;
    }
}

public record VideoRequestDto(string Url);
public record VideoDownloadDto(string Url, string Quality);