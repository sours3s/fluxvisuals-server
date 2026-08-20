using AuthServer.Data;
using AuthServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuthServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ModController : ControllerBase
{
    private readonly AuthDbContext _db;
    private readonly IConfiguration _configuration;

    public ModController(AuthDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    [HttpGet("version")]
    [AllowAnonymous]
    public async Task<IActionResult> GetVersion()
    {
        var settings = await _db.AppSettings.FindAsync(1);
        if (settings == null)
            return NotFound();

        // Приоритет: URL из админки (БД) → из appsettings (Mod:DownloadUrl) → по адресу запроса.
        string downloadUrl = settings.ModDownloadUrl;
        if (string.IsNullOrWhiteSpace(downloadUrl) ||
            downloadUrl.StartsWith("CHANGE", StringComparison.OrdinalIgnoreCase))
        {
            downloadUrl = _configuration["Mod:DownloadUrl"] ?? "";
        }
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            downloadUrl = "https://github.com/sours3s/FluxVisuals/releases/download/v1.0.17/fluxvisuals-1.0.17.jar";
        }

        return Ok(new
        {
            downloadUrl,
            version = "1.0.17", // маркер версии мода: лоадер по нему определяет, что jar нужно перекачать
            fileName = "fluxvisuals-1.0.17.jar"
        });
    }

    [HttpPost("version")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> UpdateVersion([FromBody] UpdateModVersionRequest req)
    {
        var settings = await _db.AppSettings.FindAsync(1);
        if (settings == null) return NotFound();

        settings.ModDownloadUrl = req.DownloadUrl;
        await _db.SaveChangesAsync();

        return Ok(new { downloadUrl = settings.ModDownloadUrl });
    }
}

public record UpdateModVersionRequest(string DownloadUrl);