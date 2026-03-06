using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using VideoDownloader.Data;
using VideoDownloader.DTOs;
using VideoDownloader.Models;

namespace VideoDownloader.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AuthController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    [HttpPost("register-device")]
    public async Task<IActionResult> RegisterDevice([FromBody] DeviceDto dto)
    {
        var fingerprint = GenerateFingerprint(dto);

        var device = await _db.Devices
            .FirstOrDefaultAsync(d => d.Fingerprint == fingerprint);

        if (device == null)
        {
            device = new Device
            {
                Serial       = dto.Serial,
                DeviceName   = dto.DeviceName,
                OS           = dto.OS,
                MacAddress   = dto.MacAddress,
                Fingerprint  = fingerprint,
                RegisteredAt = DateTime.UtcNow,
                LastSeenAt   = DateTime.UtcNow,
                IsBlocked    = false
            };
            _db.Devices.Add(device);
        }
        else
        {
            if (device.IsBlocked)
                return Unauthorized(new { message = "Device is blocked" });

            device.LastSeenAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        var token = GenerateJwt(device);
        return Ok(new { token, deviceId = device.Id });
    }

    private string GenerateFingerprint(DeviceDto dto)
    {
        var raw = $"{dto.Serial}|{dto.DeviceName}|{dto.OS}|{dto.MacAddress}";
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }

    private string GenerateJwt(Device device)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));

        var claims = new[]
        {
            new Claim("deviceId",    device.Id.ToString()),
            new Claim("fingerprint", device.Fingerprint),
            new Claim("deviceName",  device.DeviceName)
        };

        var token = new JwtSecurityToken(
            issuer:             _config["Jwt:Issuer"],
            audience:           _config["Jwt:Audience"],
            claims:             claims,
            expires:            DateTime.UtcNow.AddDays(30),
            signingCredentials: new SigningCredentials(
                key, SecurityAlgorithms.HmacSha256)
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}