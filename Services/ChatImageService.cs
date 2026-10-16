using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Conversations;
using UsluzionicaServer.Infrastructure.ChatMedia;
using UsluzionicaServer.Infrastructure.Media;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Services;

/// <summary>
/// Slike u četu: slanje (jedan zahtev = fajl + poruka), prikaz potpisanim
/// URL-om, admin prikaz kod prijave i brisanje. Plan: Obsidian „Plan — Slike
/// u četu" §4–§7.
///
/// Redosled pri slanju je namerno FAJL PA RED: ako baza padne posle upisa
/// fajla, fajl se briše odmah; ako ni to ne uspe, pokupi ga čistač siročića
/// (MessageCleanupService). Obrnuto bi ostala poruka koja pokazuje na ništa.
/// </summary>
public sealed class ChatImageService(
    AppDbContext                db,
    ChatMediaStore              store,
    ChatSender                  chat,
    BlockService                blokade,
    ImageModerationGate         moderacija,
    ILogger<ChatImageService>   logger)
{
    public const int NajduziOpis = 1000;

    /// <summary>Koliko dugo se slika iz prijavljene poruke čuva za admina.</summary>
    public static readonly TimeSpan ZadrzavanjePrijavljene = TimeSpan.FromDays(30);

    public async Task<(MessageDto? Poruka, string? Greska, bool NijePronadjeno)> PosaljiAsync(
        int razgovorId, string posiljalacId, IFormFile fajl, string? opis, CancellationToken ct = default)
    {
        var razgovor = await db.Conversations
            .FirstOrDefaultAsync(c => c.Id == razgovorId && (c.User1Id == posiljalacId || c.User2Id == posiljalacId), ct);
        if (razgovor is null) return (null, "Razgovor nije pronađen.", true);

        var posiljalac = await db.Users.FirstOrDefaultAsync(u => u.Id == posiljalacId, ct);
        if (posiljalac is null || !posiljalac.IsActive) return (null, "Nalog nije aktivan.", false);

        // Ista poruka kao za nepostojeći razgovor — ne odaje ko je koga blokirao.
        var primalacId = razgovor.User1Id == posiljalacId ? razgovor.User2Id : razgovor.User1Id;
        if (await blokade.JeBlokiranoAsync(posiljalacId, primalacId))
            return (null, "Razgovor nije pronađen.", true);

        var tekst = (opis ?? "").Trim();
        if (tekst.Length > NajduziOpis) return (null, $"Opis može imati najviše {NajduziOpis} znakova.", false);

        // Format po sadržaju (ne po ekstenziji), veličina, tragovi skripte.
        var (ekstenzija, greska) = await ImageUploads.ValidateAsync(fajl, store.NajviseBajtova);
        if (ekstenzija is null) return (null, greska, false);

        var (presuda, detalji) = await moderacija.PresudaAsync(fajl, posiljalacId, ct: ct);
        if (presuda == ImageVerdict.Reject)
        {
            logger.LogWarning("Slika u četu odbijena automatskom proverom. Korisnik {UserId}. {Detalji}", posiljalacId, detalji);
            return (null, "Slika ne može da se pošalje. Izaberi drugu fotografiju.", false);
        }

        byte[] bajtovi;
        await using (var ulaz = fajl.OpenReadStream())
        using (var mem = new MemoryStream())
        {
            await ulaz.CopyToAsync(mem, ct);
            bajtovi = mem.ToArray();
        }

        var (sirina, visina) = ImageDimensions.Read(bajtovi);
        var sada   = DateTime.UtcNow;
        var prilog = new MessageAttachment
        {
            Width       = sirina,
            Height      = visina,
            SizeBytes   = bajtovi.Length,
            NeedsReview = presuda == ImageVerdict.Review,
            HeldUntil   = presuda == ImageVerdict.Review ? sada + ZadrzavanjePrijavljene : null,
            CreatedAt   = sada
        };
        prilog.StoragePath = ChatMediaStore.RelativnaPutanja(prilog.Id, sada);

        await store.UpisiAsync(prilog.StoragePath, bajtovi, razgovor.Id, prilog.Id, ct);

        MessageDto poruka;
        try
        {
            poruka = await chat.PosaljiAsync(razgovor, posiljalac, tekst, prilog: prilog);
        }
        catch
        {
            store.Obrisi(prilog.StoragePath);
            throw;
        }

        if (presuda == ImageVerdict.Review)
            await moderacija.PrijaviSlikuIzCetaAsync(posiljalacId, poruka.Id, detalji, ct);

        return (poruka, null, false);
    }

    /// <summary>
    /// Bajtovi slike za potpisan URL. null za SVE neuspehe (loš potpis, istekao,
    /// nije učesnik, nema fajla) — kontroler vraća 404 i ne otkriva da prilog postoji.
    /// </summary>
    public async Task<byte[]?> ProcitajAsync(Guid id, string u, long exp, string sig, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(u) || !store.PotpisVazi(id, u, exp, sig)) return null;

        var p = await db.MessageAttachments.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new { a.StoragePath, a.Message.ConversationId, a.Message.Conversation.User1Id, a.Message.Conversation.User2Id })
            .FirstOrDefaultAsync(ct);
        if (p is null || (p.User1Id != u && p.User2Id != u)) return null;

        // Potpis je bio dobar, ali nalog možda više ne postoji ili je ugašen.
        if (!await db.Users.AnyAsync(x => x.Id == u && x.IsActive, ct)) return null;

        return await store.ProcitajAsync(p.StoragePath, p.ConversationId, id, ct);
    }

    /// <summary>Admin, kod prijave poruke — bez potpisanog URL-a (admin JWT).</summary>
    public async Task<byte[]?> ProcitajZaAdminaAsync(int porukaId, CancellationToken ct = default)
    {
        var p = await db.MessageAttachments.AsNoTracking()
            .Where(a => a.MessageId == porukaId)
            .Select(a => new { a.Id, a.StoragePath, a.Message.ConversationId })
            .FirstOrDefaultAsync(ct);
        return p is null ? null : await store.ProcitajAsync(p.StoragePath, p.ConversationId, p.Id, ct);
    }

    /// <summary>Brisanje naloga: slike koje je korisnik POSLAO nestaju odmah — fajl pa red.</summary>
    public async Task ObrisiPoslateAsync(string userId, CancellationToken ct = default)
    {
        var prilozi = await db.MessageAttachments
            .Where(a => a.Message.SenderId == userId)
            .Select(a => new { a.Id, a.StoragePath })
            .ToListAsync(ct);

        foreach (var p in prilozi)
            store.Obrisi(p.StoragePath);

        var ids = prilozi.Select(p => p.Id).ToList();
        if (ids.Count > 0)
            await db.MessageAttachments.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct);
    }

    /// <summary>Korisnik je prijavio poruku — slika ostaje adminu do 30 dana i posle roka od 14.</summary>
    public Task ZadrziAsync(int porukaId)
    {
        DateTime? do_ = DateTime.UtcNow + ZadrzavanjePrijavljene;
        return db.MessageAttachments.Where(a => a.MessageId == porukaId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.HeldUntil, do_));
    }

    /// <summary>Prijava rešena — zadržavanje prestaje, sledeće noći slika odlazi sa porukom.</summary>
    public Task OslobodiAsync(IEnumerable<int> porukeIds)
    {
        var ids = porukeIds.Distinct().ToList();
        DateTime? sada = DateTime.UtcNow;
        return ids.Count == 0
            ? Task.CompletedTask
            : db.MessageAttachments.Where(a => ids.Contains(a.MessageId))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.HeldUntil, sada));
    }

    /// <summary>MIME po prvim bajtovima — fajl je već prošao proveru formata pri slanju.</summary>
    public static string VrstaSadrzaja(ReadOnlySpan<byte> b) =>
        b.Length > 3 && b[0] == 0x89 && b[1] == 0x50 ? "image/png"
        : b.Length > 12 && b[8] == (byte)'W' && b[9] == (byte)'E' ? "image/webp"
        : "image/jpeg";
}
