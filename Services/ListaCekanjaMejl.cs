using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Infrastructure.Redis;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Mejl „Uslužionica je na App Store-u" svima sa liste čekanja. Admin radnja
/// (AdminController → /api/admin/waitlist/launch-email).
///
/// BEZBEDNOST PRE SVEGA — ovo je jedina radnja na serveru koja šalje mejl
/// stotinama ljudi odjednom, i greška se ne može povući:
///
///   • DRY-RUN JE PODRAZUMEVAN. Bez `posalji=true` vraća se samo broj
///     primalaca i pregled mejla, bez ijedne adrese.
///   • NAJVIŠE JEDNOM PO ADRESI. Pre slanja se upisuje <see cref="WaitlistEmail"/>
///     (rezervacija); jedinstveni indeks (Campaign, EmailHash) sprečava duplikat
///     i kad se slanje pokrene ponovo, i kad dva zahteva stignu istovremeno.
///     Rezervacija se ne briše ni kad SMTP padne — poruka je možda ipak otišla.
///     Neuspele se ponavljaju samo na izričit zahtev (`ponoviNeuspele=true`).
///   • U PAKETIMA. Waitlist:Email:BatchSize po paketu, PauseSeconds između, i
///     najviše MaxPerRun po pokretanju. Verifikacioni mejlovi troše isti dnevni
///     limit provajdera — slanje listi ne sme da ostavi nove korisnike bez
///     linka za potvrdu.
///   • NE PRE IZLASKA. Pravo slanje se odbija dok Waitlist:IosLaunchDate nije
///     upisan (ili je u budućnosti) i dok App:AppStoreUrl nije upisan.
///
/// KOME SE NE ŠALJE (odluka, vidi _integracija/mejl-lista-cekanja.md):
///   • ko već ima nalog tom adresom — tekst je poziv da otvori nalog, a on ga
///     ima; bonus mu ionako stiže sam (ListaCekanjaBonus.ProdjiAsync). Isto i
///     za adresu koja je bonus već dobila pa obrisala nalog;
///   • ko se odjavio (<see cref="WaitlistOptOut"/>);
///   • ko se upisao POSLE izlaska na iOS-u — tada je aplikacija već bila tu.
///
/// Bonus se u mejlu pominje SAMO kad za tu prijavu još važi
/// (<see cref="ListaCekanjaBonus.Vazi"/>), sa tačnim rokom.
/// </summary>
public sealed class ListaCekanjaMejl(
    AppDbContext              db,
    ListaCekanja              lista,
    ListaCekanjaBonus         bonus,
    IEmailService             email,
    DistributedLock           locks,
    IConfiguration            config,
    ILogger<ListaCekanjaMejl> logger)
{
    public const string Kampanja = "ios-izlazak";

    /// <summary>Upisani pre ovog datuma dobijaju poruku; vidi <see cref="ListaCekanjaBonus.IzlazakIos"/>.</summary>
    private DateTime? IzlazakIos => bonus.IzlazakIos;

    private string? AppStoreUrl => config["App:AppStoreUrl"] is { Length: > 0 } u ? u.Trim() : null;
    private string? GooglePlayUrl => config["App:GooglePlayUrl"] is { Length: > 0 } u ? u.Trim() : null;

    private int Paket   => Math.Max(1, config.GetValue("Waitlist:Email:BatchSize", 20));
    private int Pauza   => Math.Max(0, config.GetValue("Waitlist:Email:PauseSeconds", 60));
    private int Najvise => Math.Max(1, config.GetValue("Waitlist:Email:MaxPerRun", 80));

    // ── Javni tipovi ───────────────────────────────────────────────────────

    /// <param name="Posalji">false = dry-run (podrazumevano).</param>
    /// <param name="Lista">null = obe liste; „ios" ili „android" = samo ta.</param>
    /// <param name="Max">Najviše poruka u ovom pokretanju; null = Waitlist:Email:MaxPerRun.</param>
    /// <param name="PonoviNeuspele">Pošalji ponovo adresama kojima je SMTP ranije odbio.</param>
    public sealed record Opcije(bool Posalji = false, string? Lista = null, int? Max = null, bool PonoviNeuspele = false);

    public sealed record Preskoceno(int ImaNalog, int VecPoslato, int Odjavljeni, int UpisPosleIzlaska, int NeuspeloRanije);

    public sealed record Pregled(string Naslov, string Html, string Tekst);

    public sealed record Izvestaj(
        bool                 Posalji,
        int                  NaListi,
        int                  Primalaca,
        int                  SaBonusom,
        int                  BezBonusa,
        IReadOnlyDictionary<string, int> PoListi,
        Preskoceno           Preskoceno,
        int                  Poslato,
        int                  Neuspelo,
        int                  Preostalo,
        bool                 Prekinuto,
        IReadOnlyList<string> Upozorenja,
        Pregled              PregledSaBonusom,
        Pregled              PregledBezBonusa);

    /// <summary>Greška koja zaustavlja PRAVO slanje pre prve poruke (nema izlaska, nema linka…).</summary>
    public sealed class Odbijeno(string poruka) : Exception(poruka);

    // ── Kandidati ──────────────────────────────────────────────────────────

    private sealed record Primalac(string Email, string Hes, ListaCekanja.Prijava Prijava, string Lista, bool Bonus, int? NeuspeoId);

    private async Task<(List<Primalac> Primaoci, Preskoceno Preskoceno, int NaListi)> PrimaociAsync(
        Opcije opcije, CancellationToken ct)
    {
        var sada = DateTime.UtcNow;
        var izlazak = IzlazakIos;

        var sve = lista.Prijave()
            .Select(p => (Email: p.Key, Prijava: p.Value, Lista: bonus.ListaZa(p.Value)))
            .Where(x => opcije.Lista is null || x.Lista == opcije.Lista)
            .OrderBy(x => x.Prijava.Kreirano)          // najstariji upisi prvi — čekali su najduže
            .ThenBy(x => x.Email, StringComparer.Ordinal)
            .ToList();

        int imaNalog = 0, vecPoslato = 0, odjavljeni = 0, posleIzlaska = 0, neuspeloRanije = 0;
        var primaoci = new List<Primalac>();

        // Po 500 — IN lista u SQL-u ima granicu broja parametara (kao ProdjiAsync).
        foreach (var deo in sve.Chunk(500))
        {
            var hesevi = deo.Select(x => ListaCekanja.Hes(x.Email)).ToList();
            var velika = deo.Select(x => x.Email.ToUpperInvariant()).ToList();   // Identity: NormalizedEmail

            var naloga = (await db.Users.AsNoTracking()
                    .Where(u => u.NormalizedEmail != null && velika.Contains(u.NormalizedEmail))
                    .Select(u => u.NormalizedEmail!)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            var bonusDobili = (await db.WaitlistBonuses.AsNoTracking()
                    .Where(w => hesevi.Contains(w.EmailHash))
                    .Select(w => w.EmailHash)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            var odjave = (await db.WaitlistOptOuts.AsNoTracking()
                    .Where(o => hesevi.Contains(o.EmailHash))
                    .Select(o => o.EmailHash)
                    .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            var poslato = await db.WaitlistEmails.AsNoTracking()
                .Where(m => m.Campaign == Kampanja && hesevi.Contains(m.EmailHash))
                .Select(m => new { m.Id, m.EmailHash, m.SentAt, m.FailedAt })
                .ToDictionaryAsync(m => m.EmailHash, StringComparer.Ordinal, ct);

            foreach (var x in deo)
            {
                var hes = ListaCekanja.Hes(x.Email);

                if (odjave.Contains(hes))                                         { odjavljeni++;   continue; }
                if (naloga.Contains(x.Email.ToUpperInvariant()) || bonusDobili.Contains(hes)) { imaNalog++; continue; }
                if (izlazak is { } iz && x.Prijava.Kreirano > iz)                 { posleIzlaska++; continue; }

                int? neuspeoId = null;
                if (poslato.TryGetValue(hes, out var m))
                {
                    var neuspeo = m.SentAt is null && m.FailedAt is not null;
                    if (!neuspeo)                  { vecPoslato++;     continue; }
                    if (!opcije.PonoviNeuspele)    { neuspeloRanije++; continue; }
                    neuspeoId = m.Id;
                }

                var saBonusom = bonus.Iznos > 0 && bonus.Vazi(x.Prijava, sada);
                primaoci.Add(new Primalac(x.Email, hes, x.Prijava, x.Lista, saBonusom, neuspeoId));
            }
        }

        return (primaoci, new Preskoceno(imaNalog, vecPoslato, odjavljeni, posleIzlaska, neuspeloRanije), sve.Count);
    }

    // ── Poruka ─────────────────────────────────────────────────────────────

    private ListaCekanjaMejlSablon.Podaci Podaci(string adresa, string hes, ListaCekanja.Prijava? prijava, bool saBonusom)
    {
        var listaPrijave = prijava is null ? ListaCekanjaBonus.Ios : bonus.ListaZa(prijava);
        return new ListaCekanjaMejlSablon.Podaci(
            Email:            adresa,
            IzlazakIos:       IzlazakIos ?? DateTime.UtcNow.Date,
            AppStoreUrl:      AppStoreUrl ?? ListaCekanjaMejlSablon.AppStorePlaceholder,
            GooglePlayUrl:    GooglePlayUrl,
            Bonus:            saBonusom ? bonus.Iznos : null,
            BonusRok:         saBonusom ? bonus.Rok(listaPrijave) : null,
            PozivPotvrda:     config.GetValue("Referral:SignupRewardTokens", 2m),
            PozivUslugodavac: config.GetValue("Referral:ProviderActivationRewardTokens", 3m),
            OdjavaUrl:        OdjavaUrl(hes));
    }

    private MejlListeCekanja Poruka(string adresa, string hes, ListaCekanja.Prijava? prijava, bool saBonusom)
    {
        var p = Podaci(adresa, hes, prijava, saBonusom);
        return new MejlListeCekanja(adresa, ListaCekanjaMejlSablon.Naslov(p),
            ListaCekanjaMejlSablon.Html(p), ListaCekanjaMejlSablon.Tekst(p), p.OdjavaUrl);
    }

    private Pregled PregledZa(bool saBonusom)
    {
        const string primer = "primer@primer.rs";
        var m = Poruka(primer, ListaCekanja.Hes(primer), null, saBonusom);
        return new Pregled(m.Subject, m.Html, m.Text);
    }

    // ── Odjava ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Ključ za potpis linka za odjavu, izveden iz Jwt:Secret (ista tajna, druga
    /// namena — pa potpis odjave ne može da posluži kao ništa drugo).
    /// Bez potpisa bi bilo ko mogao da odjavi tuđu adresu znajući samo njen heš.
    /// </summary>
    private byte[] KljucOdjave =>
        SHA256.HashData(Encoding.UTF8.GetBytes("usluzionica:lista-cekanja:odjava:" + config["Jwt:Secret"]));

    public string Potpis(string hes) =>
        Convert.ToHexString(HMACSHA256.HashData(KljucOdjave, Encoding.UTF8.GetBytes(hes)))[..32].ToLowerInvariant();

    public bool PotpisVazi(string? hes, string? potpis) =>
        hes is { Length: 64 } && potpis is { Length: 32 } &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Potpis(hes)), Encoding.ASCII.GetBytes(potpis.ToLowerInvariant()));

    public string OdjavaUrl(string hes)
    {
        var baza = (config["App:BaseUrl"] ?? "").TrimEnd('/');
        return $"{baza}/api/lista-cekanja/odjava?a={hes}&p={Potpis(hes)}";
    }

    /// <summary>Upisuje odjavu. Idempotentno — drugi klik (ili prefetch) ne smeta.</summary>
    public async Task<bool> OdjaviAsync(string hes, string potpis, CancellationToken ct = default)
    {
        if (!PotpisVazi(hes, potpis)) return false;
        hes = hes.ToLowerInvariant();

        if (await db.WaitlistOptOuts.AnyAsync(o => o.EmailHash == hes, ct)) return true;

        db.WaitlistOptOuts.Add(new WaitlistOptOut { EmailHash = hes, OptedOutAt = DateTime.UtcNow });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();   // istovremeni drugi klik je upisao isto
        }

        logger.LogInformation("Lista čekanja: odjava upisana.");
        return true;
    }

    // ── Glavna radnja ──────────────────────────────────────────────────────

    /// <summary>
    /// Dry-run (podrazumevano) ili pravo slanje. Vraća brojeve i pregled — nikad adrese.
    /// Baca <see cref="Odbijeno"/> kad pravo slanje nije dozvoljeno.
    /// </summary>
    public async Task<Izvestaj> PokreniAsync(Opcije opcije, CancellationToken ct = default)
    {
        if (opcije.Lista is not (null or ListaCekanjaBonus.Ios or ListaCekanjaBonus.Android))
            throw new Odbijeno("Lista mora biti „ios\", „android\" ili prazna (obe).");

        var sada = DateTime.UtcNow;

        // Blokade zaustavljaju PRAVO slanje; dry-run ih samo prijavljuje.
        var blokade = new List<string>();
        if (lista.Putanja is null)
            blokade.Add("Waitlist:Path nije podešen — server ne vidi listu čekanja.");
        if (AppStoreUrl is null)
            blokade.Add("App:AppStoreUrl nije upisan — u pregledu stoji placeholder.");
        if (IzlazakIos is null)
            blokade.Add("Waitlist:IosLaunchDate nije upisan — aplikacija još nije označena kao izašla na iOS-u.");
        else if (IzlazakIos > sada)
            blokade.Add($"Waitlist:IosLaunchDate ({IzlazakIos:yyyy-MM-dd}) je u budućnosti.");

        var upozorenja = new List<string>(blokade);
        if (bonus.Iznos > 0 && bonus.Rok(ListaCekanjaBonus.Ios) is { } rok && rok < sada)
            upozorenja.Add("Rok za bonus sa iPhone liste je istekao — mejl ga nikome sa te liste ne pominje.");

        var max = Math.Clamp(opcije.Max ?? Najvise, 1, 1000);

        if (!opcije.Posalji)
        {
            var (svi, presk, naListi) = await PrimaociAsync(opcije, ct);
            return Napravi(false, naListi, svi, presk, 0, 0, Math.Max(0, svi.Count), false, upozorenja);
        }

        // ── Pravo slanje: zaštite ──────────────────────────────────────────
        if (blokade.Count > 0)
            throw new Odbijeno("Slanje odbijeno: " + string.Join(" ", blokade));

        // Jedno slanje u isto vreme na celom klasteru. Duplikat ionako sprečava
        // jedinstveni indeks, ali dva paralelna slanja bi duplo trošila limit.
        await using var brava = await locks.TryAcquireAsync("lista-cekanja-mejl", TimeSpan.FromHours(2));
        if (brava is null)
            throw new Odbijeno("Slanje već traje (druga instanca ili drugi zahtev).");

        var (primaoci, preskoceno, ukupno) = await PrimaociAsync(opcije, ct);
        var ovaj = primaoci.Take(max).ToList();

        int poslato = 0, neuspelo = 0, zaredom = 0;
        var prekinuto = false;

        var paketi = ovaj.Chunk(Paket).ToList();
        for (var i = 0; i < paketi.Count && !prekinuto; i++)
        {
            if (i > 0 && Pauza > 0)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(Pauza), ct); }
                catch (OperationCanceledException) { prekinuto = true; break; }
            }

            foreach (var p in paketi[i])
            {
                // Prekid (klijent je prekinuo zahtev) se proverava samo IZMEĐU
                // poruka — nikad između rezervacije i slanja.
                if (ct.IsCancellationRequested) { prekinuto = true; break; }

                var red = await RezervisiAsync(p);
                if (red is null) { continue; }   // istovremeno je rezervisao drugi zahtev

                try
                {
                    await email.SendListaCekanjaAsync(Poruka(p.Email, p.Hes, p.Prijava, p.Bonus));
                    await db.WaitlistEmails.Where(m => m.Id == red)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.SentAt, DateTime.UtcNow), CancellationToken.None);
                    poslato++;
                    zaredom = 0;
                }
                catch (Exception ex)
                {
                    await db.WaitlistEmails.Where(m => m.Id == red)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(m => m.FailedAt, DateTime.UtcNow)
                            .SetProperty(m => m.Error, ex.GetType().Name), CancellationToken.None);
                    neuspelo++;
                    zaredom++;

                    // Id reda, ne adresa — lični podatak ne pripada logu.
                    logger.LogWarning(ex, "Mejl listi čekanja (red {Id}) nije poslat.", red);

                    // Tri zaredom = verovatno je pređen limit provajdera ili SMTP
                    // ne radi. Bolje stati nego obeležiti ceo paket kao neuspeo.
                    if (zaredom >= 3) { prekinuto = true; break; }
                }
            }
        }

        logger.LogInformation("Mejl listi čekanja: poslato {Poslato}, neuspelo {Neuspelo}, preostalo {Preostalo}.",
            poslato, neuspelo, primaoci.Count - poslato - neuspelo);

        return Napravi(true, ukupno, primaoci, preskoceno, poslato, neuspelo,
            Math.Max(0, primaoci.Count - poslato - neuspelo), prekinuto, upozorenja);
    }

    /// <summary>Rezervacija pre slanja. Vraća id reda, ili null ako je adresu već uzeo drugi zahtev.</summary>
    private async Task<int?> RezervisiAsync(Primalac p)
    {
        var sada = DateTime.UtcNow;

        if (p.NeuspeoId is { } id)
        {
            // Ponovni pokušaj: uslov u SAMOJ naredbi, kao u ReferralService —
            // od dva istovremena zahteva samo jedan pogodi red.
            var uzeto = await db.WaitlistEmails
                .Where(m => m.Id == id && m.SentAt == null && m.FailedAt != null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.FailedAt, (DateTime?)null)
                    .SetProperty(m => m.Error, (string?)null)
                    .SetProperty(m => m.ReservedAt, sada));
            return uzeto == 1 ? id : null;
        }

        var red = new WaitlistEmail { Campaign = Kampanja, EmailHash = p.Hes, ReservedAt = sada };
        db.WaitlistEmails.Add(red);
        try
        {
            await db.SaveChangesAsync();
            return red.Id;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return null;
        }
        finally
        {
            if (db.Entry(red).State != EntityState.Detached) db.Entry(red).State = EntityState.Detached;
        }
    }

    private Izvestaj Napravi(bool posalji, int naListi, List<Primalac> primaoci, Preskoceno preskoceno,
                             int poslato, int neuspelo, int preostalo, bool prekinuto, List<string> upozorenja) =>
        new(posalji, naListi, primaoci.Count,
            primaoci.Count(p => p.Bonus), primaoci.Count(p => !p.Bonus),
            primaoci.GroupBy(p => p.Lista).ToDictionary(g => g.Key, g => g.Count()),
            preskoceno, poslato, neuspelo, preostalo, prekinuto, upozorenja,
            PregledZa(true), PregledZa(false));

    // ── Probno slanje ──────────────────────────────────────────────────────

    /// <summary>
    /// Jedna poruka na adresu koju admin izričito navede — da vidi mejl u
    /// pravom inboksu pre slanja listi. Ne upisuje se kao poslato: probna
    /// poruka ne sme da izbaci adresu iz pravog slanja.
    /// </summary>
    public async Task<string> ProbaAsync(string adresa, bool saBonusom = true)
    {
        var norm = ListaCekanja.Normalizuj(adresa);
        if (norm is null || !System.Net.Mail.MailAddress.TryCreate(norm, out _))
            throw new Odbijeno("Neispravna adresa za probno slanje.");

        var prijava = lista.Nadji(norm);
        var poruka  = Poruka(norm, ListaCekanja.Hes(norm), prijava, saBonusom);
        await email.SendListaCekanjaAsync(poruka with { Subject = "[PROBA] " + poruka.Subject });

        logger.LogInformation("Mejl listi čekanja: probna poruka poslata.");
        return poruka.Subject;
    }
}
