using System.Globalization;
using System.Net;

namespace UsluzionicaServer.Services;

/// <summary>
/// Šablon mejla „Uslužionica je na App Store-u" za listu čekanja — HTML i
/// tekstualna verzija iz ISTIH podataka, da dry-run pokaže tačno ono što ode.
///
/// TEKST PRATI SERVER, NE ŽELJE. Svaka rečenica o tokenima odgovara pravilu u
/// kodu:
///   • bonus i rok      → ListaCekanjaBonus (Waitlist:BonusTokens, ClaimDays);
///                        pominje se SAMO kad za tu prijavu bonus još važi
///   • „istom adresom"  → bonus ide na potvrđenu adresu sa liste; Google i
///                        Apple prijava je potvrđuju same (ExternalLoginService)
///   • Apple relej      → „Sakrij moju adresu" daje …@privaterelay.appleid.com,
///                        koja nije na listi — zato rečenica o tome
///   • preporuke        → ReferralService (Referral:SignupRewardTokens i
///                        ProviderActivationRewardTokens): prva rata kad
///                        pozvani potvrdi adresu, druga kad postane uslugodavac
///
/// Stil je isti kao verifikacioni mejl (EmailService): svetla pozadina, bela
/// kartica, brend plava. Bez slika — mnogi klijenti ih blokiraju, a mejl mora
/// da radi i bez njih. Tabele umesto div-ova zbog Outlook-a.
/// </summary>
public static class ListaCekanjaMejlSablon
{
    /// <param name="Email">Adresa primaoca (ide u tekst — „ovom adresom").</param>
    /// <param name="IzlazakIos">Datum izlaska na iOS-u (Waitlist:IosLaunchDate).</param>
    /// <param name="Bonus">Iznos bonusa, ili null kad za ovu prijavu bonus više ne važi.</param>
    /// <param name="BonusRok">Poslednji dan za nalog (null = rok još ne teče).</param>
    public sealed record Podaci(
        string    Email,
        DateTime  IzlazakIos,
        string    AppStoreUrl,
        string?   GooglePlayUrl,
        decimal?  Bonus,
        DateTime? BonusRok,
        decimal   PozivPotvrda,
        decimal   PozivUslugodavac,
        string    OdjavaUrl);

    /// <summary>Upadljiva zamena dok App:AppStoreUrl nije upisan. Pravo slanje je tada odbijeno.</summary>
    public const string AppStorePlaceholder = "https://apps.apple.com/POPUNI-App__AppStoreUrl";

    private static readonly string[] Meseci =
    [
        "januara", "februara", "marta", "aprila", "maja", "juna",
        "jula", "avgusta", "septembra", "oktobra", "novembra", "decembra"
    ];

    /// <summary>„6. novembra 2026." — ručno, jer kontejner nema garantovane srpske kulture.</summary>
    public static string Datum(DateTime d) => $"{d.Day}. {Meseci[d.Month - 1]} {d.Year}.";

    private static string Broj(decimal x) => x.ToString("0.##", CultureInfo.InvariantCulture);

    private static string E(string s) => WebUtility.HtmlEncode(s);

    public static string Naslov(Podaci p) => p.Bonus is { } b
        ? $"Uslužionica je na App Store-u — tvojih {Broj(b)} tokena te čeka"
        : "Uslužionica je na App Store-u";

    // ── Tekstovi (jedno mesto za obe verzije) ──────────────────────────────

    private static string Uvod(Podaci p) =>
        "Zdravo! Na listi čekanja si, a obećali smo da ćemo ti se javiti. " +
        $"Od {Datum(p.IzlazakIos)} Uslužionica je na App Store-u: nađeš majstora, frizera ili " +
        "instrukcije u svom gradu, sa cenom i ocenama.";

    private static string BonusNaslov(decimal b) => $"{Broj(b)} tokena te čeka na startu";

    private static string BonusTekst(Podaci p, decimal b) =>
        $"Pošto si na listi čekanja, na startu dobijaš {Broj(b)} tokena. " +
        "Otvori nalog u aplikaciji ovom adresom i tokeni stižu sami čim je adresa potvrđena " +
        "(link za potvrdu stiže mejlom, a uz prijavu preko Google-a ili Apple-a ni toga nema).";

    private const string BonusApple =
        "Ako ulaziš preko Apple-a, izaberi da podeliš adresu, a ne da je sakriješ — " +
        "skrivenu adresu ne možemo da povežemo sa listom.";

    private static string BonusRokTekst(DateTime rok) => $"Bonus važi za nalog otvoren do {Datum(rok)}";

    private const string BezBonusa =
        "Nalog otvaraš za minut, mejlom ili preko Google-a ili Apple-a.";

    private const string PozivNaslov = "Zaradi još tokena: pozovi prijatelje";

    private static string PozivTekst(Podaci p) =>
        "U aplikaciji otvori Profil → Pozovi prijatelje i podeli svoj kod ili link. " +
        $"Kad se prijatelj registruje sa tvojim kodom i potvrdi adresu, dobijaš {Broj(p.PozivPotvrda)} " +
        $"tokena, a još {Broj(p.PozivUslugodavac)} kad postane uslugodavac.";

    private const string Tokeni =
        "Tokene ponudiš uslugodavcu pri dogovoru i on ti za njih spusti cenu, ili ih uložiš u isticanje oglasa.";

    private static string Razlog(Podaci p) =>
        $"Ovu poruku dobijaš jer je adresa {p.Email} upisana na listu čekanja na usluzionica.rs. " +
        "Ovo je obaveštenje o izlasku koje smo obećali; newsletter ne šaljemo.";

    // ── HTML ───────────────────────────────────────────────────────────────

    public static string Html(Podaci p)
    {
        var bonus = p.Bonus is { } b
            ? $"""
              <tr><td style="padding:0 40px 8px;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#EEF3FF;border-radius:12px;">
                  <tr><td style="padding:20px 22px;">
                    <p style="margin:0 0 8px;font-size:16px;font-weight:700;color:#0B1220;">🎁 {E(BonusNaslov(b))}</p>
                    <p style="margin:0 0 10px;font-size:14px;line-height:1.6;color:#334155;">{E(BonusTekst(p, b))}</p>
                    <p style="margin:0 0 10px;font-size:14px;line-height:1.6;color:#334155;">Adresa: <strong style="color:#0B1220;">{E(p.Email)}</strong>{(p.BonusRok is { } rok ? "<br>" + E(BonusRokTekst(rok)) : "")}</p>
                    <p style="margin:0;font-size:13px;line-height:1.6;color:#64748B;">{E(BonusApple)}</p>
                  </td></tr>
                </table>
              </td></tr>
              """
            : $"""
              <tr><td style="padding:0 40px 8px;">
                <p style="margin:0;font-size:15px;line-height:1.6;color:#334155;">{E(BezBonusa)}</p>
              </td></tr>
              """;

        var play = string.IsNullOrWhiteSpace(p.GooglePlayUrl)
            ? ""
            : $"""
              <tr><td style="padding:0 40px 24px;">
                <p style="margin:0;font-size:14px;line-height:1.6;color:#334155;">Imaš Android? Uslužionica je i na <a href="{E(p.GooglePlayUrl)}" style="color:#2F6BFF;font-weight:600;">Google Play-u</a>.</p>
              </td></tr>
              """;

        return $"""
            <!DOCTYPE html>
            <html lang="sr">
            <head>
              <meta charset="UTF-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <title>{E(Naslov(p))}</title>
            </head>
            <body style="margin:0;padding:0;background:#F7FAFC;font-family:Inter,Arial,sans-serif;">
              <div style="display:none;max-height:0;overflow:hidden;opacity:0;">{E(p.Bonus is { } pb ? $"Preuzmi aplikaciju i otvori nalog ovom adresom — {Broj(pb)} tokena te čeka." : "Preuzmi aplikaciju za iPhone.")}</div>
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#F7FAFC;">
                <tr><td align="center" style="padding:32px 16px;">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;background:#FFFFFF;border-radius:16px;box-shadow:0 2px 8px rgba(0,0,0,0.08);">
                    <tr><td style="padding:32px 40px 0;">
                      <p style="margin:0;font-size:18px;font-weight:800;letter-spacing:-0.02em;color:#2F6BFF;">Uslužionica</p>
                    </td></tr>
                    <tr><td style="padding:20px 40px 0;">
                      <h1 style="margin:0 0 14px;font-size:24px;line-height:1.25;color:#0B1220;">Stigli smo na App Store 🎉</h1>
                      <p style="margin:0;font-size:15px;line-height:1.6;color:#334155;">{E(Uvod(p))}</p>
                    </td></tr>
                    <tr><td style="padding:24px 40px;">
                      <a href="{E(p.AppStoreUrl)}" style="display:inline-block;padding:14px 32px;background:#2F6BFF;color:#FFFFFF;text-decoration:none;border-radius:12px;font-weight:700;font-size:15px;">Preuzmi na App Store-u</a>
                    </td></tr>
                    {bonus}
                    <tr><td style="padding:16px 40px 8px;">
                      <p style="margin:0 0 6px;font-size:16px;font-weight:700;color:#0B1220;">{E(PozivNaslov)}</p>
                      <p style="margin:0 0 10px;font-size:14px;line-height:1.6;color:#334155;">{E(PozivTekst(p))}</p>
                      <p style="margin:0 0 16px;font-size:14px;line-height:1.6;color:#334155;">{E(Tokeni)}</p>
                    </td></tr>
                    {play}
                    <tr><td style="padding:20px 40px 32px;border-top:1px solid #E2E8F0;">
                      <p style="margin:0;font-size:12px;line-height:1.6;color:#94A3B8;">{E(Razlog(p))} <a href="{E(p.OdjavaUrl)}" style="color:#64748B;text-decoration:underline;">Odjavi se jednim klikom</a>.</p>
                    </td></tr>
                  </table>
                </td></tr>
              </table>
            </body>
            </html>
            """;
    }

    // ── Tekst ──────────────────────────────────────────────────────────────

    public static string Tekst(Podaci p)
    {
        var r = new System.Text.StringBuilder();
        r.AppendLine("Stigli smo na App Store!");
        r.AppendLine();
        r.AppendLine(Uvod(p));
        r.AppendLine();
        r.AppendLine($"Preuzmi na App Store-u: {p.AppStoreUrl}");
        r.AppendLine();

        if (p.Bonus is { } b)
        {
            r.AppendLine(BonusNaslov(b).ToUpperInvariant());
            r.AppendLine(BonusTekst(p, b));
            r.AppendLine($"Adresa: {p.Email}");
            if (p.BonusRok is { } rok) r.AppendLine(BonusRokTekst(rok));
            r.AppendLine(BonusApple);
        }
        else
        {
            r.AppendLine(BezBonusa);
        }

        r.AppendLine();
        r.AppendLine(PozivNaslov.ToUpperInvariant());
        r.AppendLine(PozivTekst(p));
        r.AppendLine(Tokeni);

        if (!string.IsNullOrWhiteSpace(p.GooglePlayUrl))
        {
            r.AppendLine();
            r.AppendLine($"Imaš Android? Uslužionica je i na Google Play-u: {p.GooglePlayUrl}");
        }

        r.AppendLine();
        r.AppendLine("—");
        r.AppendLine(Razlog(p));
        r.AppendLine($"Odjava jednim klikom: {p.OdjavaUrl}");
        return r.ToString();
    }
}
