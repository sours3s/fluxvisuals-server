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

    public ModController(AuthDbContext db)
    {
        _db = db;
    }

    [HttpGet("version")]
    [AllowAnonymous]
    public async Task<IActionResult> GetVersion()
    {
        var settings = await _db.AppSettings.FindAsync(1);
        if (settings == null)
            return NotFound();

        // URL мода строится из адреса, с которого пришёл запрос — работает на любом хосте/домене.
        // Если админ явно задал свой URL (например, GitHub) — используем его.
        string downloadUrl = settings.ModDownloadUrl;
        if (string.IsNullOrWhiteSpace(downloadUrl) ||
            downloadUrl.StartsWith("CHANGE", StringComparison.OrdinalIgnoreCase))
        {
            downloadUrl = $"{Request.Scheme}://{Request.Host}/mods/fluxvisuals-mod-1.21.11.jar";
        }

        return Ok(new
        {
            downloadUrl,
            version = "1.21.11", // Можно вынести в настройки
            fileName = "fluxvisuals-mod-1.21.11.jar"
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