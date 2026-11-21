using System.Security.Cryptography;
using System.Text;
using AuthServer.Data;
using AuthServer.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Swashbuckle.AspNetCore.SwaggerGen;

var builder = WebApplication.CreateBuilder(args);

// База данных: PostgreSQL если задан DATABASE_URL (Render-бесплатный Postgres),
// иначе локальный SQLite (для разработки/самостоятельного хоста).
var postgresUrl = builder.Configuration["DATABASE_URL"] ?? builder.Configuration["DB:PostgresUrl"];
if (!string.IsNullOrWhiteSpace(postgresUrl))
{
    var pg = ParsePostgresUrl(postgresUrl);
    builder.Services.AddDbContext<AuthDbContext>(opt =>
        opt.UseNpgsql($"Host={pg.host};Port={pg.port};Database={pg.database};Username={pg.user};Password={pg.password};SSL Mode=Require;Trust Server Certificate=true"));
}
else
{
    builder.Services.AddDbContext<AuthDbContext>(opt =>
        opt.UseSqlite("Data Source=auth.db"));
}

builder.Services.Configure<JwtSettings>(opt =>
{
    var cfg = builder.Configuration.GetSection("Jwt");
    opt.Secret = cfg["Secret"] ?? GenerateRandomSecret();
    opt.Issuer = cfg["Issuer"] ?? "FluxVisualsAuth";
    opt.Audience = cfg["Audience"] ?? "FluxVisualsLoader";
    opt.ExpiryHours = int.TryParse(cfg["ExpiryHours"], out var h) ? h : 24;
});

builder.Services.AddSingleton<JwtService>();
builder.Services.AddHttpClient();

// Одноразовые launch-тикеты (защита: мод работает только через лоадер).
builder.Services.Configure<LaunchTicketSettings>(builder.Configuration.GetSection("LaunchTickets"));
builder.Services.AddSingleton<LaunchTicketService>();

// Платёжный шлюз
builder.Services.Configure<CrystalPayOptions>(builder.Configuration.GetSection("Payment:CrystalPay"));
builder.Services.AddSingleton<IPaymentProvider, CrystalPayProvider>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opt =>
    {
        var jwtSettings = builder.Configuration.GetSection("Jwt");
        var secret = jwtSettings["Secret"] ?? GenerateRandomSecret();
        opt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings["Issuer"] ?? "FluxVisualsAuth",
            ValidAudience = jwtSettings["Audience"] ?? "FluxVisualsLoader",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)) { KeyId = "flux-jwt" },
            ClockSkew = TimeSpan.Zero,
            RoleClaimType = "role"
        };
        opt.MapInboundClaims = false;
    });

builder.Services.AddAuthorization(opt =>
{
    opt.AddPolicy("admin", p => p.RequireClaim("role", "admin"));
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Serve static files for admin panel
app.UseDefaultFiles(new DefaultFilesOptions
{
    DefaultFileNames = new List<string> { "index.html" }
});
app.UseStaticFiles();

// Redirect /index.html to / (for root only, not for /admin)
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/index.html")
    {
        context.Response.Redirect("/");
        return;
    }
    await next();
});

// Диагностика запуска: если ключ тикетов не задан — явно пишем в лог (иначе лоадер получит
// «Сервер отклонил выдачу тикета» без понятной причины).
if (string.IsNullOrWhiteSpace(builder.Configuration["LaunchTickets:PrivateKey"]))
    app.Logger.LogError("LaunchTickets:PrivateKey НЕ ЗАДАН. Установи env LaunchTickets__PrivateKey = содержимое flux-private.pem — иначе /api/launch/issue вернёт ошибку.");

// Создаём схему сами. EnsureCreated() нельзя использовать с Supabase: там уже есть
// служебные схемы (auth/storage/extensions), поэтому EF считает БД инициализированной
// и не создаёт наши таблицы — отсюда падение 42P01: relation "Users" does not exist.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    var isPostgres = db.Database.GetDbConnection() is Npgsql.NpgsqlConnection;

    if (isPostgres)
    {
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"Users\" (" +
            "\"Id\" SERIAL PRIMARY KEY, " +
            "\"Uid\" integer NULL, " +
            "\"Username\" text NOT NULL, " +
            "\"PasswordHash\" text NOT NULL, " +
            "\"Hwid\" text NULL, " +
            "\"Role\" text NOT NULL, " +
            "\"AccessExpiresAt\" timestamp with time zone NULL, " +
            "\"IsActive\" boolean NOT NULL, " +
            "\"CreatedAt\" timestamp with time zone NOT NULL, " +
            "\"LastLoginAt\" timestamp with time zone NULL, " +
            "\"HwidLockedAt\" timestamp with time zone NULL)");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"AuthLogs\" (" +
            "\"Id\" SERIAL PRIMARY KEY, " +
            "\"UserId\" integer NULL, " +
            "\"Action\" text NOT NULL, " +
            "\"IpAddress\" text NULL, " +
            "\"Hwid\" text NULL, " +
            "\"Success\" boolean NOT NULL, " +
            "\"CreatedAt\" timestamp with time zone NOT NULL, " +
            "CONSTRAINT \"FK_AuthLogs_Users_UserId\" FOREIGN KEY (\"UserId\") REFERENCES \"Users\"(\"Id\") ON DELETE CASCADE)");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"LaunchTickets\" (" +
            "\"Jti\" text NOT NULL CONSTRAINT \"PK_LaunchTickets\" PRIMARY KEY, " +
            "\"UserId\" integer NOT NULL, " +
            "\"ChallengeHash\" text NOT NULL, " +
            "\"ExpiresAt\" timestamp with time zone NOT NULL, " +
            "\"UsedAt\" timestamp with time zone NULL, " +
            "CONSTRAINT \"FK_LaunchTickets_Users_UserId\" FOREIGN KEY (\"UserId\") REFERENCES \"Users\"(\"Id\") ON DELETE CASCADE)");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"PaymentOrders\" (" +
            "\"Id\" SERIAL PRIMARY KEY, " +
            "\"UserId\" integer NOT NULL, " +
            "\"PlanId\" text NOT NULL, " +
            "\"Amount\" numeric NOT NULL, " +
            "\"Currency\" text NOT NULL, " +
            "\"Status\" text NOT NULL, " +
            "\"ExternalId\" text NULL, " +
            "\"GrantedDays\" integer NULL, " +
            "\"Lifetime\" boolean NOT NULL, " +
            "\"CreatedAt\" timestamp with time zone NOT NULL, " +
            "\"PaidAt\" timestamp with time zone NULL, " +
            "CONSTRAINT \"FK_PaymentOrders_Users_UserId\" FOREIGN KEY (\"UserId\") REFERENCES \"Users\"(\"Id\") ON DELETE CASCADE)");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"AppSettings\" (" +
            "\"Id\" integer NOT NULL CONSTRAINT \"PK_AppSettings\" PRIMARY KEY, " +
            "\"JwtSecret\" text NOT NULL, " +
            "\"JwtIssuer\" text NOT NULL, " +
            "\"JwtAudience\" text NOT NULL, " +
            "\"JwtExpiryHours\" integer NOT NULL, " +
            "\"ModDownloadUrl\" text NULL, " +
            "\"LoaderDownloadUrl\" text NULL)");
    }
    else
    {
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"Users\" (" +
            "\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "\"Uid\" INTEGER NULL, " +
            "\"Username\" TEXT NOT NULL, " +
            "\"PasswordHash\" TEXT NOT NULL, " +
            "\"Hwid\" TEXT NULL, " +
            "\"Role\" TEXT NOT NULL, " +
            "\"AccessExpiresAt\" TEXT NULL, " +
            "\"IsActive\" INTEGER NOT NULL, " +
            "\"CreatedAt\" TEXT NOT NULL, " +
            "\"LastLoginAt\" TEXT NULL, " +
            "\"HwidLockedAt\" TEXT NULL)");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"AuthLogs\" (" +
            "\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "\"UserId\" INTEGER NULL, " +
            "\"Action\" TEXT NOT NULL, " +
            "\"IpAddress\" TEXT NULL, " +
            "\"Hwid\" TEXT NULL, " +
            "\"Success\" INTEGER NOT NULL, " +
            "\"CreatedAt\" TEXT NOT NULL)");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"LaunchTickets\" (" +
            "\"Jti\" TEXT NOT NULL CONSTRAINT \"PK_LaunchTickets\" PRIMARY KEY, " +
            "\"UserId\" INTEGER NOT NULL, " +
            "\"ChallengeHash\" TEXT NOT NULL, " +
            "\"ExpiresAt\" TEXT NOT NULL, " +
            "\"UsedAt\" TEXT NULL)");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"PaymentOrders\" (" +
            "\"Id\" INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "\"UserId\" INTEGER NOT NULL, " +
            "\"PlanId\" TEXT NOT NULL, " +
            "\"Amount\" DECIMAL NOT NULL, " +
            "\"Currency\" TEXT NOT NULL, " +
            "\"Status\" TEXT NOT NULL, " +
            "\"ExternalId\" TEXT NULL, " +
            "\"GrantedDays\" INTEGER NULL, " +
            "\"Lifetime\" INTEGER NOT NULL, " +
            "\"CreatedAt\" TEXT NOT NULL, " +
            "\"PaidAt\" TEXT NULL)");

        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"AppSettings\" (" +
            "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_AppSettings\" PRIMARY KEY, " +
            "\"JwtSecret\" TEXT NOT NULL, " +
            "\"JwtIssuer\" TEXT NOT NULL, " +
            "\"JwtAudience\" TEXT NOT NULL, " +
            "\"JwtExpiryHours\" INTEGER NOT NULL, " +
            "\"ModDownloadUrl\" TEXT NULL, " +
            "\"LoaderDownloadUrl\" TEXT NULL)");
    }

    // Индексы (IF NOT EXISTS защищает повторный запуск на уже существующей БД).
    try { db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Users_Username\" ON \"Users\"(\"Username\")"); } catch { }
    try { db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Users_Uid\" ON \"Users\"(\"Uid\")"); } catch { }
    try { db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_Users_Role\" ON \"Users\"(\"Role\")"); } catch { }

    // Дозаполнение колонок в старых БД (в новых они уже есть — ALTER упадёт, ловим).
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"Users\" ADD COLUMN \"Uid\" INTEGER NULL"); } catch { }
    try { db.Database.ExecuteSqlRaw("ALTER TABLE \"AppSettings\" ADD COLUMN \"LoaderDownloadUrl\" text NULL"); } catch { }
    try { db.Database.ExecuteSqlRaw("UPDATE \"Users\" SET \"Uid\" = \"Id\" WHERE \"Uid\" IS NULL"); } catch { }
    // Клиент стал бесплатным: старые обычные аккаунты тоже получают роль free.
    try { db.Database.ExecuteSqlRaw("UPDATE \"Users\" SET \"Role\" = 'free', \"AccessExpiresAt\" = NULL WHERE \"Role\" = 'user'"); } catch { }

    // Сид настроек: HasData применяется только миграциями, а мы схему Creating вручную.
    if (!await db.AppSettings.AnyAsync())
    {
        db.AppSettings.Add(new AuthServer.Models.AppSettings { Id = 1 });
        await db.SaveChangesAsync();
    }

    var adminPassword = builder.Configuration["BootstrapAdminPassword"]
        ?? Environment.GetEnvironmentVariable("FLUXVISUALS_ADMIN_PASSWORD")
        ?? "sobaka";

    if (!await db.Users.AnyAsync())
    {
        var admin = new AuthServer.Models.User
        {
            Username = "admin",
            Role = "admin",
            IsActive = true,
            AccessExpiresAt = null // пожизненный доступ
        };
        admin.SetPassword(adminPassword);
        db.Users.Add(admin);
        await db.SaveChangesAsync();
    }

    // Аварийный сброс админа: если в конфиге (или env FLUXVISUALS_BOOTSTRAP_ADMIN)
    // указано имя пользователя, он становится админом с пожизненным доступом.
    var bootstrapAdmin = builder.Configuration["BootstrapAdmin"]
        ?? Environment.GetEnvironmentVariable("FLUXVISUALS_BOOTSTRAP_ADMIN");
    if (!string.IsNullOrWhiteSpace(bootstrapAdmin))
    {
        var target = await db.Users.FirstOrDefaultAsync(u => u.Username == bootstrapAdmin);
        if (target != null)
        {
            target.Role = "admin";
            target.IsActive = true;
            target.AccessExpiresAt = null;
            // Раньше сид создавал admin/admin123 — обновляем пароль на нужный владельцу.
            if (target.VerifyPassword("admin123"))
                target.SetPassword(adminPassword);
            await db.SaveChangesAsync();
        }
    }
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Админ-панель: любой путь под /admin → admin/index.html
app.MapGet("/admin/{**path}", async (HttpContext ctx) =>
{
    var file = Path.Combine(app.Environment.WebRootPath, "admin", "index.html");
    if (File.Exists(file))
    {
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.SendFileAsync(file);
    }
    else
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }
});

// Лендинг для остальных неизвестных путей
app.MapFallbackToFile("index.html");

app.Run();

static string GenerateRandomSecret()
{
    var bytes = new byte[64];
    using var rng = RandomNumberGenerator.Create();
    rng.GetBytes(bytes);
    return Convert.ToBase64String(bytes);
}

// Разбор строки вида: postgres://user:pass@host:port/database
static (string host, string port, string database, string user, string password) ParsePostgresUrl(string url)
{
    var u = new Uri(url);
    var user = Uri.UnescapeDataString(u.UserInfo.Split(':')[0]);
    var pass = u.UserInfo.Contains(':') ? Uri.UnescapeDataString(u.UserInfo.Split(':', 2)[1]) : "";
    return (u.Host, u.Port.ToString(), u.AbsolutePath.TrimStart('/'), user, pass);
}