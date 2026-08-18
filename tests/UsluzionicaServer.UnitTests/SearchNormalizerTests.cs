using System.Globalization;
using UsluzionicaServer.Infrastructure.Search;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Štiti pravilo: upit i sadržaj oglasa moraju završiti u ISTOM obliku, bez
/// obzira na dijakritiku i pismo. Ako se preklapanje pokvari, pretraga tiho
/// prestaje da nalazi rezultate — bez ijedne greške u logu.
/// </summary>
public class SearchNormalizerTests
{
    // ── Dijakritika ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Čačak",     "cacak")]
    [InlineData("Šišanje",   "sisanje")]
    [InlineData("Žarko",     "zarko")]
    [InlineData("Ćira",      "cira")]
    [InlineData("ČĆŠŽ",      "ccsz")]
    public void Fold_SkidaSrpskuDijakritiku(string input, string expected)
    {
        SearchNormalizer.Fold(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Đorđe",  "djordje")]
    [InlineData("đubre",  "djubre")]
    [InlineData("ĐAK",    "djak")]
    public void Fold_PreslovljavaDjUDvaZnaka(string input, string expected)
    {
        // ZAMKA: đ (U+0111) NEMA Unicode dekompoziciju — NFD ga ne dira.
        // Bez eksplicitne mape bi ispao kroz whitelist i tiho nestao,
        // pa bi "Đorđe" postalo "ore".
        SearchNormalizer.Fold(input).Should().Be(expected);
    }

    // ── Ćirilica ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("фризер",   "frizer")]
    [InlineData("Београд",  "beograd")]
    [InlineData("Ђорђе",    "djordje")]
    [InlineData("Њива",     "njiva")]
    [InlineData("Љубав",    "ljubav")]
    [InlineData("џеп",      "dzep")]
    [InlineData("ћевапи",   "cevapi")]
    [InlineData("шминка",   "sminka")]
    public void Fold_PreslovljavaCirilicuULatinicu(string input, string expected)
    {
        // Ni jedno ćirilično slovo nema NFD dekompoziciju — sve ide kroz mapu.
        SearchNormalizer.Fold(input).Should().Be(expected);
    }

    [Fact]
    public void Fold_CirilicaILatinicaDajuIstiRezultat()
    {
        // Ovo je suština: svejedno je kojim pismom su upit i oglas napisani.
        SearchNormalizer.Fold("фризер").Should().Be(SearchNormalizer.Fold("frizer"));
        SearchNormalizer.Fold("Ђорђе").Should().Be(SearchNormalizer.Fold("Đorđe"));
    }

    // ── Turski I ───────────────────────────────────────────────────────────

    [Fact]
    public void Fold_PodTurskomKulturom_NeGubiSlovoI()
    {
        // ZAMKA: pod tr-TR, "I".ToLower() daje 'ı' (U+0131, beztačkasto i),
        // koje bi whitelist [a-z0-9] obrisao → "znajmljvanje".
        // Zato kod koristi ToLowerInvariant.
        //
        // Kultura se menja na nivou niti da test ne zavisi od podešavanja
        // mašine na kojoj se izvršava.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            SearchNormalizer.Fold("IZNAJMLJIVANJE").Should().Be("iznajmljivanje");
            SearchNormalizer.Fold("Instalacija").Should().Be("instalacija");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Fold_TurskoIsaTackom_NeGubiSlovo()
    {
        // 'İ' (U+0130) je zamka jer ToLowerInvariant ne menja sam znak — NFD
        // ga razlaže na 'I' + combining dot, pa se tačka odbacuje a slovo
        // snižava u petlji.
        //
        // Napomena posle provere: prvobitno sam tvrdio da bi obrnut redosled
        // (mala slova pre NFD) dao "stanbul". To je NETAČNO za ovu
        // implementaciju — sabotažni test je to oborio. Oba redosleda daju
        // "istanbul" jer petlja svaki znak provlači kroz char.ToLowerInvariant.
        // Ovaj test dakle štiti IZLAZ, ne redosled koraka.
        SearchNormalizer.Fold("İSTANBUL").Should().Be("istanbul");
    }

    // ── Opšte ponašanje ────────────────────────────────────────────────────

    [Fact]
    public void Fold_JeIdempotentan()
    {
        // Isti tekst prolazi kroz Fold i pri upisu u bazu i pri obradi upita —
        // dvostruka primena ne sme menjati rezultat.
        var once  = SearchNormalizer.Fold("Šišanje i feniranje — Čačak");
        var twice = SearchNormalizer.Fold(once);

        twice.Should().Be(once);
    }

    [Theory]
    [InlineData("Beograd — Novi Beograd", "beograd novi beograd")]
    [InlineData("  puno   razmaka  ",     "puno razmaka")]
    [InlineData("tab\tи\nnovi red",       "tab i novi red")]
    public void Fold_SazimaRazmakeIPretvaraInterpunkciju(string input, string expected)
    {
        // Em-crta u imenima beogradskih opština mora postati razdvajač,
        // ne nestati bez traga.
        SearchNormalizer.Fold(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Fold_ZaPrazanUlaz_VracaPrazanString(string? input)
    {
        SearchNormalizer.Fold(input).Should().BeEmpty();
    }

    [Fact]
    public void Fold_ZadrzavaCifre()
    {
        SearchNormalizer.Fold("Servis 24/7").Should().Be("servis 24 7");
    }

    [Fact]
    public void Fold_HvataIStraneZnakoveKrozNfd()
    {
        // Ovo mapa ne pokriva — dolazi besplatno iz NFD dekompozicije.
        SearchNormalizer.Fold("café naïve").Should().Be("cafe naive");
    }

    // ── Tokenizacija ───────────────────────────────────────────────────────

    [Fact]
    public void Tokenize_DeliNaReci()
    {
        SearchNormalizer.Tokenize("Frizerski salon — Čačak")
            .Should().Equal("frizerski", "salon", "cacak");
    }

    [Fact]
    public void Tokenize_ZaPrazanUlaz_VracaPraznuListu()
    {
        SearchNormalizer.Tokenize("   ").Should().BeEmpty();
    }

    // ── Varijante za đ ─────────────────────────────────────────────────────

    [Fact]
    public void DjVariants_ZaTokenSaDj_VracaIVarijantuBezJ()
    {
        // Korisnik „Đorđe" kuca na dva česta načina:
        //   "Djordje" → fold → "djordje"   (obe đ kao dj)
        //   "Dorde"   → fold → "dorde"     (obe đ kao d)
        // Generišu se ta DVA ekstrema, ne sve kombinacije.
        //
        // Mešoviti oblici ("Dordje" → "dordje") se ne generišu namerno —
        // broj kombinacija raste kao 2^n sa brojem đ u reči. Njih hvata
        // fuzzy sloj, jer je "dordje" na rastojanju 1 od "djordje".
        SearchNormalizer.DjVariants("djordje")
            .Should().BeEquivalentTo(["djordje", "dorde"]);
    }

    [Fact]
    public void DjVariants_ZaTokenBezDj_VracaSamoNjega()
    {
        SearchNormalizer.DjVariants("frizer").Should().Equal("frizer");
    }

    // ── Escape za LIKE ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("100%",   @"100\%")]
    [InlineData("a_b",    @"a\_b")]
    [InlineData("[test]", @"\[test]")]
    [InlineData(@"c:\x",  @"c:\\x")]
    public void EscapeLike_NeutralizujeSpecijalneZnakove(string input, string expected)
    {
        // Bez ovoga upit "%" vraća SVE oglase, a "[a-z]" se tumači kao opseg.
        SearchNormalizer.EscapeLike(input).Should().Be(expected);
    }
}
