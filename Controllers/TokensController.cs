using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/tokens")]
[Authorize]
public sealed class TokensController(TokenWalletService walletService) : ControllerBase
{
    // ── GET /api/tokens/balance ───────────────────────────────────────────
    /// <summary>Trenutni token balans prijavljenog korisnika.</summary>
    [HttpGet("balance")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetBalance()
    {
        var userId  = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var balance = await walletService.GetBalanceAsync(userId);

        if (balance is null)
            return NotFound(new { success = false, message = "Korisnik nije pronađen." });

        return Ok(new { success = true, data = balance });
    }

    // ── GET /api/tokens/transactions ──────────────────────────────────────
    /// <summary>
    /// Paginovani ledger svih token transakcija.
    /// Sortiran od najnovije. Query: ?page=1&amp;pageSize=20
    /// </summary>
    [HttpGet("transactions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTransactions(
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var result = await walletService.GetTransactionsAsync(userId, page, pageSize);

        return Ok(new { success = true, data = result });
    }
}
