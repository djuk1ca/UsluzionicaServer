using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UsluzionicaServer.Infrastructure.ExternalAuth;
using UsluzionicaServer.IntegrationTests.Infrastructure;

namespace UsluzionicaServer.IntegrationTests.Api;

/// <summary>
/// Kuda se prijava preko Google-a/Facebook-a vraća: aplikacija
/// (<c>usluzionica://auth</c>) ili sajt (<c>client=web</c>).
///
/// Pravi provajder se zamenjuje lažnim „test" provajderom — ovde se testira
/// izbor povratne adrese i oblik fragmenta, ne razgovor sa Google-om. Najvažniji
/// slučaj je nepoznat klijent: on nikad ne sme da odredi adresu (open redirect).
/// </summary>
public class ExternalAuthPovratakTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private const string Sajt       = "https://sajt.test";
    private const string WebPovratak = Sajt + "/app/oauth/povratak";
    private const string AppPovratak = "usluzionica://auth";

    private sealed class LazniProvajder : IExternalAuthProvider
    {
        public string Name => "test";
        public bool   IsConfigured => true;

        public string BuildAuthorizeUrl(string state, string redirectUri) =>
            $"https://provajder.test/authorize?state={Uri.EscapeDataString(state)}";

        public Task<ExternalIdentity> ExchangeCodeAsync(string code, string redirectUri, CancellationToken ct) =>
            Task.FromResult(new ExternalIdentity("Test", "test-sub-1", "nova@test.rs", true, "Ana Anić"));
    }

    private WebApplicationFactory<Program> Host(string? webCallbackUri = null) =>
        Factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("App:SiteUrl", Sajt);
            if (webCallbackUri is not null) b.UseSetting("ExternalAuth:WebCallbackUri", webCallbackUri);
            b.ConfigureTestServices(s => s.AddSingleton<IExternalAuthProvider>(new LazniProvajder()));
        });

    private static HttpClient Klijent(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static (string Verifier, string Challenge) NovPar()
    {
        var verifier  = ExternalAuthTickets.Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = ExternalAuthTickets.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static async Task<string> PreusmerenjeAsync(HttpClient client, string adresa)
    {
        var odgovor = await client.GetAsync(adresa);
        odgovor.StatusCode.Should().Be(HttpStatusCode.Redirect, adresa);
        return odgovor.Headers.Location!.OriginalString;
    }

    /// <summary>Start → (provajder) → callback; vraća adresu na koju callback šalje.</summary>
    private static async Task<string> CeoTokAsync(HttpClient client, string challenge, string? clientParam, string callbackUpit = "code=abc")
    {
        var start = $"/api/auth/test/start?challenge={challenge}"
                    + (clientParam is null ? "" : $"&client={Uri.EscapeDataString(clientParam)}");
        var kaProvajderu = await PreusmerenjeAsync(client, start);
        kaProvajderu.Should().StartWith("https://provajder.test/authorize");

        var state = HttpUtility.ParseQueryString(new Uri(kaProvajderu).Query)["state"]!;
        return await PreusmerenjeAsync(client,
            $"/api/auth/test/callback?{callbackUpit}&state={Uri.EscapeDataString(state)}");
    }

    private static string Kod(string povratak, string prefiks)
    {
        povratak.Should().StartWith(prefiks + "#code=");
        return Uri.UnescapeDataString(povratak[(prefiks.Length + "#code=".Length)..]);
    }

    // ── Sajt ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Web_CallbackVracaNaSajt_IstaRazmena()
    {
        await using var host = Host();
        var client = Klijent(host);
        var (verifier, challenge) = NovPar();

        var povratak = await CeoTokAsync(client, challenge, "web");
        var kod = Kod(povratak, WebPovratak);

        // Razmena je ista kao za aplikaciju — sajt je zove sa servera, istom tajnom.
        var razmena = await client.PostAsJsonAsync("/api/auth/external/exchange", new { code = kod, verifier });
        razmena.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await razmena.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("status").GetString().Should().Be("signup");
        data.GetProperty("email").GetString().Should().Be("nova@test.rs");
    }

    [Fact]
    public async Task Web_OtkazanoKodProvajdera_VracaNaSajt()
    {
        await using var host = Host();
        var (_, challenge) = NovPar();

        var povratak = await CeoTokAsync(Klijent(host), challenge, "web", callbackUpit: "error=access_denied");

        povratak.Should().Be(WebPovratak + "#error=otkazano");
    }

    [Fact]
    public async Task Web_GreskaNaStartu_VracaNaSajt()
    {
        await using var host = Host();

        var povratak = await PreusmerenjeAsync(Klijent(host), "/api/auth/test/start?challenge=los&client=web");

        povratak.Should().Be(WebPovratak + "#error=greska");
    }

    [Fact]
    public async Task Web_AdresaIzKonfiguracije()
    {
        await using var host = Host(webCallbackUri: "http://localhost:5100/app/oauth/povratak");
        var (_, challenge) = NovPar();

        var povratak = await CeoTokAsync(Klijent(host), challenge, "web");

        povratak.Should().StartWith("http://localhost:5100/app/oauth/povratak#code=");
    }

    // ── Aplikacija ─────────────────────────────────────────────────────────

    [Fact]
    public async Task BezKlijenta_CallbackVracaUAplikaciju()
    {
        await using var host = Host();
        var (_, challenge) = NovPar();

        var povratak = await CeoTokAsync(Klijent(host), challenge, clientParam: null);

        Kod(povratak, AppPovratak).Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("https://napadac.example")]
    [InlineData("//napadac.example")]
    [InlineData("webx")]
    [InlineData("app")]
    public async Task NepoznatKlijent_VracaUAplikaciju(string nepoznat)
    {
        await using var host = Host();
        var client = Klijent(host);
        var (_, challenge) = NovPar();

        var povratak = await CeoTokAsync(client, challenge, nepoznat);
        povratak.Should().StartWith(AppPovratak + "#code=");

        (await PreusmerenjeAsync(client, $"/api/auth/test/start?challenge=los&client={Uri.EscapeDataString(nepoznat)}"))
            .Should().Be(AppPovratak + "#error=greska");
    }

    [Fact]
    public async Task NeispravanState_VracaUAplikaciju()
    {
        // Bez važećeg state-a nema ni klijenta — sajt se ne može izabrati podmetanjem.
        await using var host = Host();

        var povratak = await PreusmerenjeAsync(Klijent(host), "/api/auth/test/callback?code=abc&state=izmisljen");

        povratak.Should().Be(AppPovratak + "#error=greska");
    }
}
