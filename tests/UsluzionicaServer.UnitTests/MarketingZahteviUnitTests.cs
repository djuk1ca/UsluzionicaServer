using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using UsluzionicaServer.Infrastructure.Demo;
using UsluzionicaServer.Infrastructure.ExternalAuth;

namespace UsluzionicaServer.UnitTests;

/// <summary>
/// Dva prekidača koja štite prvi utisak i produkciju (Marketing 14 P0-5, P0-7).
/// </summary>
public class MarketingZahteviUnitTests
{
    private static IConfiguration Konfig(params (string Kljuc, string Vrednost)[] vrednosti) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(vrednosti.ToDictionary(v => v.Kljuc, v => (string?)v.Vrednost))
            .Build();

    // ── P0-7 · Facebook dugme ──────────────────────────────────────────────

    [Fact]
    public void Facebook_SaKljucevima_AliBezEnabled_Iskljucen()
    {
        // Ključevi postoje od prvog dana, ali dok Meta aplikacija nije Live,
        // prijava radi samo za naloge sa ulogom. Dugme mora da ostane skriveno.
        var fb = new FacebookAuthProvider(new HttpClient(), Konfig(
            ("Facebook:AppId", "123"), ("Facebook:AppSecret", "tajna")));

        fb.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void Facebook_EnabledIKljucevi_Ukljucen()
    {
        var fb = new FacebookAuthProvider(new HttpClient(), Konfig(
            ("Facebook:Enabled", "true"), ("Facebook:AppId", "123"), ("Facebook:AppSecret", "tajna")));

        fb.IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void Facebook_EnabledBezTajne_Iskljucen()
    {
        var fb = new FacebookAuthProvider(new HttpClient(), Konfig(
            ("Facebook:Enabled", "true"), ("Facebook:AppId", "123")));

        fb.IsConfigured.Should().BeFalse();
    }

    // ── P0-5 · Demo nikad u produkciji ─────────────────────────────────────

    private sealed class Okruzenje(string ime) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = ime;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void Demo_UProdukciji_ObaraStart()
    {
        var akcija = () => DemoSeed.ProveriOkruzenje(
            Konfig(("DemoSeed:Enabled", "true")), new Okruzenje(Environments.Production));

        akcija.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("Production", "false")]    // isključen — produkcija normalno radi
    [InlineData("Development", "true")]    // uključen lokalno — upravo za to postoji
    public void Demo_DozvoljeneKombinacije(string okruzenje, string ukljucen)
    {
        var akcija = () => DemoSeed.ProveriOkruzenje(
            Konfig(("DemoSeed:Enabled", ukljucen)), new Okruzenje(okruzenje));

        akcija.Should().NotThrow();
    }
}
