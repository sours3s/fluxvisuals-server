using AuthServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthServer.Controllers;

/// <summary>Catal — ИИ-саппорт в лоадере. Ключ ИИ живёт только на сервере.</summary>
[ApiController]
[Route("api/ai")]
public class AiController : ControllerBase
{
    private readonly AiChatService _ai;

    public AiController(AiChatService ai)
    {
        _ai = ai;
    }

    /// <summary>Без секретов и без технических деталей провайдера.</summary>
    [HttpGet("status")]
    [Authorize]
    public IActionResult Status()
    {
        return Ok(new
        {
            available = _ai.Configured,
            name = "Catal"
        });
    }

    [HttpPost("ask")]
    [Authorize]
    public async Task<IActionResult> Ask([FromBody] AiAskRequest req, CancellationToken ct)
    {
        if (!int.TryParse(User.FindFirst("userId")?.Value, out var userId))
            return Unauthorized(new { error = "Не удалось определить пользователя" });

        var result = await _ai.AskAsync(req, userId, ct);
        if (!result.Ok)
            return StatusCode(result.Status, new { error = result.Error });

        return Ok(new { answer = result.Answer });
    }
}
