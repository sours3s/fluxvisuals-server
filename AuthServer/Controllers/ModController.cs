using AuthServer.Data;
using AuthServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

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

        // Единственный источник ссылок — админ-панель (БД). Версия выводится из URL.
        string modUrl = (settings.ModDownloadUrl ?? "").Trim();
        string loaderUrl = (settings.LoaderDownloadUrl ?? "").Trim();

        if (string.IsNullOrWhiteSpace(modUrl))
            return BadRequest(new { error = "URL мода не задан в админ-панели" });

        return Ok(new
        {
            downloadUrl = modUrl,
            version = VersionFromUrl(modUrl),
            fileName = FileNameFromUrl(modUrl),
            loaderDownloadUrl = loaderUrl,
            loaderVersion = VersionFromUrl(loaderUrl)
        });
    }

    private static string FileNameFromUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? Path.GetFileName(uri.AbsolutePath)
            : "";
    }

    private static string VersionFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        var path = uri.AbsolutePath;
        var match = Regex.Match(path, @"(?:^|/)v?(\d+(?:\.\d+){1,3})(?:[-_.][^/]*)?(?:/|$)");
        if (!match.Success)
            match = Regex.Match(path, @"[-_]v?(\d+(?:\.\d+){1,3})(?=[-_.]|$)");
        return match.Success ? match.Groups[1].Value : "";
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

    [HttpPost("loader")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> UpdateLoader([FromBody] UpdateLoaderRequest req)
    {
        var settings = await _db.AppSettings.FindAsync(1);
        if (settings == null) return NotFound();

        settings.LoaderDownloadUrl = req.DownloadUrl;
        await _db.SaveChangesAsync();

        return Ok(new { downloadUrl = settings.LoaderDownloadUrl });
    }
}

public record UpdateModVersionRequest(string DownloadUrl);
public record UpdateLoaderRequest(string DownloadUrl);