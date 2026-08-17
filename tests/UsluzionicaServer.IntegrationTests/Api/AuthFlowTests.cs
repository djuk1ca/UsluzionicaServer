using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using UsluzionicaServer.IntegrationTests.Infrastructure;

namespace UsluzionicaServer.IntegrationTests.Api;

/// <summary>
/// ŠABLON ZA API TESTOVE.
///
/// Za razliku od Services/ testova koji zovu servis direktno, ovi idu kroz
/// pravi HTTP: rutiranje, model binding, autorizacija, oblik odgovora i
/// serijalizacija su deo onoga što se testira.
///
/// Pokriva kompletan tok: register → verify → login → refresh → zaštićeni
/// endpoint. To je put kojim prolazi svaki novi korisnik; ako on pukne,
/// aplikacija je neupotrebljiva bez obzira što je sve ostalo ispravno.
/// </summary>
public class AuthFlowTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task KompletanTok_RegistracijaVerifikacijaPrijavaObnovaZasticeniEndpoint()
    {
        var client = Factory.CreateClient();
        const string email = "novi.korisnik@test.rs";
        const string lozinka = "MojaLoz123!";

        // ── 1. Registracija ────────────────────────────────────────────────
        var register = await client.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = "Novi Korisnik",
            email,
            password = lozinka
        });

        register.StatusCode.Should().Be(HttpStatusCode.OK);

        // ── 2. Verifikacija ────────────────────────────────────────────────
        // Verifikacioni URL postoji SAMO u poslatom emailu — zato je
        // IEmailService izvučen u interfejs i zamenjen fake-om.
        var verifyUrl = Email.LastVerificationUrlFor(email);
        verifyUrl.Should().NotBeNull("registracija mora poslati verifikacioni email");

        // Server vraća HTML stranicu, ne JSON — korisnik je otvara u browseru.
        var verify = await client.GetAsync(StripHost(verifyUrl!));
        verify.StatusCode.Should().Be(HttpStatusCode.OK);
        (await verify.Content.ReadAsStringAsync()).Should().Contain("potvrđen");

        // ── 3. Prijava ─────────────────────────────────────────────────────
        var login = await client.PostAsJsonAsync("/api/auth/login", new { email, password = lozinka });
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>(Json);
        loginBody.GetProperty("success").GetBoolean().Should().BeTrue();

        var data         = loginBody.GetProperty("data");
        var accessToken  = data.GetProperty("accessToken").GetString()!;
        var refreshToken = data.GetProperty("refreshToken").GetString()!;

        accessToken.Should().NotBeNullOrWhiteSpace();

        // ── 4. Zaštićeni endpoint sa tokenom ───────────────────────────────
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var me = await client.GetAsync("/api/users/me");
        me.StatusCode.Should().Be(HttpStatusCode.OK);

        var meBody = await me.Content.ReadFromJsonAsync<JsonElement>(Json);
        meBody.GetProperty("data").GetProperty("email").GetString().Should().Be(email);

        // ── 5. Obnova tokena ───────────────────────────────────────────────
        client.DefaultRequestHeaders.Authorization = null;

        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);

        var refreshBody = await refresh.Content.ReadFromJsonAsync<JsonElement>(Json);
        var noviToken   = refreshBody.GetProperty("data").GetProperty("accessToken").GetString()!;

        noviToken.Should().NotBeNullOrWhiteSpace();

        // Nov token mora raditi na zaštićenom endpointu.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", noviToken);
        (await client.GetAsync("/api/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ZasticeniEndpoint_BezTokena_Vraca401()
    {
        // Negativan par prethodnog testa: dokazuje da autorizacija zaista
        // štiti endpoint, a ne da je 200 slučajno prošao.
        var client = Factory.CreateClient();

        var response = await client.GetAsync("/api/users/me");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Prijava_PreNegoStoJeEmailPotvrdjen_Odbija()
    {
        var client = Factory.CreateClient();
        const string email = "nepotvrdjen@test.rs";

        await client.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = "Nepotvrđeni", email, password = "MojaLoz123!"
        });

        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email, password = "MojaLoz123!"
        });

        login.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await login.Content.ReadAsStringAsync()).Should().Contain("nije potvrđena");
    }

    [Fact]
    public async Task ObrisanNalog_ViseNeMozeKoristitiPostojeciToken()
    {
        // Štiti popravku iz Faze 0: brisanje naloga mora odmah poništiti i
        // access token koji je već izdat. Bez toga bi obrisan nalog nastavio
        // da radi do isteka tokena (do 60 minuta).
        var client = Factory.CreateClient();
        const string email = "za.brisanje@test.rs";

        var user = await Data.CreateConfirmedUserAsync(email);

        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email, password = TestData.DefaultPassword
        });
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("data").GetProperty("accessToken").GetString()!;

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Nalog radi pre brisanja.
        (await client.GetAsync("/api/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Obriši nalog istim tokenom.
        var delete = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/users/me")
        {
            Content = JsonContent.Create(new { password = TestData.DefaultPassword })
        });
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        // Isti token više ne sme da prolazi.
        (await client.GetAsync("/api/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        user.Id.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Verifikacioni URL sadrži pun domen (App:BaseUrl), a test klijent gađa
    /// host u memoriji — pa uzimamo samo putanju i query.
    /// </summary>
    private static string StripHost(string absoluteUrl)
    {
        var uri = new Uri(absoluteUrl);
        return uri.PathAndQuery;
    }
}
