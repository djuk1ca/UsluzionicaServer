using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.DTOs.Conversations;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/conversations")]
[Authorize]
public sealed class ConversationsController(ConversationService conversationService) : ControllerBase
{
    // ── GET /api/conversations ────────────────────────────────────────────
    /// <summary>
    /// Lista svih konverzacija prijavljenog korisnika.
    /// Sortirana od najnovije aktivnosti.
    /// Uključuje: ime i avatar drugog korisnika, preview zadnje poruke,
    /// broj nepročitanih poruka, online status.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var list   = await conversationService.GetConversationsAsync(userId);
        return Ok(new { success = true, data = list, total = list.Count });
    }

    // ── POST /api/conversations ───────────────────────────────────────────
    /// <summary>
    /// Otvara konverzaciju sa datim korisnikom.
    /// Ako konverzacija već postoji — vraća nju (ne kreira duplikat).
    /// Body: { "receiverId": "user-guid" }
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ConversationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] CreateConversationDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (result, error) = await conversationService.GetOrCreateAsync(userId, dto.ReceiverId);

        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return Ok(new { success = true, data = result });
    }

    // ── GET /api/conversations/{id}/messages ──────────────────────────────
    /// <summary>
    /// Istorija poruka date konverzacije (hronološki, najstarije prve).
    /// Tekst svake poruke je dekriptovan.
    /// Query: ?page=1&amp;pageSize=50
    /// </summary>
    [HttpGet("{id:int}/messages")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMessages(
        int id,
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 50)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (messages, error) = await conversationService.GetMessagesAsync(id, userId, page, pageSize);

        if (error is not null)
            return NotFound(new { success = false, message = error });

        return Ok(new { success = true, data = messages, total = messages!.Count });
    }

    // ── POST /api/conversations/{id}/messages ─────────────────────────────
    /// <summary>
    /// REST fallback za slanje poruke (ako SignalR nije dostupan).
    /// Enkriptuje tekst, snima u bazu, vraća kreiran MessageDto.
    /// Preporučeno: koristiti SignalR hub umesto ovog endpointa.
    /// </summary>
    [HttpPost("{id:int}/messages")]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SendMessage(int id, [FromBody] SendMessageDto dto)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (message, error) = await conversationService.SendMessageAsync(id, userId, dto.Text);

        if (error is not null)
            return BadRequest(new { success = false, message = error });

        return StatusCode(StatusCodes.Status201Created,
            new { success = true, data = message });
    }

    // ── PATCH /api/conversations/{id}/read ────────────────────────────────
    /// <summary>
    /// Označava sve primljene poruke u konverzaciji kao pročitane.
    /// Resetuje UnreadCount na nulu za ovu konverzaciju.
    /// </summary>
    [HttpPatch("{id:int}/read")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (success, error) = await conversationService.MarkAsReadAsync(id, userId);

        if (!success)
            return NotFound(new { success = false, message = error });

        return Ok(new { success = true, message = "Poruke označene kao pročitane." });
    }
}
