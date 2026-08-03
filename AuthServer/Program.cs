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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
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
app.UseDefaultFiles();
app.UseStaticFiles();

// Ensure database created and seed admin
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    db.Database.EnsureCreated();

    if (!await db.Users.AnyAsync())
    {
        var admin = new AuthServer.Models.User
        {
            Username = "admin",
            Role = "admin",
            IsActive = true
        };
        admin.SetPassword("admin123"); // Change on first login!
        db.Users.Add(admin);
        await db.SaveChangesAsync();
    }

    // Аварийный сброс админа: если в конфиге (или env FLUXVISUALS_BOOTSTRAP_ADMIN)
    // указано имя пользователя, он становится админом при старте.
    // Нужно, если роль случайно сбита на client (например, старым багом выдачи доступа).
    var bootstrapAdmin = builder.Configuration["BootstrapAdmin"]
        ?? Environment.GetEnvironmentVariable("FLUXVISUALS_BOOTSTRAP_ADMIN");
    if (!string.IsNullOrWhiteSpace(bootstrapAdmin))
    {
        var target = await db.Users.FirstOrDefaultAsync(u => u.Username == bootstrapAdmin);
        if (target != null)
        {
            target.Role = "admin";
            target.IsActive = true;
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