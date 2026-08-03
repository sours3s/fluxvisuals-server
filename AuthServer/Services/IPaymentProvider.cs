using AuthServer.Models;

namespace AuthServer.Services;

/// <summary>Результат создания инвойса в платёжном шлюзе.</summary>
public record CreateInvoiceResult(
    bool Success,
    string? Error,
    string? ExternalId,
    string? PayUrl,
    decimal Amount,
    string Currency);

/// <summary>Входящий вебхук от шлюза.</summary>
public record PaymentCallback(
    string ExternalId,
    string State,        // "payed"/"paid" и т.п.
    string? Signature,
    decimal? Amount,
    string? Currency);

/// <summary>Абстракция платёжного шлюза. Реализации: CrystalPay (по умолчанию).</summary>
public interface IPaymentProvider
{
    string Name { get; }

    /// <summary>Ключи заданы в конфиге — шлюз можно использовать.</summary>
    bool IsConfigured { get; }

    /// <summary>Создать инвойс/счёт для заказа.</summary>
    Task<CreateInvoiceResult> CreateInvoiceAsync(PaymentOrder order, string callbackUrl, string redirectUrl, CancellationToken ct);

    /// <summary>Проверить подпись вебхука. True — подпись валидна.</summary>
    bool VerifyCallback(PaymentCallback callback);
}
