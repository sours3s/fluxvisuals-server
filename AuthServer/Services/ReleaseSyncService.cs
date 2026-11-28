using System.Text.Json;
using System.Text.RegularExpressions;
using AuthServer.Data;
using Microsoft.EntityFrameworkCore;

namespace AuthServer.Services;

/// <summary>
/// Автоматически подтягивает ссылки на мод и лоадер из последних релизов в настройки
/// админ-панели. Лоадер по-прежнему берёт ссылки только из /api/mod/version — вручную
/// их задавать не нужно: достаточно опубликовать релиз.
/// </summary>
public sealed class ReleaseSyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ReleaseSyncService> _logger;
    private readonly IConfiguration _config;
    private static readonly HttpClient Http = CreateClient();

    public ReleaseSyncService(
        IServiceScopeFactory scopes,
        ILogger<ReleaseSyncService> logger,
        IConfiguration config)
    {
        _scopes = scopes;
        _logger = logger;
        _config = config;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("FluxVisualsAuth/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("ReleaseSync:Enabled", true)) return;

        int minutes = Math.Max(1, _config.GetValue("ReleaseSync:IntervalMinutes", 15));
        string repo = _config["ReleaseSync:Repository"] ?? "sours3s/FluxVisuals";
        var apiUrl = $"https://api.github.com/repos/{repo}/releases?per_page=30";

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncAsync(apiUrl, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось синхронизировать ссылки на релизы");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SyncAsync(string apiUrl, CancellationToken ct)
    {
        using var response = await Http.GetAsync(apiUrl, ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("GitHub Releases ответил {Status}", (int)response.StatusCode);
            return;
        }

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        string? modUrl = null;
        string? loaderUrl = null;
        DateTimeOffset newestMod = DateTimeOffset.MinValue;
        Version? newestLoaderVersion = null;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            bool draft = release.TryGetProperty("draft", out var d) && d.GetBoolean();
            bool prerelease = release.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            if (draft || !release.TryGetProperty("assets", out var assets)) continue;

            string tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var versionMatch = Regex.Match(tag, @"v?(\d+(?:\.\d+){1,3})");

            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(url)) continue;

                if (!prerelease && name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    var published = DateTimeOffset.MinValue;
                    if (release.TryGetProperty("published_at", out var publishedAt) &&
                        publishedAt.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(publishedAt.GetString(), out var parsed))
                    {
                        published = parsed;
                    }

                    if (published >= newestMod)
                    {
                        newestMod = published;
                        modUrl = url;
                    }
                }

                if (name.Equals("FluxVisualsLoader.exe", StringComparison.OrdinalIgnoreCase) &&
                    versionMatch.Success &&
                    Version.TryParse(versionMatch.Groups[1].Value, out var version) &&
                    (newestLoaderVersion == null || version > newestLoaderVersion))
                {
                    newestLoaderVersion = version;
                    loaderUrl = url;
                }
            }
        }

        if (modUrl == null && loaderUrl == null) return;

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var settings = await db.AppSettings.FindAsync(1);
        if (settings == null) return;

        bool changed = false;
        if (modUrl != null && settings.ModDownloadUrl != modUrl)
        {
            settings.ModDownloadUrl = modUrl;
            changed = true;
            _logger.LogInformation("Ссылка на мод обновлена: {Url}", modUrl);
        }
        if (loaderUrl != null && settings.LoaderDownloadUrl != loaderUrl)
        {
            settings.LoaderDownloadUrl = loaderUrl;
            changed = true;
            _logger.LogInformation("Ссылка на лоадер обновлена: {Url}", loaderUrl);
        }

        if (changed) await db.SaveChangesAsync(ct);
    }
}
