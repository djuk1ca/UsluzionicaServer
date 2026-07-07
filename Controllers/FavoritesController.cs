using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UsluzionicaServer.DTOs.Favorites;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Controllers;

[ApiController]
[Route("api/favorites")]
[Authorize]
public sealed class FavoritesController(FavoriteService favoriteService) : ControllerBase
{
    // ── POST /api/favorites/listings/{id} ─────────────────────────────────
    /// <summary>
    /// Toggle: dodaje oglas u omiljene ako nije tamo, uklanja ga ako već jeste.
    /// Vraća { isFavorited: true/false }.
    /// </summary>
    [HttpPost("listings/{id:int}")]
    [ProducesResponseType(typeof(FavoriteStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleListing(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (status, error) = await favoriteService.ToggleListingAsync(userId, id);

        if (error is not null)
            return NotFound(new { success = false, message = error });

        return Ok(new { success = true, data = status });
    }

    // ── POST /api/favorites/providers/{id} ────────────────────────────────
    /// <summary>
    /// Toggle: dodaje uslugodavca u omiljene ako nije tamo, uklanja ga ako već jeste.
    /// Vraća { isFavorited: true/false }.
    /// </summary>
    [HttpPost("providers/{id:int}")]
    [ProducesResponseType(typeof(FavoriteStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ToggleProvider(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var (status, error) = await favoriteService.ToggleProviderAsync(userId, id);

        if (error is not null)
            return NotFound(new { success = false, message = error });

        return Ok(new { success = true, data = status });
    }

    // ── GET /api/favorites/listings ───────────────────────────────────────
    /// <summary>
    /// Lista omiljenih oglasa prijavljenog korisnika.
    /// Sortirano od najnovije sačuvanog. Bez paginacije — lista je lična.
    /// </summary>
    [HttpGet("listings")]
    [ProducesResponseType(typeof(List<FavoriteListingDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFavoriteListings()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var items  = await favoriteService.GetFavoriteListingsAsync(userId);
        return Ok(new { success = true, data = items, total = items.Count });
    }

    // ── GET /api/favorites/providers ──────────────────────────────────────
    /// <summary>
    /// Lista omiljenih uslugodavaca prijavljenog korisnika.
    /// Sortirano od najnovije sačuvanog.
    /// </summary>
    [HttpGet("providers")]
    [ProducesResponseType(typeof(List<FavoriteProviderDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFavoriteProviders()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var items  = await favoriteService.GetFavoriteProvidersAsync(userId);
        return Ok(new { success = true, data = items, total = items.Count });
    }

    // ── GET /api/favorites/listings/{id}/status ───────────────────────────
    /// <summary>
    /// Proverava da li je korisnik označio oglas kao omiljeni.
    /// Koristiti za inicijalni render srca na kartici oglasa.
    /// </summary>
    [HttpGet("listings/{id:int}/status")]
    [ProducesResponseType(typeof(FavoriteStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetListingStatus(int id)
    {
        var userId      = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var isFavorited = await favoriteService.IsListingFavoritedAsync(userId, id);
        return Ok(new { success = true, data = new FavoriteStatusDto { IsFavorited = isFavorited } });
    }

    // ── GET /api/favorites/providers/{id}/status ──────────────────────────
    /// <summary>
    /// Proverava da li je korisnik označio uslugodavca kao omiljenog.
    /// </summary>
    [HttpGet("providers/{id:int}/status")]
    [ProducesResponseType(typeof(FavoriteStatusDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProviderStatus(int id)
    {
        var userId      = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var isFavorited = await favoriteService.IsProviderFavoritedAsync(userId, id);
        return Ok(new { success = true, data = new FavoriteStatusDto { IsFavorited = isFavorited } });
    }
}
