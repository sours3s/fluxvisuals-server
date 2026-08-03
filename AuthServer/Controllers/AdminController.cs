using AuthServer.Data;
using AuthServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuthServer.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "admin")]
public class AdminController : ControllerBase
{
    private readonly AuthDbContext _db;

    public AdminController(AuthDbContext db)
    {
        _db = db;
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers()
    {
        var users = await _db.Users
            .Select(u => new
            {
                u.Id,
                u.Username,
                u.IsAdmin,
                u.IsActive,
                u.CreatedAt,
                u.LastLoginAt,
                u.Hwid,
                u.HwidLockedAt
            })
            .ToListAsync();
        return Ok(users);
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "Username and password required" });

        if (await _db.Users.AnyAsync(u => u.Username == req.Username))
            return Conflict(new { error = "Username already exists" });

        var user = new User { Username = req.Username, IsAdmin = req.IsAdmin };
        user.SetPassword(req.Password);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return Ok(new { id = user.Id, username = user.Username });
    }

    [HttpPut("users/{id}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest req)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(req.Password))
            user.SetPassword(req.Password);

        user.IsAdmin = req.IsAdmin;
        user.IsActive = req.IsActive;

        if (req.ResetHwid)
            user.Hwid = null;

        await _db.SaveChangesAsync();
        return Ok(new { id = user.Id, username = user.Username });
    }

    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        _db.Users.Remove(user);
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] int limit = 100)
    {
        var logs = await _db.AuthLogs
            .OrderByDescending(l => l.CreatedAt)
            .Take(limit)
            .Select(l => new
            {
                l.Id,
                l.UserId,
                Username = l.User != null ? l.User.Username : "Unknown",
                l.Action,
                l.IpAddress,
                l.Hwid,
                l.Success,
                l.CreatedAt
            })
            .ToListAsync();
        return Ok(logs);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var totalUsers = await _db.Users.CountAsync();
        var activeUsers = await _db.Users.CountAsync(u => u.IsActive);
        var adminUsers = await _db.Users.CountAsync(u => u.IsAdmin);
        var hwidLocked = await _db.Users.CountAsync(u => !string.IsNullOrEmpty(u.Hwid));
        var recentLogins = await _db.AuthLogs.CountAsync(l => l.Action == "login" && l.Success && l.CreatedAt > DateTime.UtcNow.AddDays(-7));

        return Ok(new
        {
            totalUsers,
            activeUsers,
            adminUsers,
            hwidLocked,
            recentLogins
        });
    }
}

public record CreateUserRequest(string Username, string Password, bool IsAdmin = false);
public record UpdateUserRequest(string? Password, bool IsAdmin, bool IsActive, bool ResetHwid = false);