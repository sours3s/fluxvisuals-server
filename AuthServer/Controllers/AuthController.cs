using AuthServer.Data;
using AuthServer.Models;
using AuthServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace AuthServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AuthDbContext _db;
    private readonly JwtService _jwt;
    private readonly JwtSettings _jwtSettings;

    public AuthController(AuthDbContext db, JwtService jwt, IOptions<JwtSettings> jwtSettings)
    {
        _db = db;
        _jwt = jwt;
        _jwtSettings = jwtSettings.Value;
    }

    [HttpPost("register")]
    [Authorize(Policy = "admin")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "Username and password required" });

        if (await _db.Users.AnyAsync(u => u.Username == req.Username))
            return Conflict(new { error = "Username already exists" });

        var user = new User { Username = req.Username, IsAdmin = req.IsAdmin };
        user.SetPassword(req.Password);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        await LogAuth(user.Id, "register", req.IpAddress, null, true);
        return Ok(new { id = user.Id, username = user.Username });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == req.Username);
        if (user == null || !user.IsActive)
        {
            await LogAuth(0, "login", req.IpAddress, req.Hwid, false);
            return Unauthorized(new { error = "Invalid credentials or account disabled" });
        }

        if (!user.VerifyPassword(req.Password))
        {
            await LogAuth(user.Id, "login", req.IpAddress, req.Hwid, false);
            return Unauthorized(new { error = "Invalid credentials" });
        }

        // HWID check/bind
        if (!string.IsNullOrWhiteSpace(req.Hwid))
        {
            if (!user.CheckHwid(req.Hwid))
            {
                await LogAuth(user.Id, "hwid_mismatch", req.IpAddress, req.Hwid, false);
                return Unauthorized(new { error = "HWID mismatch. This account is bound to another computer." });
            }

            if (string.IsNullOrEmpty(user.Hwid))
            {
                user.LockHwid(req.Hwid);
            }
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Generate token using JwtService (which uses injected settings)
        var token = _jwt.GenerateToken(user);

        // Debug: decode the token to verify role claim
        var handler = new JwtSecurityTokenHandler();
        var jwtToken = handler.ReadJwtToken(token);
        var roleClaim = jwtToken.Claims.FirstOrDefault(c => c.Type == "role");
        Console.WriteLine($"[DEBUG] Token claims: {string.Join(", ", jwtToken.Claims.Select(c => $"{c.Type}={c.Value}"))}");
        Console.WriteLine($"[DEBUG] Role claim: {roleClaim?.Value ?? "MISSING"}");

        await LogAuth(user.Id, "login", req.IpAddress, req.Hwid, true);

        return Ok(new { token, username = user.Username, isAdmin = user.IsAdmin });
    }

    [HttpGet("verify")]
    [Authorize]
    public async Task<IActionResult> VerifyToken()
    {
        var userIdClaim = User.FindFirst("userId")?.Value;
        if (!int.TryParse(userIdClaim, out var userId))
            return Unauthorized();

        var user = await _db.Users.FindAsync(userId);
        if (user == null || !user.IsActive)
            return Unauthorized();

        return Ok(new { username = user.Username, isAdmin = user.IsAdmin });
    }

    [HttpGet("claims")]
    [Authorize]
    public IActionResult GetClaims()
    {
        return Ok(User.Claims.Select(c => new { c.Type, c.Value }));
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        var userIdClaim = User.FindFirst("userId")?.Value;
        if (int.TryParse(userIdClaim, out var userId))
            await LogAuth(userId, "logout", GetClientIp(), null, true);
        return Ok();
    }

    private async Task LogAuth(int userId, string action, string? ip, string? hwid, bool success)
    {
        _db.AuthLogs.Add(new AuthLog
        {
            // 0 = юзер не найден (неудачная попытка) — сохраняем null, иначе FK-ошибка
            UserId = userId == 0 ? null : userId,
            Action = action,
            IpAddress = ip,
            Hwid = hwid,
            Success = success
        });
        await _db.SaveChangesAsync();
    }

    private string? GetClientIp() => Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? Request.HttpContext.Connection.RemoteIpAddress?.ToString();
}

public record RegisterRequest(string Username, string Password, bool IsAdmin = false, string? IpAddress = null);
public record LoginRequest(string Username, string Password, string? Hwid = null, string? IpAddress = null);