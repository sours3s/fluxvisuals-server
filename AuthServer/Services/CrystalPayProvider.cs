using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthServer.Models;
using Microsoft.Extensions.Options;

namespace AuthServer.Services;

public class CrystalPayOptions
{
    public string MerchantId { get; set; } = "";   // auth_login (логин кассы)
    public string Secret { get; set; } = "";       // auth_secret (секретный ключ кассы)
    public string Salt { get; set; } = "";         // соль для подписи коллбэков
}

/// <summary>
/// Платёжный шлюз CrystalPay (криптовалютный агрегатор).
/// Документация: POST /v3/invoice/create/, авторизация auth_login + auth_secret в теле,
/// коллбэк с подписью sha1(id:salt).
/// </summary>
public class CrystalPayProvider : IPaymentProvider
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly CrystalPayOptions _opts;

    public CrystalPayProvider(IOptions<CrystalPayOptions> opts)
    {
        _opts = opts.Value;
    }

    public string Name => "crystalpay";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opts.MerchantId) && !string.IsNullOrWhiteSpace(_opts.Secret);

    public async Task<CreateInvoiceResult> CreateInvoiceAsync(PaymentOrder order, string callbackUrl, string redirectUrl, CancellationToken ct)
    {
        if (!IsConfigured)
            return new CreateInvoiceResult(false, "Платёжный шлюз не настроен", null, null, order.Amount, order.Currency);

        var payload = new Dictionary<string, object?>
        {
            ["auth_login"] = _opts.MerchantId,
            ["auth_secret"] = _opts.Secret,
            ["amount"] = order.Amount,
            ["currency"] = order.Currency,
            ["type"] = "purchase",
            ["lifetime"] = 60, // минут на оплату
            ["subtract_from"] = "amount",
            ["description"] = $"FluxVisuals — {order.PlanId}",
            ["callback_url"] = callbackUrl,
            ["redirect_url"] = redirectUrl,
            ["extra"] = order.Id.ToString(),
        };

        var resp = await _http.PostAsync("https://api.crystalpay.io/v3/invoice/create/",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return new CreateInvoiceResult(false, $"CrystalPay: {(int)resp.StatusCode} {Truncate(json, 200)}", null, null, order.Amount, order.Currency);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        string? url = root.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
            return new CreateInvoiceResult(false, $"CrystalPay: неожиданный ответ {Truncate(json, 200)}", null, null, order.Amount, order.Currency);

        return new CreateInvoiceResult(true, null, id, url, order.Amount, order.Currency);
    }

    public bool VerifyCallback(PaymentCallback callback)
    {
        if (string.IsNullOrWhiteSpace(callback.Signature)) return false;
        // Подпись = sha1( externalId : salt )
        var expected = Sha1Hex($"{callback.ExternalId}:{_opts.Salt}");
        return string.Equals(expected, callback.Signature, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha1Hex(string input)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];
}
