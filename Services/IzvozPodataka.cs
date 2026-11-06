using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Infrastructure;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Izvoz podataka o ličnosti: sve što server čuva o korisniku, u jednom
/// JSON-u koji može da sačuva i otvori bilo gde.
///
/// ZAŠTO POSTOJI: ZZPL (čl. 26 i 36) i GDPR (čl. 15 i 20) daju pravo na
/// pristup i na prenosivost „u strukturisanom, uobičajeno korišćenom i
/// elektronski čitljivom obliku". Do sada je to išlo samo mejlom, ručno;
/// ručni izvoz se lako zaboravi ili uradi nepotpuno, a rok je 30 dana.
///
/// ŠTA NAMERNO NIJE U IZVOZU:
///   • Poruke DRUGE strane u razgovoru — to su njeni podaci. Ulaze samo
///     poruke koje je korisnik sam poslao, dešifrovane.
///   • Heš lozinke, refresh tokeni, kod za reset — tajne, ne podaci o ličnosti
///     koji korisniku išta znače, a izvozni fajl se lako prosledi dalje.
///   • Sadržaj slika — samo adrese. Slike u četu su šifrovane na disku i
///     otvaraju se potpisanim linkom koji kratko traje.
///   • Prijave koje su DRUGI podneli protiv korisnika — štite prijavioca.
///
/// Kad se doda nova tabela sa podacima korisnika, dodaje se i ovde, a
/// <c>IzvozPodatakaTests</c> proverava da je svaki odeljak popunjen.
/// </summary>
public sealed class IzvozPodataka(AppDbContext db, MessageEncryption encryption)
{
    public async Task<object?> NapraviAsync(string userId)
    {
        var u = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId);
        if (u is null || !u.IsActive) return null;

        var profil = await db.ProviderProfiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new
            {
                p.Profession, p.Bio, p.Location, p.Instagram, p.CoverImageUrl,
                p.AverageRating, p.TotalReviews, p.CreatedAt
            })
            .SingleOrDefaultAsync();

        var oglasi = await db.Listings.AsNoTracking()
            .Where(l => l.ProviderProfile.UserId == userId)
            .OrderBy(l => l.Id)
            .Select(l => new
            {
                l.Id, l.Title, l.Description, l.Location, Kategorija = l.Category.Name,
                CenaRezim = l.PriceMode.ToString(), l.FixedPrice, l.PriceFrom, l.PriceTo,
                Status = l.Status.ToString(), l.CreatedAt, l.UpdatedAt,
                Slike = l.Images.OrderBy(i => i.SortOrder).Select(i => i.ImageUrl).ToList()
            })
            .ToListAsync();

        var poslovi = await db.Jobs.AsNoTracking()
            .Where(j => j.AuthorId == userId)
            .OrderBy(j => j.Id)
            .Select(j => new
            {
                j.Id, j.Title, j.Description, j.Location, j.PayAmount,
                Status = j.Status.ToString(), j.ExpiresAt, j.CompletedAt,
                Slike = j.Images.OrderBy(i => i.SortOrder).Select(i => i.ImageUrl).ToList()
            })
            .ToListAsync();

        var javljanja = await db.JobApplications.AsNoTracking()
            .Where(a => a.ProviderUserId == userId)
            .OrderBy(a => a.Id)
            .Select(a => new
            {
                a.Id, a.JobId, Posao = a.Job.Title, a.Message, a.OfferAmount,
                Status = a.Status.ToString(), a.CreatedAt
            })
            .ToListAsync();

        var zahtevi = await db.BookingRequests.AsNoTracking()
            .Where(b => b.ClientId == userId || b.ProviderUserId == userId)
            .OrderBy(b => b.Id)
            .Select(b => new
            {
                b.Id, Oglas = b.Listing.Title,
                Uloga = b.ClientId == userId ? "korisnik" : "uslugodavac",
                b.RequestedDate, b.RequestedTime, b.Notes,
                Status = b.Status.ToString(), b.CreatedAt
            })
            .ToListAsync();

        var ocene = await db.Reviews.AsNoTracking()
            .Where(r => r.AuthorId == userId || r.SubjectUserId == userId)
            .OrderBy(r => r.Id)
            .Select(r => new
            {
                r.Id, Smer = r.AuthorId == userId ? "dao" : "dobio",
                Uloga = r.SubjectRole.ToString(), r.ContextTitle, r.Stars, r.Comment,
                r.CreatedAt, r.PublishedAt
            })
            .ToListAsync();

        // Poruke se dešifruju u memoriji — šifra je vezana za razgovor (AAD),
        // pa SQL ovde ne može da pomogne.
        var porukeSirove = await db.Messages.AsNoTracking()
            .Where(m => m.SenderId == userId)
            .OrderBy(m => m.SentAt)
            .Select(m => new { m.ConversationId, m.Text, m.SentAt, Vrsta = m.Kind.ToString() })
            .ToListAsync();
        var poruke = porukeSirove
            .Select(m => new
            {
                Razgovor = m.ConversationId,
                Tekst    = encryption.SafeDecrypt(m.Text, m.ConversationId),
                m.SentAt, m.Vrsta
            })
            .ToList();

        var tokeni = await db.TokenTransactions.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.Id)
            .Select(t => new { t.Amount, Vrsta = t.Kind.ToString(), t.Description, t.BalanceAfter, t.CreatedAt })
            .ToListAsync();

        var sacuvaniOglasi = await db.FavoriteListings.AsNoTracking()
            .Where(f => f.UserId == userId)
            .Select(f => new { f.ListingId, Oglas = f.Listing.Title, f.CreatedAt })
            .ToListAsync();

        var sacuvaniUslugodavci = await db.FavoriteProviders.AsNoTracking()
            .Where(f => f.UserId == userId)
            .Select(f => new { f.ProviderProfileId, f.CreatedAt })
            .ToListAsync();

        var blokirani = await db.UserBlocks.AsNoTracking()
            .Where(b => b.BlockerId == userId)
            .Select(b => new { Korisnik = b.Blocked.FullName, b.CreatedAt })
            .ToListAsync();

        var prijave = await db.Reports.AsNoTracking()
            .Where(r => r.ReporterId == userId)
            .OrderBy(r => r.Id)
            .Select(r => new
            {
                Meta = r.TargetType.ToString(), Razlog = r.Reason.ToString(), r.Note,
                Status = r.Status.ToString(), r.CreatedAt
            })
            .ToListAsync();

        var obavestenja = await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderBy(n => n.Id)
            .Select(n => new { n.Title, n.Body, n.IsRead, n.CreatedAt })
            .ToListAsync();

        var uredjaji = await db.DeviceTokens.AsNoTracking()
            .Where(d => d.UserId == userId)
            .Select(d => new { d.Platform, d.CreatedAt, d.LastSeenAt })
            .ToListAsync();

        var pozvani = await db.Referrals.AsNoTracking()
            .Where(r => r.ReferrerId == userId)
            .Select(r => new { Status = r.Status.ToString(), r.CreatedAt })
            .ToListAsync();

        return new
        {
            Napomena = "Podaci koje Uslužionica čuva o tebi, na dan izvoza. Poruke druge strane u razgovoru nisu uključene, jer su to njeni podaci.",
            IzvezenoUtc = DateTime.UtcNow,
            Nalog = new
            {
                u.Id, u.Email, u.EmailConfirmed, u.FullName, Grad = u.LastKnownCity,
                u.ProfileImageUrl, u.IsProvider, u.CreatedAt, u.ReferralCode, u.TokenBalance,
                OcenaKaoNarucilac = u.ClientRating, BrojOcenaKaoNarucilac = u.ClientReviewCount,
                SaglasnostPrihvacena = u.PolicyAcceptedAt, VerzijaSaglasnosti = u.PolicyVersionAccepted,
                KakoSiCuoZaNas = u.AcquisitionSource
            },
            ProfilUslugodavca   = profil,
            Oglasi              = oglasi,
            Poslovi             = poslovi,
            JavljanjaNaPoslove  = javljanja,
            ZahteviZaUslugu     = zahtevi,
            Ocene               = ocene,
            PoslatePoruke       = poruke,
            Tokeni              = tokeni,
            SacuvaniOglasi      = sacuvaniOglasi,
            SacuvaniUslugodavci = sacuvaniUslugodavci,
            BlokiraniKorisnici  = blokirani,
            MojePrijave         = prijave,
            Obavestenja         = obavestenja,
            Uredjaji            = uredjaji,
            PozvaniKorisnici    = pozvani
        };
    }
}
