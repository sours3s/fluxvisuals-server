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
    private readonly RSA _signingKey;

    public LaunchTicketService(AuthDbContext db, IOptions<LaunchTicketSettings> options)
    {
        _db = db;
        _settings = options.Value;
        if (string.IsNullOrWhiteSpace(_settings.PrivateKey))
            throw new InvalidOperationException("LaunchTickets:PrivateKey is not configured.");

        _signingKey = RSA.Create();
        _signingKey.ImportFromPem(_settings.PrivateKey);
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
        await _db.SaveChangesAsync();

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
                new RsaSecurityKey(_signingKey) { KeyId = _settings.KeyId },
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
                IssuerSigningKey = new RsaSecurityKey(_signingKey),
            };
            principal = new JwtSecurityTokenHandler().ValidateToken(token, validation, out _);
            jti = principal.FindFirstValue(JwtRegisteredClaimNames.Jti)
                ?? throw new SecurityTokenException("missing_jti");
            if (principal.FindFirstValue("challenge") != challenge)
                return (false, "challenge_mismatch");
        }
        catch (SecurityTokenExpiredException) { return (false, "ticket_expired"); }
        catch { return (false, "invalid_ticket"); }

        var ticket = await _db.LaunchTickets.AsNoTracking().FirstOrDefaultAsync(x => x.Jti == jti);
        if (ticket == null) return (false, "unknown_ticket");
        if (ticket.ExpiresAt <= DateTime.UtcNow) return (false, "ticket_expired");

        var changed = await _db.LaunchTickets
            .Where(x => x.Jti == jti && x.UsedAt == null && x.ExpiresAt > DateTime.UtcNow)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, DateTime.UtcNow));
        return changed == 1 ? (true, "ok") : (false, "ticket_replayed");
    }
}

public sealed class LaunchTicketException : Exception
{
    public string Code { get; }
    public LaunchTicketException(string code, string message) : base(message) => Code = code;
}
