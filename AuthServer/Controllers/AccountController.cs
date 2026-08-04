using AuthServer.Data;
using AuthServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuthServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AccountController : ControllerBase
{
    private readonly AuthDbContext _db;
    private readonly IConfiguration _configuration;

    public AccountController(AuthDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    private async Task<User?> CurrentUser()
    {
        var idClaim = User.FindFirst("userId")?.Value;
        if (!int.TryParse(idClaim, out var userId)) return null;
        return await _db.Users.FindAsync(userId);
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> Me()
    {
        var user = await CurrentUser();
        if (user == null || !user.IsActive) return Unauthorized();

        return Ok(new
        {
            uid = user.Uid,
            username = user.Username,
            role = user.Role,
            hasAccess = user.HasAccess(DateTime.UtcNow),
            accessExpiresAt = user.AccessExpiresAt,
            hwid = user.Hwid,
            hwidLockedAt = user.HwidLockedAt,
            createdAt = user.CreatedAt
        });
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        if (string.IsNullOrWhiteSpace(req.CurrentPassword) || !user.VerifyPassword(req.CurrentPassword))
            return BadRequest(new { error = "Текущий пароль неверен" });
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
            return BadRequest(new { error = "Новый пароль минимум 6 символов" });

        user.SetPassword(req.NewPassword);
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpGet("plans")]
    [AllowAnonymous]
    public IActionResult Plans()
    {
        var plans = _configuration.GetSection("Plans").Get<List<PlanConfig>>() ?? new();
        return Ok(plans.Select(p => new
        {
            p.Id,
            p.Name,
            p.Days,
            p.Lifetime,
            p.Price,
            p.Currency,
            p.Description
        }));
    }
}

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public class PlanConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Days { get; set; }
    public bool Lifetime { get; set; }
    public decimal Price { get; set; }
    public string Currency { get; set; } = "USD";
    public string? Description { get; set; }
}
