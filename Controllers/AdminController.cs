using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VideoDownloader.Data;

namespace VideoDownloader.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AdminController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    private bool IsAuthorized() =>
        Request.Headers.TryGetValue("X-Admin-Key", out var key) &&
        key == _config["Admin:Key"];

    [HttpGet("devices")]
    public async Task<IActionResult> GetDevices([FromQuery] string? search = null)
    {
        if (!IsAuthorized()) return Unauthorized();

        var query = _db.Devices.AsQueryable();

        if (!string.IsNullOrEmpty(search))
            query = query.Where(d =>
                d.DeviceName.Contains(search) ||
                d.OS.Contains(search) ||
                d.Serial.Contains(search));

        var devices = await query
            .OrderByDescending(d => d.LastSeenAt)
            .Select(d => new
            {
                d.Id,
                d.DeviceName,
                d.OS,
                d.Serial,
                d.RegisteredAt,
                d.LastSeenAt,
                d.IsBlocked,
                d.RequestCount
            })
            .ToListAsync();

        return Ok(devices);
    }

    [HttpPost("block/{id}")]
    public async Task<IActionResult> BlockDevice(int id)
    {
        if (!IsAuthorized()) return Unauthorized();
        var device = await _db.Devices.FindAsync(id);
        if (device == null) return NotFound();
        device.IsBlocked = true;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Device blocked" });
    }

    [HttpPost("unblock/{id}")]
    public async Task<IActionResult> UnblockDevice(int id)
    {
        if (!IsAuthorized()) return Unauthorized();
        var device = await _db.Devices.FindAsync(id);
        if (device == null) return NotFound();
        device.IsBlocked = false;
        await _db.SaveChangesAsync();
        return Ok(new { message = "Device unblocked" });
    }

    [HttpDelete("devices/{id}")]
    public async Task<IActionResult> DeleteDevice(int id)
    {
        if (!IsAuthorized()) return Unauthorized();
        var device = await _db.Devices.FindAsync(id);
        if (device == null) return NotFound();
        _db.Devices.Remove(device);
        await _db.SaveChangesAsync();
        return Ok(new { message = "Device deleted" });
    }
}