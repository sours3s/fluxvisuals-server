using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BCrypt.Net;

namespace AuthServer.Models;

public class User
{
    [Key]
    public int Id { get; set; }

    /// <summary>Публичный UID (порядковый номер). Выдаётся при регистрации по порядку
    /// (первый зарегистрировавшийся = UID 1). Админ может менять любому юзеру.</summary>
    public int? Uid { get; set; }

    [Required]
    [MaxLength(50)]
    public string Username { get; set; } = "";

    [Required]
    [MaxLength(255)]
    public string PasswordHash { get; set; } = "";

    [MaxLength(500)]
    public string? Hwid { get; set; }

    /// <summary>Роль: "admin" | "client" (купил доступ) | "user" (обычный, без доступа).</summary>
    [Required]
    [MaxLength(20)]
    public string Role { get; set; } = "user";

    /// <summary>Срок доступа для client. null = пожизненно.</summary>
    public DateTime? AccessExpiresAt { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    public DateTime? HwidLockedAt { get; set; }

    public bool IsAdminRole => Role == "admin";

    /// <summary>Есть ли право пользоваться клиентом: роль client/admin и срок не истёк (или пожизненно).</summary>
    public bool HasAccess(DateTime now)
    {
        if (Role != "client" && Role != "admin") return false;
        if (!IsActive) return false;
        return AccessExpiresAt == null || AccessExpiresAt > now;
    }

    public void SetPassword(string password)
    {
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, BCrypt.Net.BCrypt.GenerateSalt(12));
    }

    public bool VerifyPassword(string password)
    {
        return BCrypt.Net.BCrypt.Verify(password, PasswordHash);
    }

    public void LockHwid(string hwid)
    {
        Hwid = hwid;
        HwidLockedAt = DateTime.UtcNow;
    }

    public bool CheckHwid(string hwid)
    {
        if (string.IsNullOrEmpty(Hwid)) return true; // First login - auto-bind
        return Hwid == hwid;
    }
}

public class AuthLog
{
    [Key]
    public int Id { get; set; }

    // nullable: для неудачных попыток с неизвестным юзером (null вместо несуществующего Id=0)
    public int? UserId { get; set; }

    [ForeignKey("UserId")]
    public User? User { get; set; }

    public string Action { get; set; } = ""; // login, register, hwid_mismatch, banned

    public string? IpAddress { get; set; }

    public string? Hwid { get; set; }

    public bool Success { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AppSettings
{
    [Key]
    public int Id { get; set; } = 1;

    [Required]
    [MaxLength(500)]
    public string JwtSecret { get; set; } = "";

    [Required]
    [MaxLength(50)]
    public string JwtIssuer { get; set; } = "FluxVisualsAuth";

    [Required]
    [MaxLength(50)]
    public string JwtAudience { get; set; } = "FluxVisualsLoader";

    public int JwtExpiryHours { get; set; } = 24;

    public string ModDownloadUrl { get; set; } = "https://github.com/sours3s/FluxVisuals/releases/download/v1.0.17/fluxvisuals-1.0.17.jar";

    public string LoaderDownloadUrl { get; set; } = "https://github.com/sours3s/FluxVisuals/releases/download/v1.0.12-loader/FluxVisualsLoader.exe";
}

public class LaunchTicket
{
    [Key]
    public string Jti { get; set; } = "";

    public int UserId { get; set; }

    [ForeignKey("UserId")]
    public User? User { get; set; }

    [Required]
    [MaxLength(128)]
    public string ChallengeHash { get; set; } = "";

    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}

public class PaymentOrder
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }

    [ForeignKey("UserId")]
    public User? User { get; set; }

    [MaxLength(30)]
    public string PlanId { get; set; } = "";

    public decimal Amount { get; set; }

    [MaxLength(10)]
    public string Currency { get; set; } = "USD";

    /// <summary>pending | paid | cancelled</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "pending";

    /// <summary>Инвойс/счёт на стороне платёжного шлюза.</summary>
    [MaxLength(200)]
    public string? ExternalId { get; set; }

    /// <summary>Сколько дней выдаётся (если не lifetime).</summary>
    public int? GrantedDays { get; set; }

    /// <summary>Пожизненный доступ.</summary>
    public bool Lifetime { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PaidAt { get; set; }
}