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
        var now = DateTime.UtcNow;
        var users = await _db.Users
            .OrderBy(u => u.Id)
            .Select(u => new
            {
                u.Id,
                u.Uid,
                u.Username,
                u.Role,
                u.IsActive,
                u.AccessExpiresAt,
                HasAccess = u.Role != "user" && u.IsActive && (u.AccessExpiresAt == null || u.AccessExpiresAt > now),
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

        var role = NormalizeRole(req.Role);
        var user = new User { Username = req.Username, Role = role };
        user.SetPassword(req.Password);

        if (req.Uid.HasValue)
        {
            if (req.Uid.Value < 1)
                return BadRequest(new { error = "UID должен быть не меньше 1" });
            if (await _db.Users.AnyAsync(u => u.Uid == req.Uid.Value))
                return Conflict(new { error = "Этот UID уже занят" });
            user.Uid = req.Uid.Value;
        }
        else
        {
            user.Uid = (await _db.Users.MaxAsync(u => (int?)u.Uid) ?? 0) + 1;
        }

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return Ok(new { id = user.Id, uid = user.Uid, username = user.Username, role });
    }

    [HttpPut("users/{id}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest req)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(req.Password))
            user.SetPassword(req.Password);

        if (!string.IsNullOrWhiteSpace(req.Role))
            user.Role = NormalizeRole(req.Role);

        if (req.Uid.HasValue)
        {
            if (req.Uid.Value < 1)
                return BadRequest(new { error = "UID должен быть не меньше 1" });
            if (await _db.Users.AnyAsync(u => u.Uid == req.Uid.Value && u.Id != id))
                return Conflict(new { error = "Этот UID уже занят другим юзером" });
            user.Uid = req.Uid.Value;
        }

        user.IsActive = req.IsActive;

        // Выдача доступа. Роль "client" получает только тот, кто ещё не админ:
        // иначе выдача доступа админу сбросит его роль на client и он потеряет админку.
        if (req.GrantDays is > 0)
        {
            if (user.Role != "admin") user.Role = "client";
            var now = DateTime.UtcNow;
            var baseTime = user.AccessExpiresAt != null && user.AccessExpiresAt > now ? user.AccessExpiresAt.Value : now;
            user.AccessExpiresAt = baseTime.AddDays(req.GrantDays.Value);
        }
        if (req.SetLifetime)
        {
            if (user.Role != "admin") user.Role = "client";
            user.AccessExpiresAt = null; // пожизненно
        }
        if (req.ClearAccess)
        {
            if (user.Role == "client") user.Role = "user";
            user.AccessExpiresAt = null;
        }

        if (req.ResetHwid)
            user.Hwid = null;

        await _db.SaveChangesAsync();
        return Ok(new { id = user.Id, username = user.Username, role = user.Role, accessExpiresAt = user.AccessExpiresAt });
    }

    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        // Сначала связанные записи — иначе FK-нарушение при удалении → 500
        var orders = await _db.PaymentOrders.Where(o => o.UserId == id).ToListAsync();
        if (orders.Count > 0) _db.PaymentOrders.RemoveRange(orders);

        var logs = await _db.AuthLogs.Where(l => l.UserId == id).ToListAsync();
        if (logs.Count > 0) _db.AuthLogs.RemoveRange(logs);

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
        var now = DateTime.UtcNow;
        return Ok(new
        {
            totalUsers = await _db.Users.CountAsync(),
            activeUsers = await _db.Users.CountAsync(u => u.IsActive),
            admins = await _db.Users.CountAsync(u => u.Role == "admin"),
            clients = await _db.Users.CountAsync(u => u.Role == "client"),
            users = await _db.Users.CountAsync(u => u.Role == "user"),
            clientsWithAccess = await _db.Users.CountAsync(u => u.Role == "client" && u.IsActive && (u.AccessExpiresAt == null || u.AccessExpiresAt > now)),
            expiredClients = await _db.Users.CountAsync(u => u.Role == "client" && u.AccessExpiresAt != null && u.AccessExpiresAt <= now),
            hwidLocked = await _db.Users.CountAsync(u => !string.IsNullOrEmpty(u.Hwid)),
            recentLogins = await _db.AuthLogs.CountAsync(l => l.Action == "login" && l.Success && l.CreatedAt > now.AddDays(-7))
        });
    }

    private static string NormalizeRole(string? role)
    {
        var r = (role ?? "user").Trim().ToLowerInvariant();
        return r is "admin" or "client" or "user" ? r : "user";
    }
}

public record CreateUserRequest(string Username, string Password, string? Role = null, int? Uid = null);
public record UpdateUserRequest(string? Password = null, string? Role = null, bool IsActive = true,
    int? GrantDays = null, bool SetLifetime = false, bool ClearAccess = false, bool ResetHwid = false, int? Uid = null);
