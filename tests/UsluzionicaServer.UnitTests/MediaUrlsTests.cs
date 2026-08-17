using UsluzionicaServer.Infrastructure.Media;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Štiti pravilo: slike se u bazi čuvaju RELATIVNO, a pun URL se sastavlja tek
/// pri serijalizaciji. Bez toga se domen razvojne mašine peče u podatke i prvo
/// puštanje na pravi domen polomi sve postojeće slike.
/// </summary>
public class MediaUrlsTests
{
    // ── ToRelative ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://localhost:7176/uploads/listings/3/a.jpg", "/uploads/listings/3/a.jpg")]
    [InlineData("http://api.usluzionica.rs/uploads/avatars/x.png", "/uploads/avatars/x.png")]
    [InlineData("HTTPS://LOCALHOST:7176/uploads/covers/c.jpg",     "/uploads/covers/c.jpg")]
    public void ToRelative_KadaJePunUrl_SkidaShemuIHost(string input, string expected)
    {
        MediaUrls.ToRelative(input).Should().Be(expected);
    }

    [Fact]
    public void ToRelative_KadaJeVecRelativan_NeMenjaVrednost()
    {
        // Idempotentnost je bitna: migracija podataka i kod za upis mogu
        // pozvati ToRelative nad istom vrednošću više puta.
        const string relative = "/uploads/listings/3/a.jpg";

        MediaUrls.ToRelative(relative).Should().Be(relative);
        MediaUrls.ToRelative(MediaUrls.ToRelative(relative)).Should().Be(relative);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToRelative_KadaJePrazno_VracaNull(string? input)
    {
        MediaUrls.ToRelative(input).Should().BeNull();
    }

    // ── ToAbsolute ─────────────────────────────────────────────────────────

    [Fact]
    public void ToAbsolute_KadaJeRelativan_DodajeBaseUrl()
    {
        MediaUrls.ToAbsolute("/uploads/listings/3/a.jpg", "https://api.usluzionica.rs")
            .Should().Be("https://api.usluzionica.rs/uploads/listings/3/a.jpg");
    }

    [Fact]
    public void ToAbsolute_KadaBaseUrlImaKosuCrtuNaKraju_NeDupliraJe()
    {
        MediaUrls.ToAbsolute("/uploads/a.jpg", "https://api.usluzionica.rs/")
            .Should().Be("https://api.usluzionica.rs/uploads/a.jpg");
    }

    [Fact]
    public void ToAbsolute_KadaJeSpoljniUrl_ProsledjujeGaNetaknutog()
    {
        // Bitno za OAuth (Faza 2): avatar sa Google naloga je pun URL na tuđem
        // domenu i NE SME dobiti naš prefiks.
        const string google = "https://lh3.googleusercontent.com/a/ABC123";

        MediaUrls.ToAbsolute(google, "https://api.usluzionica.rs").Should().Be(google);
    }

    [Fact]
    public void ToAbsolute_KadaJeNull_VracaNull()
    {
        MediaUrls.ToAbsolute(null, "https://api.usluzionica.rs").Should().BeNull();
    }
}
