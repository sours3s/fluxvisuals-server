using System.Security.Cryptography;
using System.Text;
using AuthServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AuthServer.Controllers;

/// <summary>Общий секрет для Discord-ботов. Ключ ИИ и токены ботов здесь не хранятся.</summary>
public sealed class BotGatewayOptions
{
    public string SharedSecret { get; set; } = "";
}

/// <summary>Шлюз Catal для Discord-ботов: не требует аккаунта FluxVisuals,
/// но требует общий секрет, известный только серверу и ботам.</summary>
[ApiController]
[Route("api/bot")]
public class BotController : ControllerBase
{
    private readonly AiChatService _ai;
    private readonly byte[] _secret;

    public BotController(AiChatService ai, IOptions<BotGatewayOptions> options)
    {
        _ai = ai;
        _secret = Encoding.UTF8.GetBytes(options.Value.SharedSecret ?? "");
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        if (!Authorized) return Unauthorized(new { error = "Bot gateway is not configured" });
        return Ok(new { available = _ai.Configured, name = "Catal" });
    }

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AiAskRequest req, CancellationToken ct)
    {
        if (!Authorized) return Unauthorized(new { error = "Bot gateway is not configured" });

        // Стабильный ID пользователя Discord нужен только для rate limit в AiChatService.
        string raw = Request.Headers["X-Client-User-Id"].FirstOrDefault() ?? "";
        if (!long.TryParse(raw, out var externalId)) externalId = 0;
        int userId = (int)(Math.Abs(externalId) % int.MaxValue);
        if (userId == 0) userId = 1;

        var result = await _ai.AskAsync(req, userId, ct);
        if (!result.Ok)
            return StatusCode(result.Status, new { error = result.Error });

        return Ok(new { answer = result.Answer });
    }

    private bool Authorized
    {
        get
        {
            if (_secret.Length == 0) return false;
            string provided = Request.Headers["X-Bot-Token"].ToString();
            if (string.IsNullOrEmpty(provided)) return false;
            return CryptographicOperations.FixedTimeEquals(
                _secret, Encoding.UTF8.GetBytes(provided));
        }
    }
}
