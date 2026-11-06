using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.Infrastructure.Media;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Spaja automatsku proveru slike sa redom za moderaciju.
///
/// Postoji kao zaseban servis, a ne kao poziv u svakom od tri upload puta,
/// zato što srednji ishod (<see cref="ImageVerdict.Review"/>) nije samo
/// „propusti" — mora i da napravi prijavu. Da je ta logika prepisana na tri
/// mesta, jedno bi pre ili kasnije propustilo da je napravi i slika bi prošla
/// bez ikakvog traga.
///
/// ZAŠTO NIJE U <see cref="ImageUploads"/>:
/// Ta klasa je statička, čista, bez DI i pokrivena unit testovima — proverava
/// bajtove i ne dodiruje ni bazu ni mrežu. Mrežni poziv u njoj pokvario bi sve
/// četiri osobine.
/// </summary>
public sealed class ImageModerationGate(
    AppDbContext                   db,
    IImageModerator                moderator,
    UserManager<ApplicationUser>   userManager,
    ILogger<ImageModerationGate>   logger)
{
    /// <summary>
    /// Proverava sliku i, ako treba, pravi prijavu.
    /// </summary>
    /// <param name="listingId">
    /// Popunjeno za slike oglasa. Za avatar i cover je null — tada se prijavljuje
    /// korisnik, jer oglas nije ni u igri.
    /// </param>
    /// <param name="jobId">Popunjeno za slike posla — tada se prijavljuje posao.</param>
    /// <returns>
    /// <c>Error</c> različit od null znači da upload treba odbiti.
    /// </returns>
    public async Task<(bool Ok, string? Error)> ProveriAsync(
        IFormFile file, string vlasnikId, int? listingId, int? jobId = null, CancellationToken ct = default)
    {
        var (presuda, detalji) = await PresudaAsync(file, vlasnikId, listingId, ct);

        switch (presuda)
        {
            case ImageVerdict.Allow:
                return (true, null);

            case ImageVerdict.Reject:
                logger.LogWarning(
                    "Slika odbijena automatskom proverom. Korisnik {UserId}, oglas {ListingId}. {Detalji}",
                    vlasnikId, listingId, detalji);

                // Poruka namerno ne pominje „nudity" ni rezultat provere.
                // Automatska provera greši, pa optužujuća poruka pogađa i one
                // koji nisu ništa prekršili.
                return (false, "Slika nije prihvaćena. Izaberite drugu fotografiju.");

            case ImageVerdict.Review:
            default:
                await NapraviAutomatskuPrijavuAsync(vlasnikId, listingId, jobId, detalji, ct);
                return (true, null);
        }
    }

    /// <summary>
    /// Samo presuda, bez prijave — za slike u četu, gde prijava mora da nosi id
    /// poruke, a poruka nastaje tek posle provere (vidi ChatImageService).
    /// Fail-open: pad provere je <see cref="ImageVerdict.Review"/>, ne odbijanje.
    /// </summary>
    public async Task<(ImageVerdict Presuda, string? Detalji)> PresudaAsync(
        IFormFile file, string vlasnikId, int? listingId = null, CancellationToken ct = default)
    {
        await using var stream = file.OpenReadStream();

        ImageVerdict presuda;
        string?      detalji;

        try
        {
            (presuda, detalji) = await moderator.ScanAsync(stream, ct);
        }
        catch (Exception ex)
        {
            // FAIL-OPEN NA DRUGOM NIVOU.
            //
            // CloudVisionImageModerator već hvata svoje greške, ali taj catch
            // štiti samo tu jednu implementaciju. Ovde se štiti UGOVOR: bilo
            // koji IImageModerator koji baci izuzetak — današnji ili sutrašnji,
            // sa drugim provajderom — ne sme da obori otpremanje slike.
            //
            // Bez ovoga bi pad provere značio da niko na celoj aplikaciji ne
            // može da postavi oglas. To je gori ishod od sumnjive slike koja je
            // prošla i čeka ručni pregled.
            logger.LogError(ex,
                "Provera slike je bacila izuzetak — slika ide na ručni pregled. " +
                "Korisnik {UserId}, oglas {ListingId}.", vlasnikId, listingId);

            presuda = ImageVerdict.Review;
            detalji = $"Provera nije uspela: {ex.GetType().Name}";
        }

        return (presuda, detalji);
    }

    /// <summary>Sumnjiva slika iz četa — prijava pošiljaoca, sa porukom kao kontekstom.</summary>
    public Task PrijaviSlikuIzCetaAsync(string posiljalacId, int messageId, string? detalji, CancellationToken ct = default) =>
        NapraviAutomatskuPrijavuAsync(posiljalacId, listingId: null, jobId: null, detalji, ct, messageId);

    /// <summary>
    /// Prijava koju podnosi sistem, ne korisnik.
    ///
    /// Prijavilac je admin nalog — <c>Report.ReporterId</c> je strani ključ na
    /// korisnike i mora pokazivati na postojeći red. Admin nalog se seeduje pri
    /// startu (<c>Program.SeedRolesAndAdminAsync</c>), pa uvek postoji.
    /// </summary>
    private async Task NapraviAutomatskuPrijavuAsync(
        string vlasnikId, int? listingId, int? jobId, string? detalji, CancellationToken ct, int? messageId = null)
    {
        var adminId = await PronadjiAdminaAsync();

        if (adminId is null)
        {
            // Bez admina nema ko da podnese prijavu. Upload i dalje prolazi —
            // fail-open je pravilo — ali ovo mora u logove, jer znači da je
            // sumnjiva slika prošla bez ijednog traga.
            logger.LogError(
                "Sumnjiva slika nije prijavljena: nema admin naloga. " +
                "Korisnik {UserId}, oglas {ListingId}. {Detalji}",
                vlasnikId, listingId, detalji);
            return;
        }

        // Isti admin ne sme dvaput da prijavi istu metu dok prva prijava čeka —
        // to čuva filtrirani UNIQUE indeks. Kod otpremanja pet slika na isti
        // oglas to bi značilo pet pokušaja i četiri izuzetka, pa se proverava
        // unapred.
        var vecPrijavljeno = await db.Reports.AnyAsync(r =>
            r.Status     == ReportStatus.Pending &&
            r.ReporterId == adminId &&
            (listingId != null ? r.ListingId == listingId
             : jobId != null   ? r.JobId == jobId
             : r.ReportedUserId == vlasnikId),
            ct);

        if (vecPrijavljeno) return;

        db.Reports.Add(new Report
        {
            ReporterId     = adminId,
            TargetType     = listingId is not null ? ReportTargetType.Listing
                           : jobId is not null     ? ReportTargetType.Job
                           : ReportTargetType.User,
            ListingId      = listingId,
            JobId          = listingId is null ? jobId : null,
            ReportedUserId = listingId is null && jobId is null ? vlasnikId : null,
            MessageId      = messageId,
            Reason         = ReportReason.Neprikladno,
            Note           = $"Automatska provera slike: {detalji ?? "bez detalja"}",
            Status         = ReportStatus.Pending,
            CreatedAt      = DateTime.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Dva istovremena otpremanja su prošla proveru iznad. Prijava ipak
            // postoji, što je jedino što je bilo važno.
        }

        logger.LogInformation(
            "Sumnjiva slika automatski prijavljena. Korisnik {UserId}, oglas {ListingId}. {Detalji}",
            vlasnikId, listingId, detalji);
    }

    private async Task<string?> PronadjiAdminaAsync()
    {
        var admini = await userManager.GetUsersInRoleAsync("Admin");
        return admini.FirstOrDefault()?.Id;
    }
}
