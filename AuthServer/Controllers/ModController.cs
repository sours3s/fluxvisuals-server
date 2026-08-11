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
        // Читаем через EF Core, если колонки нет - используем конфиг
        string modDownloadUrl = "";
        string loaderDownloadUrl = "";

        try
        {
            var settings = await _db.AppSettings.FindAsync(1);
            if (settings != null)
            {
                modDownloadUrl = settings.ModDownloadUrl ?? "";
                // LoaderDownloadUrl может отсутствовать в старой БД
                try
                {
                    loaderDownloadUrl = settings.LoaderDownloadUrl ?? "";
                }
                catch
                {
                    loaderDownloadUrl = "";
                }
            }
        }
        catch (Exception)
        {
            // Игнорируем ошибки чтения БД
        }

        // Приоритет: URL из админки (БД) → из appsettings (Mod:DownloadUrl) → по адресу запроса.
        if (string.IsNullOrWhiteSpace(modDownloadUrl) ||
            modDownloadUrl.StartsWith("CHANGE", StringComparison.OrdinalIgnoreCase))
        {
            modDownloadUrl = _configuration["Mod:DownloadUrl"] ?? "";
        }
        if (string.IsNullOrWhiteSpace(modDownloadUrl))
        {
            modDownloadUrl = "https://github.com/sours3s/FluxVisuals/releases/download/v1.0.13/fluxvisuals-1.0.13.jar";
        }

        // Loader download URL
        if (string.IsNullOrWhiteSpace(loaderDownloadUrl) ||
            loaderDownloadUrl.StartsWith("CHANGE", StringComparison.OrdinalIgnoreCase))
        {
            loaderDownloadUrl = _configuration["Loader:DownloadUrl"] ?? "";
        }
        if (string.IsNullOrWhiteSpace(loaderDownloadUrl))
        {
            loaderDownloadUrl = "https://github.com/sours3s/FluxVisuals/releases/download/v1.0.10.loader/FluxVisualsLoader.exe";
        }

        return Ok(new
        {
            downloadUrl = modDownloadUrl,
            loaderDownloadUrl,
            version = "1.0.13",
            fileName = "fluxvisuals-mod-1.0.13.jar"
        });
    }

    [HttpPost("version")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> UpdateVersion([FromBody] UpdateModVersionRequest req)
    {
        var settings = await _db.AppSettings.FindAsync(1);
        if (settings == null)
        {
            settings = new AppSettings { Id = 1 };
            _db.AppSettings.Add(settings);
        }

        if (!string.IsNullOrWhiteSpace(req.ModDownloadUrl))
            settings.ModDownloadUrl = req.ModDownloadUrl;

        if (!string.IsNullOrWhiteSpace(req.LoaderDownloadUrl))
            settings.LoaderDownloadUrl = req.LoaderDownloadUrl;

        await _db.SaveChangesAsync();

        return Ok(new { modDownloadUrl = settings.ModDownloadUrl, loaderDownloadUrl = settings.LoaderDownloadUrl });
    }
}

public record UpdateModVersionRequest
{
    public string ModDownloadUrl { get; init; } = "";
    public string LoaderDownloadUrl { get; init; } = "";
}