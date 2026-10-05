using UsluzionicaServer.Infrastructure.Media;

namespace UsluzionicaServer.Infrastructure.ExternalAuth;

/// <summary>
/// Preuzima profilnu sliku sa Google-a ili Facebook-a na NAŠ server.
///
/// ZAŠTO PREUZIMANJE, A NE ČUVANJE NJIHOVE ADRESE
///
///   • Facebook-ove adrese slika su potpisane i ISTIČU posle nekoliko
///     nedelja — avatar bi jednog dana jednostavno nestao.
///   • Slika učitana sa njihovog servera javlja Google-u/Facebook-u svaki
///     prikaz profila: ko je gledao čiji profil i kada.
///   • Preuzeta slika prolazi ISTU proveru kao avatar koji korisnik sam
///     otpremi (potpis formata, tragovi skripte, provera sadržaja).
///
/// BEZBEDNOST — SSRF
///
/// Server ovde otvara adresu koju nije sam izmislio. Iako stiže od provajdera,
/// preuzima se samo sa njihovih CDN domena, samo preko HTTPS-a, i svako
/// preusmerenje se proverava RUČNO: automatsko praćenje bi moglo da odvede
/// zahtev na adresu unutar naše mreže (baza, Redis) pre nego što bilo šta
/// stigne da proveri gde se završio.
/// </summary>
public sealed class ProfilnaSlikaProvajdera(HttpClient http, ILogger<ProfilnaSlikaProvajdera> logger)
{
    /// <summary>
    /// Domeni sa kojih se slike smeju preuzimati. Sa vodećom tačkom — tako
    /// <c>evilgoogleusercontent.com</c> ne prolazi kao <c>googleusercontent.com</c>.
    ///
    ///   googleusercontent.com — Google profilne slike
    ///   fbsbx.com             — Facebook-ova adresa slike iz Graph API-ja
    ///   fbcdn.net             — Facebook CDN, kuda fbsbx preusmerava
    /// </summary>
    private static readonly string[] DozvoljeniDomeni =
        [".googleusercontent.com", ".fbsbx.com", ".fbcdn.net"];

    private const int MaksPreusmerenja = 3;

    public static bool JeDozvoljena(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        DozvoljeniDomeni.Any(d => uri.Host.EndsWith(d, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Bajtovi slike, ili null ako adresa nije dozvoljena, odgovor nije uspeo
    /// ili je slika prevelika. Nikad ne baca zbog mreže — slika je ukras, a
    /// njen izostanak ne sme da obori registraciju.
    /// </summary>
    public async Task<byte[]?> PreuzmiAsync(string? url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var adresa))
            return null;

        try
        {
            for (var korak = 0; korak <= MaksPreusmerenja; korak++)
            {
                if (!JeDozvoljena(adresa))
                {
                    logger.LogWarning("Profilna slika odbijena: nedozvoljen domen {Host}.", adresa.Host);
                    return null;
                }

                using var odgovor = await http.GetAsync(adresa, HttpCompletionOption.ResponseHeadersRead, ct);

                if ((int)odgovor.StatusCode is >= 300 and < 400 && odgovor.Headers.Location is { } dalje)
                {
                    adresa = dalje.IsAbsoluteUri ? dalje : new Uri(adresa, dalje);
                    continue;
                }

                if (!odgovor.IsSuccessStatusCode)
                    return null;

                if (odgovor.Content.Headers.ContentLength > ImageUploads.MaxAvatarBytes)
                    return null;

                return await ProcitajOgranicenoAsync(odgovor.Content, ct);
            }

            logger.LogWarning("Profilna slika odbijena: previše preusmerenja.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Profilna slika nije preuzeta.");
            return null;
        }
    }

    /// <summary>
    /// Čita najviše <see cref="ImageUploads.MaxAvatarBytes"/>. Content-Length se
    /// ne sme uzeti zdravo za gotovo — može da izostane ili da laže, a server bi
    /// tada u memoriju učitao koliko god da stigne.
    /// </summary>
    private static async Task<byte[]?> ProcitajOgranicenoAsync(HttpContent sadrzaj, CancellationToken ct)
    {
        await using var ulaz  = await sadrzaj.ReadAsStreamAsync(ct);
        using var       izlaz = new MemoryStream();

        var  bafer   = new byte[81920];
        long ukupno  = 0;
        int  procitano;

        while ((procitano = await ulaz.ReadAsync(bafer, ct)) > 0)
        {
            ukupno += procitano;
            if (ukupno > ImageUploads.MaxAvatarBytes)
                return null;

            izlaz.Write(bafer, 0, procitano);
        }

        return ukupno == 0 ? null : izlaz.ToArray();
    }
}
