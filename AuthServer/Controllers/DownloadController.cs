using AuthServer.Data;
using AuthServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuthServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DownloadController : ControllerBase
{
    private readonly AuthDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpFactory;

    // Кэш лоадера в памяти (Render-диск эфемерный, файл не храним на диске).
    private static byte[]? _loaderCache;
    private static DateTime _loaderCacheAt;
    private static readonly object CacheLock = new();

    public DownloadController(AuthDbContext db, IConfiguration configuration, IHttpClientFactory httpFactory)
    {
        _db = db;
        _configuration = configuration;
        _httpFactory = httpFactory;
    }

    /// <summary>
    /// Скачивание лоадера с сайта (без редиректа на GitHub).
    /// Доступен только клиентам/админам с оплаченным доступом.
    /// </summary>
    [HttpGet("loader")]
    [Authorize]
    public async Task<IActionResult> Loader(CancellationToken ct)
    {
        var idClaim = User.FindFirst("userId")?.Value;
        if (!int.TryParse(idClaim, out var userId))
            return Unauthorized();

        var user = await _db.Users.FindAsync(userId);
        if (user == null || !user.IsActive)
            return Unauthorized(new { error = "Аккаунт не активен" });

        if (!user.HasAccess(DateTime.UtcNow))
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = "Доступ не оплачен. Купите клиент в личном кабинете."
            });

        string url = _configuration["Loader:DownloadUrl"] ?? "";
        if (string.IsNullOrWhiteSpace(url))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Файл клиента не настроен" });

        var bytes = await GetLoaderBytesAsync(url, ct);

        Response.Headers["Content-Disposition"] = "attachment; filename=\"FluxVisualsLoader.exe\"";
        return File(bytes, "application/octet-stream");
    }

    private static async Task<byte[]> GetLoaderBytesAsync(string url, CancellationToken ct)
    {
        // 10-минутный кэш в памяти
        lock (CacheLock)
        {
            if (_loaderCache != null && DateTime.UtcNow - _loaderCacheAt < TimeSpan.FromMinutes(10))
                return _loaderCache;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var bytes = await http.GetByteArrayAsync(url, ct);

        lock (CacheLock)
        {
            _loaderCache = bytes;
            _loaderCacheAt = DateTime.UtcNow;
        }
        return bytes;
    }
}
