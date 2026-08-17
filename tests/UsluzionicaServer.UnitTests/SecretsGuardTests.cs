using Microsoft.Extensions.Hosting;
using NSubstitute;
using UsluzionicaServer.Infrastructure;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Štiti pravilo: server ne sme da se pokrene sa nedostajućom ili
/// kompromitovanom tajnom. Bolje pasti na startu sa jasnom porukom nego raditi
/// u produkciji sa slabim ključem.
/// </summary>
public class SecretsGuardTests
{
    // IHostEnvironment JESTE interfejs, pa ga NSubstitute može zameniti.
    // (Servisi u projektu su `sealed` bez interfejsa i zato se ne mock-uju —
    //  ovde je izuzetak jer je u pitanju framework interfejs.)
    private static IHostEnvironment Env(string environmentName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName = environmentName;
        return env;
    }

    [Fact]
    public void Validate_KadaJeSveIspravno_NePuca()
    {
        var config = TestConfig.From(TestConfig.ValidBase());

        var act = () => SecretsGuard.Validate(config, Env(Environments.Development));

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("ConnectionStrings:DefaultConnection")]
    [InlineData("Jwt:Secret")]
    [InlineData("Encryption:MessageKey")]
    [InlineData("AdminSeed:Password")]
    public void Validate_KadaNedostajeObavezanKljuc_PucaIImenujeGa(string missingKey)
    {
        var values = TestConfig.ValidBase();
        values[missingKey] = "";

        var act = () => SecretsGuard.Validate(TestConfig.From(values), Env(Environments.Development));

        // Poruka mora reći KOJI ključ nedostaje — inače je greška beskorisna.
        act.Should().Throw<InvalidOperationException>()
           .WithMessage($"*{missingKey}*");
    }

    [Fact]
    public void Validate_KadaJeJwtSecretPrekratak_Puca()
    {
        var values = TestConfig.ValidBase();
        values["Jwt:Secret"] = "prekratak";   // < 32 znaka, HMAC-SHA256 traži više

        var act = () => SecretsGuard.Validate(TestConfig.From(values), Env(Environments.Development));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*prekratak*");
    }

    [Fact]
    public void Validate_UProdukciji_ZahtevaIEmailPodesavanja()
    {
        // U produkciji bez SMTP-a niko ne može da potvrdi nalog ni resetuje
        // lozinku — zato su Email:Host i Email:Password obavezni samo tamo.
        var config = TestConfig.From(TestConfig.ValidBase());   // bez Email sekcije

        var act = () => SecretsGuard.Validate(config, Env(Environments.Production));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Email:Host*");
    }

    [Fact]
    public void Validate_KadaJeKljucKompromitovan_UProdukcijiPuca()
    {
        var values = TestConfig.ValidBase();
        // U produkciji su i Email podešavanja obavezna — popunjavamo ih da bi
        // test stigao do provere kompromitovanih ključeva, a ne pao ranije.
        values["Email:Host"]     = "smtp.example.com";
        values["Email:Password"] = "smtp-lozinka";

        // Ključ koji je stvarno bio commit-ovan u git.
        values["Encryption:MessageKey"] = "dGhpcyBpcyBhIGRldiBrZXkgb2YgMzIgYnl0ZXMhISE=";

        var act = () => SecretsGuard.Validate(TestConfig.From(values), Env(Environments.Production));

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*commit-ovane u git*");
    }

    [Fact]
    public void Validate_KadaJeKljucKompromitovan_URazvojuSamoUpozorava()
    {
        // Namerna razlika: stari dev ključ je zadržan da lokalne poruke ostanu
        // čitljive. U razvoju je to upozorenje, u produkciji tvrda greška.
        var values = TestConfig.ValidBase();
        values["Encryption:MessageKey"] = "dGhpcyBpcyBhIGRldiBrZXkgb2YgMzIgYnl0ZXMhISE=";

        var act = () => SecretsGuard.Validate(TestConfig.From(values), Env(Environments.Development));

        act.Should().NotThrow();
    }
}
