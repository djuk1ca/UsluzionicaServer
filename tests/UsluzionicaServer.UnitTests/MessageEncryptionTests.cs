using System.Security.Cryptography;
using System.Text;
using UsluzionicaServer.Infrastructure;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Štiti šifrovanje poruka u bazi (AES-256-GCM).
///
///   • dešifrovanje vraća TAČNO original — i srpske dijakritike, i ćirilicu,
///     i emoji, koje UTF-8 kodira na više bajtova
///   • IZMENA šifrata se otkriva — to je ceo razlog prelaska sa CBC-a
///   • poruka je vezana za svoj razgovor
///   • stare CBC poruke se i dalje čitaju (14 dana prelaza)
///   • rotacija ključa ne uništava postojeće poruke
/// </summary>
public class MessageEncryptionTests
{
    private const int Razgovor = 42;

    /// <summary>Drugi validan 32-bajtni ključ (32 puta slovo 'b').</summary>
    private const string DrugiKljuc = "YmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmI=";

    /// <summary>Treći validan ključ (32 puta slovo 'c').</summary>
    private const string TreciKljuc = "Y2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2M=";

    private static MessageEncryption CreateSut(
        string? kljucBase64 = null, params (string Kljuc, string Vrednost)[] dodatno)
    {
        var values = TestConfig.ValidBase();
        if (kljucBase64 is not null) values["Encryption:MessageKey"] = kljucBase64;
        foreach (var (k, v) in dodatno) values[k] = v;
        return new MessageEncryption(TestConfig.From(values));
    }

    // ── Osnovno ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Zdravo, kako si?")]
    [InlineData("Čeka me šišanje u četvrtak — Đorđe, Žarko, Ćira")]
    [InlineData("Емоџи и ћирилица 🔧🏠")]
    [InlineData("")]
    public void EncryptDecrypt_VracaTacanOriginal(string plaintext)
    {
        var sut = CreateSut();

        sut.Decrypt(sut.Encrypt(plaintext, Razgovor), Razgovor).Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_ZaIstiTekstDajeRazliciteRezultate()
    {
        // Nov nonce za svaku poruku. Da nije tako, napadač sa pristupom bazi bi
        // video da su dve poruke identične — a kod GCM-a ponovljen nonce sa istim
        // ključem otkriva i sam ključ za autentifikaciju.
        var sut = CreateSut();

        var a = sut.Encrypt("ista poruka", Razgovor);
        var b = sut.Encrypt("ista poruka", Razgovor);

        a.Should().NotBe(b);
        sut.Decrypt(a, Razgovor).Should().Be(sut.Decrypt(b, Razgovor));
    }

    [Fact]
    public void Encrypt_NosiVerzijuIIdKljuca()
    {
        CreateSut().Encrypt("x", Razgovor).Should().StartWith("v2.k0.");
    }

    // ── Integritet — razlog prelaska na GCM ────────────────────────────────

    [Fact]
    public void IzmenjenSifrat_SeOtkriva()
    {
        // NAJVAŽNIJI TEST U KLASI. Sa CBC-om bez MAC-a izmena bajta je davala
        // izmenjen tekst. Sa GCM-om mora da padne.
        var sut     = CreateSut();
        var zapis   = sut.Encrypt("Cena je 3.500 dinara", Razgovor);
        var podaci  = Convert.FromBase64String(zapis["v2.k0.".Length..]);
        podaci[15] ^= 0x01;   // jedan bit u šifratu, odmah iza nonce-a
        var izmenjen = "v2.k0." + Convert.ToBase64String(podaci);

        var act = () => sut.Decrypt(izmenjen, Razgovor);

        act.Should().Throw<CryptographicException>();
        sut.SafeDecrypt(izmenjen, Razgovor).Should().Be(MessageEncryption.Necitljiva);
    }

    [Fact]
    public void PorukaIzDrugogRazgovora_SeOdbija()
    {
        // Sa pristupom bazi se šifrat ne može premestiti u drugi razgovor.
        var sut   = CreateSut();
        var zapis = sut.Encrypt("Dolazim u 17h", conversationId: 1);

        var act = () => sut.Decrypt(zapis, conversationId: 2);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void PorukaSaDrugimKljucem_JeNecitljiva()
    {
        var zapis = CreateSut().Encrypt("tajna poruka", Razgovor);

        CreateSut(DrugiKljuc).SafeDecrypt(zapis, Razgovor).Should().Be(MessageEncryption.Necitljiva);
    }

    [Fact]
    public void Smece_NeObaraEkran()
    {
        var sut = CreateSut();

        sut.SafeDecrypt("ovo-nije-validan-base64!!!", Razgovor).Should().Be(MessageEncryption.Necitljiva);
        sut.SafeDecrypt("v2.k0.", Razgovor).Should().Be(MessageEncryption.Necitljiva);
        sut.SafeDecrypt("v2.k9.AAAA", Razgovor).Should().Be(MessageEncryption.Necitljiva);
    }

    // ── Prelaz sa CBC-a ────────────────────────────────────────────────────

    [Fact]
    public void StaraCbcPoruka_SeIDaljeCita()
    {
        // Poruke iz vremena pre GCM-a ostaju čitljive 14 dana, dok ih
        // MessageCleanupService ne obriše.
        var kljuc = Convert.FromBase64String(TestConfig.ValidBase()["Encryption:MessageKey"]!);
        var stara = SifrujKaoStariKod("Poruka pre prelaska — šifrovana CBC-om", kljuc);

        CreateSut().Decrypt(stara, Razgovor).Should().Be("Poruka pre prelaska — šifrovana CBC-om");
    }

    /// <summary>Tačno ono što je radio stari Encrypt: Base64(IV + AES-CBC).</summary>
    private static string SifrujKaoStariKod(string tekst, byte[] kljuc)
    {
        using var aes = Aes.Create();
        aes.Key = kljuc;
        aes.GenerateIV();
        var sifrat = aes.EncryptCbc(Encoding.UTF8.GetBytes(tekst), aes.IV, PaddingMode.PKCS7);
        return Convert.ToBase64String([.. aes.IV, .. sifrat]);
    }

    // ── Rotacija ključa ────────────────────────────────────────────────────

    [Fact]
    public void Rotacija_NovKljucSifruje_StariSeIDaljeCita()
    {
        // Danas rotacija ENCRYPTION_KEY-a trajno uništava sve poruke. Sa
        // verzijom ključa stari ostaje za čitanje, a nove poruke idu novim.
        var pre   = CreateSut();
        var stara = pre.Encrypt("pre rotacije", Razgovor);

        var posle = CreateSut(null,
            ("Encryption:Keys:k1", DrugiKljuc),
            ("Encryption:CurrentKeyId", "k1"));
        var nova = posle.Encrypt("posle rotacije", Razgovor);

        nova.Should().StartWith("v2.k1.");
        posle.Decrypt(stara, Razgovor).Should().Be("pre rotacije");
        posle.Decrypt(nova, Razgovor).Should().Be("posle rotacije");
    }

    [Fact]
    public void Rotacija_DrugaPoRedu_PrethodniKljucIDaljeRadi()
    {
        var sa1 = CreateSut(null, ("Encryption:Keys:k1", DrugiKljuc), ("Encryption:CurrentKeyId", "k1"));
        var porukaK1 = sa1.Encrypt("šifrovano sa k1", Razgovor);

        var sa2 = CreateSut(null,
            ("Encryption:Keys:k1", DrugiKljuc),
            ("Encryption:Keys:k2", TreciKljuc),
            ("Encryption:CurrentKeyId", "k2"));

        sa2.Decrypt(porukaK1, Razgovor).Should().Be("šifrovano sa k1");
    }

    // ── Konfiguracija ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("kratak-kljuc", "Base64")]     // nije validan Base64
    [InlineData("YWJjZA==",     "32 bajta")]   // validan Base64, ali 4 bajta
    public void Konstruktor_KadaJeKljucNeispravan_PucaSaJasnomPorukom(
        string keyBase64, string expectedInMessage)
    {
        var act = () => CreateSut(keyBase64);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"*{expectedInMessage}*");
    }

    [Fact]
    public void Konstruktor_TekuciKljucNePostoji_Puca()
    {
        // Greška u .env-u mora da obori start, a ne prvu poruku nekog korisnika.
        var act = () => CreateSut(null, ("Encryption:CurrentKeyId", "k7"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*k7*");
    }

    [Fact]
    public void Konstruktor_K0SeNeMozeZadatiRucno()
    {
        // k0 je izveden iz MessageKey; ručni k0 bi tiho promenio ključ kojim su
        // šifrovane postojeće poruke.
        var act = () => CreateSut(null, ("Encryption:Keys:k0", DrugiKljuc));

        act.Should().Throw<InvalidOperationException>();
    }
}
