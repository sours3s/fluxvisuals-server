using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AuthServer.Data;
using AuthServer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthServer.Services;

public sealed class LaunchTicketSettings
{
    public string PrivateKey { get; set; } = "";
    public string Issuer { get; set; } = "FluxVisualsAuth";
    public string Audience { get; set; } = "FluxVisualsMod";
    public string KeyId { get; set; } = "flux-2026-08";
    public int LifetimeSeconds { get; set; } = 120;
}

public sealed class LaunchTicketService
{
    private readonly AuthDbContext _db;
    private readonly LaunchTicketSettings _settings;
    private RSA? _signingKey;

    public LaunchTicketService(AuthDbContext db, IOptions<LaunchTicketSettings> options)
    {
        _db = db;
        _settings = options.Value;
    }

    /// <summary>Загружает подписывающий ключ лениво. Если не задан или битый — понятная ошибка, а не 500.</summary>
    private RSA EnsureSigningKey()
    {
        if (_signingKey != null) return _signingKey;
        var pem = NormalizePem(_settings.PrivateKey);
        if (string.IsNullOrWhiteSpace(pem))
            throw new LaunchTicketException("not_configured",
                "Приватный ключ тикетов не задан: установи LaunchTickets__PrivateKey на сервере (Render → env).");
        try
        {
            var key = RSA.Create();
            key.ImportFromPem(pem);
            _signingKey = key;
            return key;
        }
        catch (Exception ex)
        {
            throw new LaunchTicketException("invalid_key_config",
                "Приватный ключ тикетов некорректен: проверь LaunchTickets__PrivateKey (нужно содержимое flux-private.pem). " + ex.Message);
        }
    }

    /// <summary>Чинит PEM из env-переменной: Render часто хранит ключ одной строкой с литеральными \n —
    /// ImportFromPem такое не принимает. Нормализуем в реальные переносы строк.</summary>
    private static string NormalizePem(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) return pem;
        return pem.Replace("\\r\\n", "\n").Replace("\\n", "\n").Trim();
    }

    public async Task<string> IssueAsync(User user, string challenge, string hwid)
    {
        if (!user.IsActive || !user.HasAccess(DateTime.UtcNow))
            throw new LaunchTicketException("no_access", "Доступ к клиенту отсутствует или истёк.");
        if (string.IsNullOrWhiteSpace(user.Hwid) || !string.Equals(user.Hwid, hwid, StringComparison.Ordinal))
            throw new LaunchTicketException("hwid_mismatch", "HWID не совпадает с этим аккаунтом.");

        byte[] challengeBytes;
        try { challengeBytes = Base64UrlEncoder.DecodeBytes(challenge); }
        catch { throw new LaunchTicketException("invalid_challenge", "Некорректный challenge."); }
        if (challengeBytes.Length < 16 || challengeBytes.Length > 64)
            throw new LaunchTicketException("invalid_challenge", "Некорректный challenge.");

        var now = DateTime.UtcNow;
        var expires = now.AddSeconds(Math.Clamp(_settings.LifetimeSeconds, 15, 300));
        var jti = Guid.NewGuid().ToString("N");
        _db.LaunchTickets.Add(new LaunchTicket
        {
            Jti = jti,
            UserId = user.Id,
            ExpiresAt = expires,
            ChallengeHash = Convert.ToHexString(SHA256.HashData(challengeBytes))
        });
        await SaveLaunchTicketAsync();

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.Username),
            new Claim(JwtRegisteredClaimNames.Jti, jti),
            new Claim("challenge", challenge),
            new Claim("protocol", "1")
        };
        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(
                new RsaSecurityKey(EnsureSigningKey()) { KeyId = _settings.KeyId },
                SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<(bool Ok, string Code)> ConsumeAsync(string token, string challenge)
    {
        ClaimsPrincipal principal;
        string jti;
        try
        {
            var validation = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _settings.Issuer,
                ValidateAudience = true,
                ValidAudience = _settings.Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(2),
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(EnsureSigningKey()),
            };
            principal = new JwtSecurityTokenHandler().ValidateToken(token, validation, out _);
            jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti)
                ?? throw new SecurityTokenException("missing_jti");
            if (principal.FindFirstValue("challenge") != challenge)
                return (false, "challenge_mismatch");
        }
        catch (SecurityTokenExpiredException) { return (false, "ticket_expired"); }
        catch { return (false, "invalid_ticket"); }

        try
        {
            var ticket = await _db.LaunchTickets.AsNoTracking().FirstOrDefaultAsync(x => x.Jti == jti);
            if (ticket == null) return (false, "unknown_ticket");
            if (ticket.ExpiresAt <= DateTime.UtcNow) return (false, "ticket_expired");

            var changed = await _db.LaunchTickets
                .Where(x => x.Jti == jti && x.UsedAt == null && x.ExpiresAt > DateTime.UtcNow)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, DateTime.UtcNow));
            return changed == 1 ? (true, "ok") : (false, "ticket_replayed");
        }
        catch (Exception ex) when (IsMissingRelation(ex) || ex is DbUpdateException)
        {
            // Мод не считает ошибку consume блокирующей, но и 500 отдавать не должны.
            return (false, "server_error");
        }
    }

    /// <summary>Сохраняет тикет. Если таблицы ещё нет (старые деплои) — создаёт её на лету и повторяет.</summary>
    private async Task SaveLaunchTicketAsync()
    {
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsMissingRelation(ex))
        {
            EnsureLaunchTicketsTable();
            await _db.SaveChangesAsync();
        }
    }

    private static bool IsMissingRelation(Exception ex)
    {
        var inner = ex is DbUpdateException due ? due.InnerException ?? due : ex;
        return inner is Npgsql.PostgresException { SqlState: "42P01" }
            || inner is Microsoft.Data.Sqlite.SqliteException
            || inner.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Дублирует миграцию из Program.cs: для существующих БД EnsureCreated новые таблицы не создаёт.</summary>
    private void EnsureLaunchTicketsTable()
    {
        if (_db.Database.GetDbConnection() is Npgsql.NpgsqlConnection)
            _db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS \"LaunchTickets\" (" +
                "\"Jti\" text NOT NULL CONSTRAINT \"PK_LaunchTickets\" PRIMARY KEY, " +
                "\"UserId\" integer NOT NULL, " +
                "\"ChallengeHash\" text NOT NULL, " +
                "\"ExpiresAt\" timestamp with time zone NOT NULL, " +
                "\"UsedAt\" timestamp with time zone NULL, " +
                "CONSTRAINT \"FK_LaunchTickets_Users_UserId\" FOREIGN KEY (\"UserId\") REFERENCES \"Users\"(\"Id\") ON DELETE CASCADE)");
        else
            _db.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS \"LaunchTickets\" (" +
                "\"Jti\" TEXT NOT NULL CONSTRAINT \"PK_LaunchTickets\" PRIMARY KEY, " +
                "\"UserId\" INTEGER NOT NULL, " +
                "\"ChallengeHash\" TEXT NOT NULL, " +
                "\"ExpiresAt\" TEXT NOT NULL, " +
                "\"UsedAt\" TEXT NULL)");
    }
}

public sealed class LaunchTicketException : Exception
{
    public string Code { get; }
    public LaunchTicketException(string code, string message) : base(message) => Code = code;
}
