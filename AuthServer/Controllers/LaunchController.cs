using System.Security.Cryptography;
using System.Security.Claims;
using AuthServer.Data;
using AuthServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuthServer.Controllers;

[ApiController]
[Route("api/launch")]
public sealed class LaunchController : ControllerBase
{
    private readonly AuthDbContext _db;
    private readonly LaunchTicketService _tickets;

    public LaunchController(AuthDbContext db, LaunchTicketService tickets)
    {
        _db = db;
        _tickets = tickets;
    }

    [HttpPost("issue")]
    [Authorize]
    public async Task<IActionResult> Issue([FromBody] IssueLaunchRequest request)
    {
        var userId = User.FindFirstValue("userId");
        if (!int.TryParse(userId, out var id))
            return Unauthorized(new { code = "invalid_auth" });

        var user = await _db.Users.FindAsync(id);
        if (user == null || !user.IsActive)
            return Unauthorized(new { code = "invalid_auth" });

        try
        {
            var hwid = request.Hwid?.Trim() ?? "";
            var ticket = await _tickets.IssueAsync(user, request.Challenge ?? "", hwid);
            return Ok(new { ticket, protocol = 1 });
        }
        catch (LaunchTicketException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { code = ex.Code, error = ex.Message });
        }
        catch (Exception ex)
        {
            // Неожиданная ошибка (БД, ключ и т.п.) — отдаём причину, чтобы лоадер показал её, а не пустое тело.
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { code = "server_error", error = ex.Message });
        }
    }

    [HttpPost("consume")]
    [AllowAnonymous]
    public async Task<IActionResult> Consume([FromBody] ConsumeLaunchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Ticket) || string.IsNullOrWhiteSpace(request.Challenge))
            return BadRequest(new { code = "invalid_request" });

        var result = await _tickets.ConsumeAsync(request.Ticket, request.Challenge);
        return result.Ok
            ? Ok(new { ok = true })
            : StatusCode(StatusCodes.Status403Forbidden, new { ok = false, code = result.Code });
    }
}

public sealed record IssueLaunchRequest(string? Challenge, string? Hwid);
public sealed record ConsumeLaunchRequest(string Ticket, string Challenge);
