using System.Text.Json;
using AuthServer.Data;
using AuthServer.Models;
using AuthServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AuthServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PaymentController : ControllerBase
{
    private readonly AuthDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IPaymentProvider _provider;

    public PaymentController(AuthDbContext db, IConfiguration configuration, IPaymentProvider provider)
    {
        _db = db;
        _configuration = configuration;
        _provider = provider;
    }

    private async Task<User?> CurrentUser()
    {
        var idClaim = User.FindFirst("userId")?.Value;
        if (!int.TryParse(idClaim, out var userId)) return null;
        return await _db.Users.FindAsync(userId);
    }

    /// <summary>Создать заказ на покупку тарифа.</summary>
    [HttpPost("create")]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] CreatePaymentRequest req)
    {
        if (!_provider.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Оплата не настроена" });

        var user = await CurrentUser();
        if (user == null || !user.IsActive) return Unauthorized();

        var plan = (_configuration.GetSection("Plans").Get<List<PlanConfig>>() ?? new())
            .FirstOrDefault(p => p.Id == req.PlanId);
        if (plan == null) return BadRequest(new { error = "Неизвестный тариф" });

        // Уже клиент с пожизненным доступом — покупать не надо
        if (user.Role == "admin") return BadRequest(new { error = "Админу покупка не нужна" });
        if (user.Role == "client" && user.AccessExpiresAt == null)
            return BadRequest(new { error = "У вас уже пожизненный доступ" });

        var order = new PaymentOrder
        {
            UserId = user.Id,
            PlanId = plan.Id,
            Amount = plan.Price,
            Currency = plan.Currency,
            Status = "pending",
            GrantedDays = plan.Lifetime ? null : plan.Days,
            Lifetime = plan.Lifetime
        };
        _db.PaymentOrders.Add(order);
        await _db.SaveChangesAsync();

        var callbackUrl = $"{Request.Scheme}://{Request.Host}/api/payment/webhook";
        var redirectUrl = $"{Request.Scheme}://{Request.Host}/account.html";

        var result = await _provider.CreateInvoiceAsync(order, callbackUrl, redirectUrl, HttpContext.RequestAborted);
        if (!result.Success)
        {
            order.Status = "cancelled";
            await _db.SaveChangesAsync();
            return StatusCode(StatusCodes.Status502BadGateway, new { error = result.Error ?? "Не удалось создать платёж" });
        }

        order.ExternalId = result.ExternalId;
        await _db.SaveChangesAsync();

        return Ok(new
        {
            orderId = order.Id,
            payUrl = result.PayUrl,
            amount = result.Amount,
            currency = result.Currency,
            plan = new { plan.Id, plan.Name }
        });
    }

    /// <summary>Статус заказа (для фронта, пока юзер платит).</summary>
    [HttpGet("status/{id}")]
    [Authorize]
    public async Task<IActionResult> Status(int id)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        var order = await _db.PaymentOrders.FindAsync(id);
        if (order == null || order.UserId != user.Id) return NotFound();

        return Ok(new { orderId = order.Id, status = order.Status, createdAt = order.CreatedAt });
    }

    /// <summary>Вебхук от платёжного шлюза: подтверждение оплаты → выдача доступа.</summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        // CrystalPay шлёт JSON: { id, state, signature, amount, ... }
        PaymentCallback? callback;
        try
        {
            using var doc = JsonDocument.Parse(await new StreamReader(Request.Body).ReadToEndAsync(ct));
            var root = doc.RootElement;
            callback = new PaymentCallback(
                ExternalId: root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "",
                State: root.TryGetProperty("state", out var stEl) ? stEl.GetString() ?? "" : "",
                Signature: root.TryGetProperty("signature", out var sigEl) ? sigEl.GetString() : null,
                Amount: root.TryGetProperty("amount", out var amtEl) && amtEl.ValueKind == JsonValueKind.Number ? amtEl.GetDecimal() : null,
                Currency: root.TryGetProperty("currency", out var curEl) ? curEl.GetString() : null);
        }
        catch (Exception)
        {
            return BadRequest(new { error = "Bad webhook payload" });
        }

        if (string.IsNullOrWhiteSpace(callback.ExternalId))
            return BadRequest(new { error = "Missing invoice id" });

        if (!_provider.VerifyCallback(callback))
            return Unauthorized(new { error = "Bad signature" });

        var order = await _db.PaymentOrders.FirstOrDefaultAsync(o => o.ExternalId == callback.ExternalId, ct);
        if (order == null)
            return NotFound(new { error = "Order not found" });

        // "payed"/"success" — оплачен
        bool paid = callback.State.Equals("payed", StringComparison.OrdinalIgnoreCase)
                    || callback.State.Equals("paid", StringComparison.OrdinalIgnoreCase)
                    || callback.State.Equals("success", StringComparison.OrdinalIgnoreCase);

        if (paid && order.Status != "paid")
        {
            order.Status = "paid";
            order.PaidAt = DateTime.UtcNow;

            var user = await _db.Users.FindAsync(order.UserId, ct);
            if (user != null)
            {
                if (user.Role != "admin") user.Role = "client"; // админа не сбрасываем
                var now = DateTime.UtcNow;
                if (order.Lifetime)
                {
                    user.AccessExpiresAt = null; // пожизненно
                }
                else
                {
                    var baseTime = user.AccessExpiresAt != null && user.AccessExpiresAt > now ? user.AccessExpiresAt.Value : now;
                    user.AccessExpiresAt = baseTime.AddDays(order.GrantedDays ?? 0);
                }
            }

            await _db.SaveChangesAsync(ct);
            return Ok(new { ok = true });
        }

        return Ok(new { ok = true, ignored = order.Status });
    }
}

public record CreatePaymentRequest(string PlanId);
