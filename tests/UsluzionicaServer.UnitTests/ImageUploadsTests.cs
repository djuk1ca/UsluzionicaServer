using Microsoft.AspNetCore.Http;
using UsluzionicaServer.Infrastructure.Media;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Štiti pravilo: ni ekstenzija ni format se NIKAD ne uzimaju od klijenta.
///
/// Napad koji je ovo zatvorilo: upload sa imenom "payload.html" i header-om
/// "image/jpeg" pravio je fajl <c>{guid}.html</c> u <c>wwwroot</c>, koji se
/// zatim servirao kao <c>text/html</c> — stored XSS na domenu API-ja.
/// </summary>
public class ImageUploadsTests
{
    private const long MaxBytes = 10 * 1024 * 1024;

    // ── Pomoćnici ──────────────────────────────────────────────────────────

    private static IFormFile File(
        byte[] sadrzaj,
        string imeFajla    = "slika.jpg",
        string contentType = "image/jpeg")
    {
        var stream = new MemoryStream(sadrzaj);

        return new FormFile(stream, 0, sadrzaj.Length, "file", imeFajla)
        {
            Headers     = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    /// <summary>Validno JPEG zaglavlje plus proizvoljno telo.</summary>
    private static byte[] Jpeg(string? telo = null) =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0, 0, 0, 0, 0, 0, .. Bajtovi(telo)];

    private static byte[] Png(string? telo = null) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, .. Bajtovi(telo)];

    private static byte[] Webp() =>
        [.. "RIFF"u8.ToArray(), 0, 0, 0, 0, .. "WEBP"u8.ToArray(), 0, 0, 0, 0];

    private static byte[] Bajtovi(string? s) =>
        s is null ? [] : System.Text.Encoding.ASCII.GetBytes(s);

    // ── Prepoznavanje formata iz sadržaja ──────────────────────────────────

    [Fact]
    public async Task Jpeg_DajeJpgEkstenziju()
    {
        var (ext, error) = await ImageUploads.ValidateAsync(File(Jpeg()), MaxBytes);

        ext.Should().Be(".jpg");
        error.Should().BeNull();
    }

    [Fact]
    public async Task Png_DajePngEkstenziju()
    {
        var (ext, _) = await ImageUploads.ValidateAsync(File(Png()), MaxBytes);

        ext.Should().Be(".png");
    }

    [Fact]
    public async Task Webp_DajeWebpEkstenziju()
    {
        var (ext, _) = await ImageUploads.ValidateAsync(File(Webp()), MaxBytes);

        ext.Should().Be(".webp");
    }

    // ── Ime fajla i Content-Type se ignorišu ───────────────────────────────

    [Theory]
    [InlineData("payload.html")]
    [InlineData("payload.svg")]
    [InlineData("payload.aspx")]
    [InlineData("payload.php")]
    [InlineData("bez-ekstenzije")]
    public async Task OpasnaEkstenzijaUImenu_NeUticeNaRezultat(string imeFajla)
    {
        var (ext, error) = await ImageUploads.ValidateAsync(
            File(Jpeg(), imeFajla), MaxBytes);

        // Sadržaj JESTE JPEG, pa fajl prolazi — ali ekstenziju biramo mi.
        ext.Should().Be(".jpg");
        error.Should().BeNull();
    }

    [Fact]
    public async Task LaziranContentType_NePomazeAkoSadrzajNijeSlika()
    {
        var html = System.Text.Encoding.ASCII.GetBytes("<html><body>zdravo</body></html>");

        var (ext, error) = await ImageUploads.ValidateAsync(
            File(html, "slika.jpg", "image/jpeg"), MaxBytes);

        ext.Should().BeNull();
        error.Should().Be("Dozvoljeni formati: JPEG, PNG, WebP.");
    }

    [Fact]
    public async Task TacanContentTypeAliSadrzajNijeSlika_Odbija()
    {
        var (ext, _) = await ImageUploads.ValidateAsync(
            File([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B]),
            MaxBytes);

        ext.Should().BeNull();
    }

    // ── Skripta sakrivena u validnoj slici ────────────────────────────────

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<?php system($_GET['c']); ?>")]
    [InlineData("<SCRIPT>alert(1)</SCRIPT>")]
    [InlineData("<iframe src=x>")]
    [InlineData("javascript:alert(1)")]
    public async Task PayloadDopisanIzaSlike_Odbija(string payload)
    {
        var (ext, error) = await ImageUploads.ValidateAsync(
            File(Jpeg(payload)), MaxBytes);

        ext.Should().BeNull();
        error.Should().Be("Slika sadrži nedozvoljen sadržaj.");
    }

    [Fact]
    public async Task PayloadPrekoGraniceKomada_Odbija()
    {
        // Marker namerno pada tačno preko granice od 32 KB, da se proveri da
        // preklapanje bafera radi. Bez njega bi ovaj payload promakao.
        var punjenje = new string('A', 32 * 1024 - 3);

        var (ext, _) = await ImageUploads.ValidateAsync(
            File(Jpeg(punjenje + "<script>x</script>")), MaxBytes);

        ext.Should().BeNull();
    }

    [Fact]
    public async Task CistaSlikaBezPayloada_Prolazi()
    {
        var (ext, error) = await ImageUploads.ValidateAsync(
            File(Jpeg(new string('A', 64 * 1024))), MaxBytes);

        ext.Should().Be(".jpg");
        error.Should().BeNull();
    }

    // ── Veličina ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PrazanFajl_Odbija()
    {
        var (ext, error) = await ImageUploads.ValidateAsync(File([]), MaxBytes);

        ext.Should().BeNull();
        error.Should().Be("Fajl je prazan.");
    }

    [Fact]
    public async Task PrevelikFajl_Odbija()
    {
        var (ext, error) = await ImageUploads.ValidateAsync(
            File(Jpeg(new string('A', 2048))), maxBytes: 1024);

        ext.Should().BeNull();
        error.Should().Contain("ne sme biti veća");
    }

    [Fact]
    public async Task FajlKraciOdZaglavlja_Odbija()
    {
        var (ext, _) = await ImageUploads.ValidateAsync(
            File([0xFF, 0xD8, 0xFF]), MaxBytes);

        ext.Should().BeNull();
    }
}
