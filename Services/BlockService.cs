using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Moderation;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Blokiranje korisnika.
///
/// SIMETRIJA JE CELA POENTA. Red u bazi je usmeren (ko koga je blokirao), ali
/// svaka provera gleda oba smera. Ako A blokira B:
///
///   • A ne vidi B-ove oglase, profil, recenzije ni poruke
///   • B ne vidi A-ove — iako B ništa nije uradio
///   • nijedan ne može da piše onom drugom
///
/// Druga stavka je ta koja se lako preskoči, a bez nje blokada ne znači ništa:
/// osoba od koje se korisnik sklanja i dalje bi mu gledala oglase i pisala.
///
/// ŠTA SE NE DIRA: već obavljene rezervacije, isplaćeni tokeni i istorija
/// transakcija. Blokada je socijalna mera, ne brisanje prošlosti — ako se
/// zabrani pristup istoriji, prvi spor oko novca ostaje bez traga.
/// </summary>
public sealed class BlockService(
    AppDbContext          db,
    ILogger<BlockService> logger)
{
    // ── UPITNI POMOĆNICI ───────────────────────────────────────────────────

    /// <summary>
    /// Filtrira oglase blokiranih iz bilo kog upita nad <c>Listings</c>.
    ///
    /// Koristi se na svakom mestu koje vraća oglase — pretraga, omiljeni,
    /// profil uslugodavca. Jedan metod, da se pravilo ne prepisuje po servisima
    /// i ne raziđe.
    ///
    /// NOT EXISTS, a ne učitavanje skupa blokiranih u memoriju pa Contains():
    /// Contains pravi IN (@p1, @p2, ...) što radi dok je lista kratka, a puca
    /// kad neko blokira stotine ljudi — SQL Server ima granicu broja parametara,
    /// a plan upita postane neupotrebljiv. NOT EXISTS ne zavisi od veličine i
    /// koristi oba indeksa iz AppDbContext.
    /// </summary>
    public static IQueryable<Listing> FilterBlocked(
        IQueryable<Listing> query, AppDbContext db, string? viewerId)
    {
        // Anoniman posetilac nema koga da blokira niti ko njega. Bez ovog
        // izlaza bi se svakoj pretrazi bez prijave dodavao beskoristan NOT EXISTS.
        if (string.IsNullOrEmpty(viewerId))
            return query;

        return query.Where(l => !db.UserBlocks.Any(ub =>
            (ub.BlockerId == viewerId && ub.BlockedId == l.ProviderProfile.UserId) ||
            (ub.BlockedId == viewerId && ub.BlockerId == l.ProviderProfile.UserId)));
    }

    /// <summary>
    /// Postoji li blokada između dva korisnika, u bilo kom smeru.
    ///
    /// Za pojedinačne provere: slanje poruke, otvaranje razgovora, pisanje
    /// recenzije, rezervacija.
    /// </summary>
    public async Task<bool> JeBlokiranoAsync(string userA, string userB)
    {
        if (string.IsNullOrEmpty(userA) || string.IsNullOrEmpty(userB))
            return false;

        return await db.UserBlocks.AnyAsync(ub =>
            (ub.BlockerId == userA && ub.BlockedId == userB) ||
            (ub.BlockerId == userB && ub.BlockedId == userA));
    }

    /// <summary>
    /// Svi korisnici sa kojima je <paramref name="userId"/> u blokadi, u bilo
    /// kom smeru.
    ///
    /// Za filtriranje lista koje su VEĆ učitane u memoriju (razgovori,
    /// recenzije) — tu je skup jeftiniji od upita po stavci.
    /// </summary>
    public async Task<HashSet<string>> SviBlokiraniAsync(string userId)
    {
        if (string.IsNullOrEmpty(userId))
            return [];

        var ids = await db.UserBlocks
            .Where(ub => ub.BlockerId == userId || ub.BlockedId == userId)
            .Select(ub => ub.BlockerId == userId ? ub.BlockedId : ub.BlockerId)
            .Distinct()
            .ToListAsync();

        return [.. ids];
    }

    // ── RADNJE ─────────────────────────────────────────────────────────────

    public async Task<(bool Success, string? Error)> BlokirajAsync(
        string blockerId, string blockedId)
    {
        if (blockerId == blockedId)
            return (false, "Ne možete blokirati sami sebe.");

        var meta = await db.Users
            .Where(u => u.Id == blockedId)
            .Select(u => new { u.Id })
            .FirstOrDefaultAsync();

        if (meta is null)
            return (false, "Korisnik nije pronađen.");

        // Idempotentno: ponovljeno blokiranje je uspeh, ne greška. Klijent sme
        // da pošalje isti zahtev dvaput (dupli tap, ponovni pokušaj posle
        // prekida veze) i ne sme da dobije crveno.
        var vecBlokiran = await db.UserBlocks
            .AnyAsync(ub => ub.BlockerId == blockerId && ub.BlockedId == blockedId);

        if (vecBlokiran)
            return (true, null);

        db.UserBlocks.Add(new UserBlock
        {
            BlockerId = blockerId,
            BlockedId = blockedId,
            CreatedAt = DateTime.UtcNow
        });

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Dva istovremena zahteva su prošla proveru iznad pre nego što je
            // ijedan upisao. UNIQUE indeks je uhvatio drugi — a ishod koji je
            // korisnik tražio ipak postoji.
            return (true, null);
        }

        logger.LogInformation(
            "Korisnik {BlockerId} blokirao {BlockedId}", blockerId, blockedId);

        return (true, null);
    }

    public async Task<(bool Success, string? Error)> OdblokirajAsync(
        string blockerId, string blockedId)
    {
        // Briše se SAMO red u kom je pozivalac blocker. Ako je i druga strana
        // blokirala njega, taj red ostaje — i blokada i dalje važi. Jedino tako
        // odblokiranje ne može da posluži za skidanje tuđe blokade.
        var obrisano = await db.UserBlocks
            .Where(ub => ub.BlockerId == blockerId && ub.BlockedId == blockedId)
            .ExecuteDeleteAsync();

        if (obrisano == 0)
            return (false, "Korisnik nije blokiran.");

        logger.LogInformation(
            "Korisnik {BlockerId} odblokirao {BlockedId}", blockerId, blockedId);

        return (true, null);
    }

    /// <summary>
    /// Koga je ovaj korisnik blokirao. Samo njegovi redovi — ne i oni u kojima
    /// je on blokiran, jer te ne može ni da vidi ni da ukloni.
    /// </summary>
    public async Task<List<BlockedUserDto>> ListaBlokiranihAsync(string userId)
        => await db.UserBlocks
            .AsNoTracking()
            .Where(ub => ub.BlockerId == userId)
            .OrderByDescending(ub => ub.CreatedAt)
            .Select(ub => new BlockedUserDto
            {
                UserId          = ub.BlockedId,
                FullName        = ub.Blocked.FullName,
                ProfileImageUrl = ub.Blocked.ProfileImageUrl,
                BlockedAt       = ub.CreatedAt
            })
            .ToListAsync();
}
