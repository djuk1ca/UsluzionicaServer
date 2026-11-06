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
/// IZGLED PO BRENDU (design-system/colors_and_type.css): off-white podloga,
/// bela kartica, slate tekst, Primary Blue samo za naglaske. Glas iz README-a
/// design sistema: kratko, bez uzvičnika i emodžija.
///
/// SVETLI I TAMNI REŽIM. Klijenti tamni režim rade na tri načina, i mejl mora
/// da preživi sva tri:
///   1. Apple Mail / iOS Mail / Outlook.com poštuju `color-scheme` i
///      `@media (prefers-color-scheme: dark)` → ovde dobijaju sopstvenu tamnu
///      paletu (Deep Navy / Slate Blue iz brenda), klase u-*.
///   2. Gmail (iOS, Android) media upit ignoriše i SAM obrće boje. Zato nema
///      obojenih površina koje se pri obrtanju pretvore u ljubičasto: kartice
///      su neutralne (bela, slate), a plava je samo tekst.
///   3. Slike se nikad ne obrću — logo i App Store bedž su PNG sa sopstvenom
///      pozadinom, pa izgledaju isto u oba režima.
///
/// Slike (logo, bedž) su na API-ju, wwwroot/mejl/ → {App:BaseUrl}/mejl/…. Gmail
/// ne prikazuje SVG, zato PNG u 2x rezoluciji. Bedž je zvanični Apple-ov
/// („Download on the App Store", HR), neizmenjen, visine 40 px (Apple-ov
/// minimum) i sa razmakom od bar četvrtine visine oko sebe.
/// </summary>
public static class ListaCekanjaMejlSablon
{
    /// <param name="Email">Adresa primaoca (ide u tekst — „ovom adresom").</param>
    /// <param name="IzlazakIos">Datum izlaska na iOS-u (Waitlist:IosLaunchDate).</param>
    /// <param name="AppStoreUrl">Apsolutni https link (proveren u ListaCekanjaMejl).</param>
    /// <param name="Bonus">Iznos bonusa, ili null kad za ovu prijavu bonus više ne važi.</param>
    /// <param name="BonusRok">Poslednji dan za nalog (null = rok još ne teče).</param>
    /// <param name="SlikeUrl">Koren slika mejla, npr. https://api.usluzionica.rs/mejl.</param>
    public sealed record Podaci(
        string    Email,
        DateTime  IzlazakIos,
        string    AppStoreUrl,
        string?   GooglePlayUrl,
        decimal?  Bonus,
        DateTime? BonusRok,
        decimal   PozivPotvrda,
        decimal   PozivUslugodavac,
        string    OdjavaUrl,
        string    SlikeUrl);

    /// <summary>
    /// Zamena SAMO za dry-run pregled dok App:AppStoreUrl nije upisan — uz
    /// upozorenje u izveštaju. Proba i pravo slanje se tada odbijaju, pa ovaj
    /// link nikad ne stiže ni u jedan inboks.
    /// </summary>
    public const string AppStorePlaceholder = "https://apps.apple.com/POPUNI-App__AppStoreUrl";

    /// <summary>Fajlovi u wwwroot/mejl/. Verzija u upitu da klijenti ne drže staru sliku.</summary>
    public const string LogoFajl  = "logo.png";
    public const string BedzFajl  = "app-store-hr.png";
    private const string Verzija  = "?v=2";

    private static readonly string[] Meseci =
    [
        "januara", "februara", "marta", "aprila", "maja", "juna",
        "jula", "avgusta", "septembra", "oktobra", "novembra", "decembra"
    ];

    /// <summary>„6. novembra 2026." — ručno, jer kontejner nema garantovane srpske kulture.</summary>
    public static string Datum(DateTime d) => $"{d.Day}. {Meseci[d.Month - 1]} {d.Year}.";

    private static string Broj(decimal x) => x.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Tekst u HTML-u.</summary>
    private static string E(string s) => WebUtility.HtmlEncode(s);

    /// <summary>
    /// Link u atributu href/src. Uri.AbsoluteUri prvo pretvara ne-ASCII znake
    /// (npr. „uslužionica" u putanji) u %-kodove, pa enkodovanje ispod menja
    /// samo &amp; i navodnike — tačno ono što HTML atribut traži, bez dvostrukog
    /// kodiranja i bez &amp;#382; u adresi.
    /// </summary>
    public static string Link(string url) =>
        WebUtility.HtmlEncode(Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsoluteUri : url);

    public static string Naslov(Podaci p) => p.Bonus is { } b
        ? $"Uslužionica je na App Store-u — tvojih {Broj(b)} tokena te čeka"
        : "Uslužionica je na App Store-u";

    // ── Tekstovi (jedno mesto za obe verzije) ──────────────────────────────

    private const string Glavni = "Uslužionica je na App Store-u";

    private static string Uvod(Podaci p) =>
        $"Na listi čekanja si, a ovo je obećano obaveštenje: od {Datum(p.IzlazakIos)} Uslužionicu " +
        "možeš da preuzmeš na iPhone. Nađeš majstora, frizera ili instrukcije u svom gradu, " +
        "sa cenom i ocenama, i dogovoriš se direktno.";

    private const string BonusOznaka = "Bonus sa liste čekanja";

    private static string BonusTekst(decimal b) =>
        $"Pošto si na listi čekanja, na startu dobijaš {Broj(b)} tokena. " +
        "Otvori nalog u aplikaciji ovom adresom i tokeni stižu sami čim je adresa potvrđena " +
        "(link za potvrdu stiže mejlom, a uz prijavu preko Google-a ili Apple-a ni toga nema).";

    private const string BonusApple =
        "Ako ulaziš preko Apple-a, izaberi da podeliš adresu, a ne da je sakriješ. " +
        "Skrivenu adresu ne možemo da povežemo sa listom.";

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

    /// <summary>
    /// Tamna paleta za klijente koji poštuju prefers-color-scheme. [data-ogsc]
    /// je Outlook.com u tamnom režimu. !important jer inline stilovi inače pobeđuju.
    /// </summary>
    private const string Stil = """
        :root { color-scheme: light dark; supported-color-schemes: light dark; }
        body { margin:0; padding:0; -webkit-text-size-adjust:100%; }
        a { text-decoration:none; }
        @media (prefers-color-scheme: dark) {
          .u-strana { background:#0B1220 !important; }
          .u-kartica { background:#162238 !important; border-color:#24324D !important; }
          .u-okvir { background:#0F1A2E !important; border-color:#24324D !important; }
          .u-crta { border-color:#24324D !important; }
          .u-naslov { color:#F1F5F9 !important; }
          .u-tekst { color:#CBD5E1 !important; }
          .u-blago { color:#94A3B8 !important; }
          .u-plava { color:#69A8FF !important; }
        }
        [data-ogsc] .u-naslov { color:#F1F5F9 !important; }
        [data-ogsc] .u-tekst { color:#CBD5E1 !important; }
        [data-ogsc] .u-blago { color:#94A3B8 !important; }
        [data-ogsc] .u-plava { color:#69A8FF !important; }
        @media (max-width:520px) {
          .u-unutra { padding-left:24px !important; padding-right:24px !important; }
        }
        """;

    private const string Font = "Outfit,-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif";

    public static string Html(Podaci p)
    {
        var slike = p.SlikeUrl.TrimEnd('/');
        var logo  = Link($"{slike}/{LogoFajl}{Verzija}");
        var bedz  = Link($"{slike}/{BedzFajl}{Verzija}");

        var bonus = p.Bonus is { } b
            ? $"""
              <tr><td class="u-unutra" style="padding:0 40px 28px;">
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" class="u-okvir" style="background:#F8FAFC;border:1px solid #E2E8F0;border-radius:16px;">
                  <tr><td style="padding:22px 24px;">
                    <p class="u-blago" style="margin:0 0 6px;font-size:12px;font-weight:600;letter-spacing:0.06em;text-transform:uppercase;color:#64748B;">{E(BonusOznaka)}</p>
                    <p class="u-plava" style="margin:0 0 12px;font-size:28px;line-height:1.15;font-weight:700;letter-spacing:-0.02em;color:#2F6BFF;">{E(Broj(b))} tokena</p>
                    <p class="u-tekst" style="margin:0 0 12px;font-size:15px;line-height:1.6;color:#334155;">{E(BonusTekst(b))}</p>
                    <p class="u-tekst" style="margin:0 0 12px;font-size:15px;line-height:1.6;color:#334155;">Adresa: <strong class="u-naslov" style="color:#0F172A;">{E(p.Email)}</strong>{(p.BonusRok is { } rok ? "<br>" + E(BonusRokTekst(rok)) : "")}</p>
                    <p class="u-blago" style="margin:0;font-size:13px;line-height:1.55;color:#64748B;">{E(BonusApple)}</p>
                  </td></tr>
                </table>
              </td></tr>
              """
            : $"""
              <tr><td class="u-unutra" style="padding:0 40px 28px;">
                <p class="u-tekst" style="margin:0;font-size:15px;line-height:1.6;color:#334155;">{E(BezBonusa)}</p>
              </td></tr>
              """;

        var play = string.IsNullOrWhiteSpace(p.GooglePlayUrl)
            ? ""
            : $"""
              <tr><td class="u-unutra" style="padding:0 40px 32px;">
                <p class="u-blago" style="margin:0;font-size:14px;line-height:1.6;color:#64748B;">Imaš Android? Uslužionica je i na <a href="{Link(p.GooglePlayUrl)}" class="u-plava" style="color:#2F6BFF;font-weight:600;text-decoration:none;">Google Play-u</a>.</p>
              </td></tr>
              """;

        var preheader = p.Bonus is { } pb
            ? $"Preuzmi aplikaciju i otvori nalog ovom adresom — {Broj(pb)} tokena te čeka."
            : "Preuzmi aplikaciju za iPhone.";

        return $"""
            <!DOCTYPE html>
            <html lang="sr" xmlns="http://www.w3.org/1999/xhtml">
            <head>
              <meta charset="UTF-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <meta name="color-scheme" content="light dark">
              <meta name="supported-color-schemes" content="light dark">
              <meta name="x-apple-disable-message-reformatting">
              <title>{E(Naslov(p))}</title>
              <link href="https://fonts.googleapis.com/css2?family=Outfit:wght@400;600;700&amp;display=swap" rel="stylesheet">
              <style>
            {Stil}
              </style>
            </head>
            <body class="u-strana" style="margin:0;padding:0;background:#F7FAFC;">
              <div style="display:none;max-height:0;overflow:hidden;opacity:0;mso-hide:all;">{E(preheader)}</div>
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" class="u-strana" style="background:#F7FAFC;">
                <tr><td align="center" style="padding:32px 12px;">
                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" class="u-kartica" style="max-width:560px;background:#FFFFFF;border:1px solid #E2E8F0;border-radius:24px;font-family:{Font};">

                    <tr><td class="u-unutra" style="padding:32px 40px 0;">
                      <table role="presentation" cellpadding="0" cellspacing="0" border="0"><tr>
                        <td style="vertical-align:middle;"><img src="{logo}" width="44" height="44" alt="" style="display:block;width:44px;height:44px;border:0;border-radius:10px;"></td>
                        <td class="u-naslov" style="vertical-align:middle;padding-left:12px;font-size:20px;font-weight:700;letter-spacing:-0.02em;color:#0F172A;font-family:{Font};">Uslužionica</td>
                      </tr></table>
                    </td></tr>

                    <tr><td class="u-unutra" style="padding:28px 40px 0;">
                      <h1 class="u-naslov" style="margin:0 0 14px;font-size:28px;line-height:1.2;font-weight:700;letter-spacing:-0.02em;color:#0F172A;font-family:{Font};">{E(Glavni)}</h1>
                      <p class="u-tekst" style="margin:0;font-size:16px;line-height:1.6;color:#334155;">{E(Uvod(p))}</p>
                    </td></tr>

                    <tr><td class="u-unutra" style="padding:24px 40px 28px;">
                      <a href="{Link(p.AppStoreUrl)}" style="display:inline-block;padding:0;border:0;text-decoration:none;">
                        <img src="{bedz}" width="127" height="40" alt="Preuzmi u App Storeu" style="display:block;width:127px;height:40px;border:0;">
                      </a>
                    </td></tr>

                    {bonus}

                    <tr><td class="u-unutra u-crta" style="padding:28px 40px 8px;border-top:1px solid #E2E8F0;">
                      <p class="u-naslov" style="margin:0 0 8px;font-size:17px;font-weight:700;letter-spacing:-0.01em;color:#0F172A;font-family:{Font};">{E(PozivNaslov)}</p>
                      <p class="u-tekst" style="margin:0 0 12px;font-size:15px;line-height:1.6;color:#334155;">{E(PozivTekst(p))}</p>
                      <p class="u-tekst" style="margin:0 0 24px;font-size:15px;line-height:1.6;color:#334155;">{E(Tokeni)}</p>
                    </td></tr>

                    {play}
                  </table>

                  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:560px;">
                    <tr><td style="padding:20px 24px 0;font-family:{Font};">
                      <p class="u-blago" style="margin:0;font-size:12px;line-height:1.6;color:#94A3B8;text-align:center;">{E(Razlog(p))}<br><a href="{Link(p.OdjavaUrl)}" class="u-blago" style="color:#64748B;text-decoration:underline;">Odjavi se jednim klikom</a></p>
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
        r.AppendLine(Glavni);
        r.AppendLine();
        r.AppendLine(Uvod(p));
        r.AppendLine();
        r.AppendLine($"Preuzmi na App Store-u: {p.AppStoreUrl}");
        r.AppendLine();

        if (p.Bonus is { } b)
        {
            r.AppendLine($"{BonusOznaka.ToUpperInvariant()}: {Broj(b)} TOKENA");
            r.AppendLine(BonusTekst(b));
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
