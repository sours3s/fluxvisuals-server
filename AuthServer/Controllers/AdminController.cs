using AuthServer.Data;
using AuthServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuthServer.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "admin")]
public class AdminController : ControllerBase
{
    private readonly AuthDbContext _db;

    public AdminController(AuthDbContext db)
    {
        _db = db;
    }

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers()
    {
        var now = DateTime.UtcNow;
        var users = await _db.Users
            .OrderBy(u => u.Id)
            .Select(u => new
            {
                u.Id,
                u.Uid,
                u.Username,
                u.Role,
                u.IsActive,
                u.AccessExpiresAt,
                HasAccess = u.Role != "user" && u.IsActive && (u.AccessExpiresAt == null || u.AccessExpiresAt > now),
                u.CreatedAt,
                u.LastLoginAt,
                u.Hwid,
                u.HwidLockedAt
            })
            .ToListAsync();
        return Ok(users);
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "Username and password required" });

        if (await _db.Users.AnyAsync(u => u.Username == req.Username))
            return Conflict(new { error = "Username already exists" });

        var role = NormalizeRole(req.Role);
        var user = new User { Username = req.Username, Role = role };
        user.SetPassword(req.Password);

        if (req.Uid.HasValue)
        {
            if (req.Uid.Value < 1)
                return BadRequest(new { error = "UID должен быть не меньше 1" });
            if (await _db.Users.AnyAsync(u => u.Uid == req.Uid.Value))
                return Conflict(new { error = "Этот UID уже занят" });
            user.Uid = req.Uid.Value;
        }
        else
        {
            user.Uid = (await _db.Users.MaxAsync(u => (int?)u.Uid) ?? 0) + 1;
        }

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return Ok(new { id = user.Id, uid = user.Uid, username = user.Username, role });
    }

    [HttpPut("users/{id}")]
    public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserRequest req)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(req.Password))
            user.SetPassword(req.Password);

        if (!string.IsNullOrWhiteSpace(req.Role))
            user.Role = NormalizeRole(req.Role);

        if (req.Uid.HasValue)
        {
            if (req.Uid.Value < 1)
                return BadRequest(new { error = "UID должен быть не меньше 1" });
            if (await _db.Users.AnyAsync(u => u.Uid == req.Uid.Value && u.Id != id))
                return Conflict(new { error = "Этот UID уже занят другим юзером" });
            user.Uid = req.Uid.Value;
        }

        user.IsActive = req.IsActive;

        // Выдача доступа. Роль "client" получает только тот, кто ещё не админ:
        // иначе выдача доступа админу сбросит его роль на client и он потеряет админку.
        if (req.GrantDays is > 0)
        {
            if (user.Role != "admin") user.Role = "client";
            var now = DateTime.UtcNow;
            var baseTime = user.AccessExpiresAt != null && user.AccessExpiresAt > now ? user.AccessExpiresAt.Value : now;
            user.AccessExpiresAt = baseTime.AddDays(req.GrantDays.Value);
        }
        if (req.SetLifetime)
        {
            if (user.Role != "admin") user.Role = "client";
            user.AccessExpiresAt = null; // пожизненно
        }
        if (req.ClearAccess)
        {
            if (user.Role == "client") user.Role = "user";
            user.AccessExpiresAt = null;
        }

        if (req.ResetHwid)
            user.Hwid = null;

        await _db.SaveChangesAsync();
        return Ok(new { id = user.Id, username = user.Username, role = user.Role, accessExpiresAt = user.AccessExpiresAt });
    }

    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return NotFound();

        // Сначала связанные записи — иначе FK-нарушение при удалении → 500
        var orders = await _db.PaymentOrders.Where(o => o.UserId == id).ToListAsync();
        if (orders.Count > 0) _db.PaymentOrders.RemoveRange(orders);

        var logs = await _db.AuthLogs.Where(l => l.UserId == id).ToListAsync();
        if (logs.Count > 0) _db.AuthLogs.RemoveRange(logs);

        _db.Users.Remove(user);
        await _db.SaveChangesAsync();
        return Ok();
    }

    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] int limit = 100)
    {
        var logs = await _db.AuthLogs
            .OrderByDescending(l => l.CreatedAt)
            .Take(limit)
            .Select(l => new
            {
                l.Id,
                l.UserId,
                Username = l.User != null ? l.User.Username : "Unknown",
                l.Action,
                l.IpAddress,
                l.Hwid,
                l.Success,
                l.CreatedAt
            })
            .ToListAsync();
        return Ok(logs);
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var now = DateTime.UtcNow;
        return Ok(new
        {
            totalUsers = await _db.Users.CountAsync(),
            activeUsers = await _db.Users.CountAsync(u => u.IsActive),
            admins = await _db.Users.CountAsync(u => u.Role == "admin"),
            clients = await _db.Users.CountAsync(u => u.Role == "client"),
            users = await _db.Users.CountAsync(u => u.Role == "user"),
            clientsWithAccess = await _db.Users.CountAsync(u => u.Role == "client" && u.IsActive && (u.AccessExpiresAt == null || u.AccessExpiresAt > now)),
            expiredClients = await _db.Users.CountAsync(u => u.Role == "client" && u.AccessExpiresAt != null && u.AccessExpiresAt <= now),
            hwidLocked = await _db.Users.CountAsync(u => !string.IsNullOrEmpty(u.Hwid)),
            recentLogins = await _db.AuthLogs.CountAsync(l => l.Action == "login" && l.Success && l.CreatedAt > now.AddDays(-7))
        });
    }

    /// <summary>Одноразовая операция (админ): перенумеровывает ID и UID всех юзеров по порядку
    /// регистрации (создания). UID = новый ID. Связи в AuthLogs/LaunchTickets/PaymentOrders
    /// переносятся, автоинкремент сбрасывается. Работает на PostgreSQL и SQLite.</summary>
    [HttpPost("reindex-users")]
    public async Task<IActionResult> ReindexUsers()
    {
        var users = await _db.Users.OrderBy(u => u.CreatedAt).ThenBy(u => u.Id).ToListAsync();
        if (users.Count == 0) return Ok(new { reindexed = 0 });

        // Нумерация по порядку создания ВСЕХ аккаунтов (админ = первый, если создан первым).
        var final = new Dictionary<int, int>(); // oldId -> newId
        int next = 1;
        foreach (var u in users) final[u.Id] = next++;
        int maxNew = final.Values.Max();

        var conn = _db.Database.GetDbConnection();
        bool isPostgres = conn is Npgsql.NpgsqlConnection;
        bool isSqlite = conn is Microsoft.Data.Sqlite.SqliteConnection;

        // SQLite: PRAGMA foreign_keys нельзя менять внутри транзакции — выключаем до её начала.
        if (isSqlite)
        {
            if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=OFF";
            await pragma.ExecuteNonQueryAsync();
        }

        List<(string Table, string Name, string Def)> fks = new();
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            if (isPostgres)
            {
                if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
                using (var q = conn.CreateCommand())
                {
                    q.Transaction = tx.GetDbTransaction();
                    q.CommandText = "SELECT rel.relname, c.conname, pg_get_constraintdef(c.oid) " +
                        "FROM pg_constraint c " +
                        "JOIN pg_class rel ON rel.oid = c.conrelid " +
                        "JOIN pg_class ref ON ref.oid = c.confrelid " +
                        "WHERE c.contype = 'f' AND ref.relname = 'Users'";
                    using var r = await q.ExecuteReaderAsync();
                    while (await r.ReadAsync()) fks.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
                }
                foreach (var (t, n, _) in fks)
                {
                    using var c = conn.CreateCommand();
                    c.Transaction = tx.GetDbTransaction();
                    c.CommandText = $"ALTER TABLE \"{t}\" DROP CONSTRAINT \"{n}\"";
                    await c.ExecuteNonQueryAsync();
                }
            }

            // Фаза 1: временные ID (+1_000_000) и UID=NULL, чтобы не было коллизий ни PK,
            // ни уникального индекса на Uid. Всё через сырой SQL — EF запрещает менять PK.
            var temp = new Dictionary<int, int>();
            foreach (var u in users) temp[u.Id] = u.Id + 1_000_000;
            foreach (var (old, t) in temp)
                await ExecAsync(conn, tx.GetDbTransaction(), $"UPDATE \"Users\" SET \"Id\" = {t}, \"Uid\" = NULL WHERE \"Id\" = {old}");
            foreach (var (old, t) in temp)
            {
                await ExecAsync(conn, tx.GetDbTransaction(), $"UPDATE \"AuthLogs\" SET \"UserId\" = {t} WHERE \"UserId\" = {old}");
                await ExecAsync(conn, tx.GetDbTransaction(), $"UPDATE \"LaunchTickets\" SET \"UserId\" = {t} WHERE \"UserId\" = {old}");
                await ExecAsync(conn, tx.GetDbTransaction(), $"UPDATE \"PaymentOrders\" SET \"UserId\" = {t} WHERE \"UserId\" = {old}");
            }

            // Фаза 2: финальные ID + UID (UID = новый ID для всех).
            foreach (var u in users)
            {
                int t = temp[u.Id];
                int nid = final[u.Id];
                await ExecAsync(conn, tx.GetDbTransaction(), $"UPDATE \"Users\" SET \"Id\" = {nid}, \"Uid\" = {nid} WHERE \"Id\" = {t}");
                await ExecAsync(conn, tx.GetDbTransaction(), $"UPDATE \"AuthLogs\" SET \"UserId\" = {nid} WHERE \"UserId\" = {t}");
                await ExecAsync(conn, tx.GetDbTransaction(), $"UPDATE \"LaunchTickets\" SET \"UserId\" = {nid} WHERE \"UserId\" = {t}");
                await ExecAsync(conn, tx.GetDbTransaction(), $"UPDATE \"PaymentOrders\" SET \"UserId\" = {nid} WHERE \"UserId\" = {t}");
            }

            // Сброс автоинкремента.
            if (isPostgres)
            {
                using var c = conn.CreateCommand();
                c.Transaction = tx.GetDbTransaction();
                c.CommandText = $"ALTER TABLE \"Users\" ALTER COLUMN \"Id\" RESTART WITH {maxNew + 1}";
                await c.ExecuteNonQueryAsync();
            }
            else if (isSqlite)
            {
                try
                {
                    using var c = conn.CreateCommand();
                    c.Transaction = tx.GetDbTransaction();
                    c.CommandText = $"UPDATE sqlite_sequence SET seq = {maxNew} WHERE name = 'Users'";
                    await c.ExecuteNonQueryAsync();
                }
                catch { /* нет таблицы sqlite_sequence — не страшно */ }
            }

            // Возвращаем FK-ограничения (Postgres).
            if (isPostgres)
            {
                foreach (var (t, n, def) in fks)
                {
                    using var c = conn.CreateCommand();
                    c.Transaction = tx.GetDbTransaction();
                    c.CommandText = $"ALTER TABLE \"{t}\" ADD CONSTRAINT \"{n}\" {def}";
                    await c.ExecuteNonQueryAsync();
                }
            }

            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            try { await tx.RollbackAsync(); } catch { }
            return StatusCode(500, new { error = ex.Message });
        }
        finally
        {
            if (isSqlite)
            {
                try
                {
                    using var on = conn.CreateCommand();
                    on.CommandText = "PRAGMA foreign_keys=ON";
                    await on.ExecuteNonQueryAsync();
                }
                catch { }
            }
        }

        return Ok(new { reindexed = users.Count });
    }

    private static async Task ExecAsync(System.Data.Common.DbConnection conn,
        System.Data.Common.DbTransaction tx, string sql)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        await c.ExecuteNonQueryAsync();
    }

    private static string NormalizeRole(string? role)
    {
        var r = (role ?? "user").Trim().ToLowerInvariant();
        return r is "admin" or "client" or "user" ? r : "user";
    }
}

public record CreateUserRequest(string Username, string Password, string? Role = null, int? Uid = null);
public record UpdateUserRequest(string? Password = null, string? Role = null, bool IsActive = true,
    int? GrantDays = null, bool SetLifetime = false, bool ClearAccess = false, bool ResetHwid = false, int? Uid = null);
