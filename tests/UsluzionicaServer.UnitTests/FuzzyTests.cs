using UsluzionicaServer.Infrastructure.Search;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Štiti pravilo: sitna greška u kucanju sme da prođe, a nepovezan pojam ne sme.
/// Fuzzy koji je previše popustljiv gori je od nikakvog — vraća smeće i
/// korisnik izgubi poverenje u pretragu.
/// </summary>
public class FuzzyTests
{
    // ── OSA rastojanje ─────────────────────────────────────────────────────

    [Fact]
    public void Osa_ZaIsteReci_VracaNulu()
    {
        Fuzzy.Osa("frizer", "frizer", 2).Should().Be(0);
    }

    [Fact]
    public void Osa_TranspozicijuNaplacujeKaoJednuIzmenu()
    {
        // KLJUČNO: „frizre" ↔ „frizer" je zamena mesta dva susedna slova —
        // najčešća greška pri kucanju. Čist Levenshtein je naplaćuje kao 2
        // (brisanje + umetanje), pa bi na dužini 6, gde je prag 1, ovaj par
        // ispao iz tolerancije. OSA je naplaćuje kao 1.
        Fuzzy.Osa("frizre", "frizer", 2).Should().Be(1);
    }

    [Theory]
    [InlineData("frizerr", "frizer", 1)]   // dodato slovo
    [InlineData("frizr",   "frizer", 1)]   // izostavljeno slovo
    [InlineData("frizes",  "frizer", 1)]   // pogrešno slovo
    public void Osa_ZaJednuGresku_VracaJedan(string a, string b, int expected)
    {
        Fuzzy.Osa(a, b, 2).Should().Be(expected);
    }

    [Fact]
    public void Osa_KadaJeRastojanjeIznadPraga_RanoIzlazi()
    {
        // Vraća max+1, ne pravo rastojanje — dovoljno je znati da je preveliko.
        Fuzzy.Osa("frizer", "vodoinstalater", 2).Should().BeGreaterThan(2);
    }

    [Fact]
    public void Osa_KadaSeDuzineMnogoRazlikuju_OdmahOdbija()
    {
        // Razlika dužina je donja granica rastojanja — jeftin i tačan odsek
        // pre nego što se uopšte krene u računanje matrice.
        Fuzzy.Osa("ab", "abcdefghij", 2).Should().BeGreaterThan(2);
    }

    [Theory]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    public void Osa_ZaPrazanString_VracaDuzinuDrugog(string a, string b, int expected)
    {
        Fuzzy.Osa(a, b, 5).Should().Be(expected);
    }

    // ── Pragovi po dužini ──────────────────────────────────────────────────

    [Theory]
    [InlineData(2, 0)]    // „do"
    [InlineData(3, 0)]    // „sto"  — na kratkim rečima tolerancija spaja nepovezano
    [InlineData(4, 1)]
    [InlineData(6, 1)]    // „frizer"
    [InlineData(7, 1)]
    [InlineData(8, 2)]
    [InlineData(14, 2)]   // „vodoinstalater"
    public void MaxDistance_RastesaDuzinomTokena(int length, int expected)
    {
        Fuzzy.MaxDistance(length).Should().Be(expected);
    }

    [Fact]
    public void MaxDistance_ZaKratkeReci_JeNula()
    {
        // Namerno: na dužini 4 rastojanje 1 spaja „kuca", „kuka", „muka",
        // „ruka" — nepovezane pojmove. Tolerancija ima smisla tek kad reč
        // ima dovoljno konteksta.
        Fuzzy.MaxDistance(3).Should().Be(0);
    }

    // ── Pigeonhole fragmenti ───────────────────────────────────────────────

    [Fact]
    public void PigeonholeFragments_ZaRastojanjeNula_VracaCeoToken()
    {
        Fuzzy.PigeonholeFragments("frizer", 0).Should().Equal("frizer");
    }

    [Fact]
    public void PigeonholeFragments_ZaRastojanjeJedan_DeliNaDva()
    {
        var fragments = Fuzzy.PigeonholeFragments("frizerr", 1);

        fragments.Should().HaveCount(2);
        string.Concat(fragments).Should().Be("frizerr", "delovi moraju pokriti ceo token");
    }

    [Fact]
    public void PigeonholeFragments_ZaRastojanjeDva_DeliNaTri()
    {
        var fragments = Fuzzy.PigeonholeFragments("vodoinstalter", 2);

        fragments.Should().HaveCount(3);
        string.Concat(fragments).Should().Be("vodoinstalter");
    }

    [Theory]
    [InlineData("frizerr", "frizer")]          // dodato slovo
    [InlineData("frizre",  "frizer")]          // transpozicija
    [InlineData("vodoinstalter", "vodoinstalater")]
    public void PigeonholeFragments_BarJedanDeoPostojiUCiljnojReci(string upit, string cilj)
    {
        // OVO JE DOKAZ KOREKTNOSTI PREFILTERA.
        //
        // Pigeonhole princip: ako se upit i cilj razlikuju za najviše d izmena,
        // a upit podelimo na d+1 disjunktnih delova, tih d izmena može
        // pokvariti najviše d delova — bar jedan mora ostati netaknut.
        //
        // Praktično: SQL prefilter `WHERE ... LIKE '%deo1%' OR LIKE '%deo2%'`
        // NE MOŽE promašiti pravi rezultat. Nije heuristika koja obično radi.
        var d         = Fuzzy.MaxDistance(upit.Length);
        var fragments = Fuzzy.PigeonholeFragments(upit, d);

        fragments.Should().Contain(f => cilj.Contains(f, StringComparison.Ordinal),
            $"pigeonhole garantuje da bar jedan od [{string.Join(", ", fragments)}] postoji u '{cilj}'");
    }

    [Fact]
    public void PigeonholeFragments_ZaKratakToken_NeVracaNeselektivneDelove()
    {
        // Delovi od 1-2 znaka pogađaju pola baze. Radije jedan kratak prefiks.
        var fragments = Fuzzy.PigeonholeFragments("abcd", 2);

        fragments.Should().OnlyContain(f => f.Length >= 3);
    }

    // ── Skor ───────────────────────────────────────────────────────────────

    [Fact]
    public void Similarity_ZaIdenticneReci_VracaJedan()
    {
        Fuzzy.Similarity("frizer", "frizer").Should().Be(1.0);
    }

    [Fact]
    public void Similarity_ZaRecKojaSadrziToken_JeVisoka()
    {
        // „frizer" u „frizerski" je jak pogodak — korisnik traži baš to.
        Fuzzy.Similarity("frizer", "frizerski").Should().BeGreaterThan(Fuzzy.MinScore);
    }

    [Fact]
    public void Similarity_ZaTipfeler_JeIznadPraga()
    {
        Fuzzy.Similarity("frizerr", "frizer").Should().BeGreaterThan(Fuzzy.MinScore);
        Fuzzy.Similarity("frizre",  "frizer").Should().BeGreaterThan(Fuzzy.MinScore);
    }

    [Theory]
    [InlineData("frizer", "vodoinstalater")]
    [InlineData("qwerty", "frizer")]
    [InlineData("asdfgh", "sisanje")]
    public void Similarity_ZaNepovezanePojmove_JeIspodPraga(string a, string b)
    {
        // NAJVAŽNIJI TEST U FAJLU. Fuzzy koji vraća smeće gori je od nikakvog —
        // korisnik izgubi poverenje u pretragu i prestane da je koristi.
        Fuzzy.Similarity(a, b).Should().BeLessThan(Fuzzy.MinScore);
    }

    [Fact]
    public void Similarity_ZaKratkeSlicneReci_NeSpajaNepovezano()
    {
        // „kuca" i „kuka" se razlikuju za jedno slovo, ali su nepovezani
        // pojmovi. Prag za dužinu 4 je 1, pa proveravamo da skor ostane nizak.
        var skor = Fuzzy.Similarity("kuca", "muka");

        skor.Should().BeLessThan(Fuzzy.MinScore);
    }
}
