using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Bonus za listu čekanja: 10 tokena svakome ko se upisao na sajtu pre izlaska
/// aplikacije i otvori nalog ISTOM adresom.
///
/// To je obećanje sa sajta (usluzionica.rs — modal, poziv na dnu, „Česta
/// pitanja"), pa pravila ovde moraju da prate tamošnji tekst:
///
///   • važi samo za upis PRE izlaska      → Waitlist:LaunchDate
///   • nalog mora biti otvoren u roku     → Waitlist:ClaimDays posle izlaska (60)
///   • „istom adresom"                    → adresa naloga = adresa sa liste
///
/// Dok LaunchDate nije postavljen, aplikacija još nije izašla: svaki upis je
/// „pre izlaska" i rok još ne teče.
///
/// SAMO POTVRĐENA ADRESA. Bez toga bi bilo ko mogao da otvori nalog sa tuđom
/// adresom sa liste i uzme njene tokene. Zato se bonus proverava na istim
/// mestima gde i prva referral rata — kad adresa POSTANE potvrđena (klik na
/// link, reset lozinke, Google prijava) — i u periodičnom prolazu za naloge
/// koji su bili potvrđeni pre nego što se adresa pojavila na listi.
///
/// Kao ReferralService: idempotentno, bezbedno pod istovremenim pozivima, i
/// nikad ne baca — greška u bonusu ne sme da obori potvrdu mejla.
/// </summary>
public sealed class ListaCekanjaBonus(
    AppDbContext               db,
    ListaCekanja               lista,
    NotificationService        notifications,
    IConfiguration             config,
    ILogger<ListaCekanjaBonus> logger)
{
    public decimal Iznos => config.GetValue("Waitlist:BonusTokens", 10m);

    public DateTime? DatumIzlaska =>
        DateTime.TryParse(config["Waitlist:LaunchDate"], System.Globalization.CultureInfo.InvariantCulture,
                          System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                          out var d) ? d : null;

    public int RokDana => config.GetValue("Waitlist:ClaimDays", 60);

    /// <summary>Poslednji trenutak u kom se bonus još dodeljuje, ili null dok aplikacija nije izašla.</summary>
    public DateTime? Rok => DatumIzlaska?.AddDays(RokDana);

    private bool Ukljucen => lista.Putanja is not null && Iznos > 0;

    /// <summary>
    /// Dodeljuje bonus korisniku ako mu je potvrđena adresa na listi i ako ga
    /// još nije dobio. Vraća true samo kad je bonus upravo isplaćen.
    /// </summary>
    public async Task<bool> PokusajAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            if (!Ukljucen) return false;

            var sada = DateTime.UtcNow;
            if (Rok is { } rok && sada > rok) return false;

            var korisnik = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .Select(u => new { u.Id, u.Email, u.EmailConfirmed })
                .FirstOrDefaultAsync(ct);

            if (korisnik is null || !korisnik.EmailConfirmed || korisnik.Email is null) return false;

            if (lista.Nadji(korisnik.Email) is not { } upisan) return false;
            if (DatumIzlaska is { } izlazak && upisan > izlazak) return false;

            var hes = ListaCekanja.Hes(korisnik.Email);
            if (await db.WaitlistBonuses.AnyAsync(w => w.EmailHash == hes || w.UserId == userId, ct))
                return false;

            var iznos = Iznos;

            // ── Rezervacija ────────────────────────────────────────────────
            // Jedinstveni indeksi (EmailHash, UserId) su prava brana: od dva
            // istovremena poziva drugi upis pada na indeksu, i taj poziv ne
            // isplaćuje ništa. Provera iznad je samo da se ne ide u bazu sa
            // upisom koji će sigurno pasti.
            db.WaitlistBonuses.Add(new WaitlistBonus
            {
                EmailHash  = hes,
                UserId     = userId,
                Amount     = iznos,
                SignedUpAt = upisan,
                AwardedAt  = sada
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                logger.LogInformation("Bonus liste čekanja za {UserId} je već isplaćen — preskačem.", userId);
                return false;
            }

            // ── Isplata ────────────────────────────────────────────────────
            // Jednom naredbom (SET balance = balance + @x), kao u ReferralService:
            // `user.TokenBalance += x` bi pregazio isplatu koja istovremeno
            // stiže istom korisniku iz drugog toka.
            await db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.TokenBalance, u => u.TokenBalance + iznos), ct);

            var balansPosle = await db.Users
                .Where(u => u.Id == userId)
                .Select(u => u.TokenBalance)
                .FirstAsync(ct);

            db.TokenTransactions.Add(new TokenTransaction
            {
                UserId       = userId,
                Amount       = iznos,
                Kind         = TokenKind.WaitlistBonus,
                Description  = "Bonus za prijavu na listu čekanja",
                BalanceAfter = balansPosle,
                CreatedAt    = sada
            });
            await db.SaveChangesAsync(ct);

            await notifications.SendAsync(
                userId, NotificationKind.TokenEarned,
                "Stigao je tvoj bonus",
                $"Hvala što si nas čekao — {iznos:0.##} tokena za prijavu na listu čekanja je na tvom nalogu.");

            // Id, ne mejl — lični podatak ne pripada logu.
            logger.LogInformation("Bonus liste čekanja: {Iznos} tokena korisniku {UserId}.", iznos, userId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Bonus liste čekanja nije isplaćen korisniku {UserId}.", userId);
            return false;
        }
    }

    /// <summary>
    /// Prolaz kroz celu listu: isplaćuje bonus svim POTVRĐENIM nalozima čija je
    /// adresa na listi, a bonus još nisu dobili. Hvata naloge koji su bili
    /// potvrđeni pre nego što je adresa stigla na listu (ili pre ovog deploya).
    /// Vraća broj isplaćenih bonusa.
    /// </summary>
    public async Task<int> ProdjiAsync(CancellationToken ct = default)
    {
        if (!Ukljucen) return 0;
        if (Rok is { } rok && DateTime.UtcNow > rok) return 0;

        var prijave = lista.Prijave();
        if (prijave.Count == 0) return 0;

        var izlazak = DatumIzlaska;
        var adrese = prijave
            .Where(p => izlazak is null || p.Value <= izlazak)
            .Select(p => p.Key.ToUpperInvariant())   // Identity: NormalizedEmail je velikim slovima
            .ToList();

        var isplaceno = 0;

        // Po 500 — IN lista u SQL-u ima granicu broja parametara.
        foreach (var deo in adrese.Chunk(500))
        {
            var kandidati = await db.Users.AsNoTracking()
                .Where(u => u.EmailConfirmed
                         && u.NormalizedEmail != null
                         && deo.Contains(u.NormalizedEmail)
                         && !db.WaitlistBonuses.Any(w => w.UserId == u.Id))
                .Select(u => u.Id)
                .ToListAsync(ct);

            foreach (var id in kandidati)
                if (await PokusajAsync(id, ct)) isplaceno++;
        }

        if (isplaceno > 0)
            logger.LogInformation("Lista čekanja: isplaćeno {Broj} bonusa u prolazu.", isplaceno);

        return isplaceno;
    }

    /// <summary>Brojevi za admin pregled — bez ijedne adrese.</summary>
    public async Task<(int NaListi, int Isplaceno, decimal Iznos, DateTime? Izlazak, DateTime? Rok, bool Ukljucen)> StanjeAsync(
        CancellationToken ct = default)
    {
        var naListi   = lista.Prijave().Count;
        var isplaceno = await db.WaitlistBonuses.CountAsync(ct);
        return (naListi, isplaceno, Iznos, DatumIzlaska, Rok, Ukljucen);
    }
}
