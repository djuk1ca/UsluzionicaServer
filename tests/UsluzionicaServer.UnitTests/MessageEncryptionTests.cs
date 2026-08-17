using UsluzionicaServer.Infrastructure;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Štiti pravilo: sadržaj privatnih poruka se u bazi čuva šifrovan, a
/// dešifrovanje mora vratiti tačno original — uključujući srpske dijakritike,
/// koje UTF-8 kodira na više bajtova i lako se pokvare pogrešnim rukovanjem.
/// </summary>
public class MessageEncryptionTests
{
    private static MessageEncryption CreateSut(string? keyBase64 = null)
    {
        var values = TestConfig.ValidBase();
        if (keyBase64 is not null) values["Encryption:MessageKey"] = keyBase64;
        return new MessageEncryption(TestConfig.From(values));
    }

    [Theory]
    [InlineData("Zdravo, kako si?")]
    [InlineData("Čeka me šišanje u četvrtak — Đorđe, Žarko, Ćira")]
    [InlineData("Емоџи и ћирилица 🔧🏠")]
    [InlineData("")]
    public void EncryptDecrypt_VracaTacanOriginal(string plaintext)
    {
        var sut = CreateSut();

        sut.Decrypt(sut.Encrypt(plaintext)).Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_ZaIstiTekstDajeRazliciteRezultate()
    {
        // Svaka poruka dobija nov random IV. Da nije tako, napadač sa pristupom
        // bazi bi video da su dve poruke identične i bez dešifrovanja.
        var sut = CreateSut();

        var a = sut.Encrypt("ista poruka");
        var b = sut.Encrypt("ista poruka");

        a.Should().NotBe(b);
        sut.Decrypt(a).Should().Be(sut.Decrypt(b));
    }

    [Fact]
    public void SafeDecrypt_KadaJeSadrzajNeispravan_VracaPlaceholderUmestoDaPuca()
    {
        // Poruka šifrovana starim ključem ne sme da sruši ceo ekran konverzacije.
        var sut = CreateSut();

        sut.SafeDecrypt("ovo-nije-validan-base64!!!").Should().Be("[poruka nije čitljiva]");
    }

    [Fact]
    public void SafeDecrypt_KadaJePorukaSifrovanaDrugimKljucem_VracaPlaceholder()
    {
        var encrypted = CreateSut().Encrypt("tajna poruka");

        // Drugi validan 32-bajtni ključ (32 puta slovo 'b').
        var other = CreateSut("YmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmI=");

        other.SafeDecrypt(encrypted).Should().Be("[poruka nije čitljiva]");
    }

    [Theory]
    [InlineData("kratak-kljuc", "Base64")]                       // nije validan Base64
    [InlineData("YWJjZA==",     "32 bajta")]                     // validan Base64, ali 4 bajta
    public void Konstruktor_KadaJeKljucNeispravan_PucaSaJasnomPorukom(
        string keyBase64, string expectedInMessage)
    {
        var act = () => CreateSut(keyBase64);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"*{expectedInMessage}*");
    }
}
