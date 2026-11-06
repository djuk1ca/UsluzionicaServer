using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Api;

/// <summary>
/// HTTP ugovor za PUT /api/listings/{id}/images/order.
///
/// Pravila redosleda su pokrivena u Services/ListingImageOrderTests. Ovde se
/// proverava ono što servisni test ne vidi: ruta, model binding tela
/// <c>{ "imageIds": [...] }</c>, autorizacija i preslikavanje ishoda na
/// status kodove — 200 sa slikama, 400 za neispravnu listu, 404 za tuđ oglas.
/// Aplikacija se piše po tom ugovoru, pa promena oblika odgovora mora pasti ovde.
/// </summary>
public class ListingImageOrderApiTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static IFormFile Slika()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46,
                       0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
                       0xFF, 0xD9];

        var ms = new MemoryStream(jpeg);
        return new FormFile(ms, 0, ms.Length, "file", "test.jpg")
        {
            Headers     = new HeaderDictionary(),
            ContentType = "image/jpeg"
        };
    }

    /// <summary>
    /// Klijent sa access tokenom korisnika. Token izdaje isti TokenService koji
    /// koristi prijava, ali BEZ poziva /api/auth/login.
    ///
    /// Zašto ne prava prijava: Program.cs čita RateLimit:AuthPermitLimit pre
    /// nego što fabrika doda svoju konfiguraciju, pa je u testovima limit za
    /// prijavu stvarnih 5 u minuti, zajednički za ceo suite. Svaka prijava
    /// ovde bi pojela kvotu AuthFlowTests-u, koji tada pada sa 429. Sama
    /// prijava nije predmet ovog testa.
    /// </summary>
    private async Task<HttpClient> PrijavljenAsync(string email)
    {
        using var scope = Factory.Services.CreateScope();
        var users  = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var tokeni = scope.ServiceProvider.GetRequiredService<TokenService>();

        var user  = (await users.FindByEmailAsync(email))!;
        var token = tokeni.GenerateAccessToken(user, await users.GetRolesAsync(user));

        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Redosled_Vraca200_400_404()
    {
        var (majstor, _) = await Data.CreateProviderAsync("majstor@test.rs");
        await Data.CreateProviderAsync("stranac@test.rs");
        var oglasId = await Data.CreateActiveListingAsync(majstor.Id);

        var ids = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var (slika, greska) = await WithService<ListingService, (ListingImageDto?, string?)>(
                svc => svc.UploadImageAsync(oglasId, majstor.Id, Slika()));
            slika.Should().NotBeNull(greska);
            ids.Add(slika!.Id);
        }

        var putanja = $"/api/listings/{oglasId}/images/order";
        var vlasnik = await PrijavljenAsync("majstor@test.rs");

        // ── 200: tačno sve slike, novim redom ──────────────────────────────
        var novi = new[] { ids[2], ids[0], ids[1] };
        var ok = await vlasnik.PutAsJsonAsync(putanja, new { imageIds = novi });

        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var okBody = await ok.Content.ReadFromJsonAsync<JsonElement>(Json);
        okBody.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = okBody.GetProperty("data").EnumerateArray().ToList();
        data.Select(s => s.GetProperty("id").GetInt32()).Should().Equal(novi);
        data.Select(s => s.GetProperty("sortOrder").GetInt32()).Should().Equal(0, 1, 2);
        data.Should().OnlyContain(s => s.GetProperty("imageUrl").GetString()!.Length > 0);

        // ── 400: nepotpuna lista ───────────────────────────────────────────
        var los = await vlasnik.PutAsJsonAsync(putanja, new { imageIds = new[] { ids[0] } });

        los.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var losBody = await los.Content.ReadFromJsonAsync<JsonElement>(Json);
        losBody.GetProperty("success").GetBoolean().Should().BeFalse();
        losBody.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();

        // ── 404: tuđ oglas ─────────────────────────────────────────────────
        var stranac = await PrijavljenAsync("stranac@test.rs");
        var tudj = await stranac.PutAsJsonAsync(putanja, new { imageIds = novi });

        tudj.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var tudjBody = await tudj.Content.ReadFromJsonAsync<JsonElement>(Json);
        tudjBody.GetProperty("success").GetBoolean().Should().BeFalse();
        tudjBody.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();

        // ── 401: bez tokena ────────────────────────────────────────────────
        var anoniman = await Factory.CreateClient().PutAsJsonAsync(putanja, new { imageIds = novi });
        anoniman.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Neuspeli pozivi nisu dirali redosled iz prvog.
        var detalji = await vlasnik.GetFromJsonAsync<JsonElement>($"/api/listings/{oglasId}", Json);
        detalji.GetProperty("data").GetProperty("images").EnumerateArray()
            .Select(s => s.GetProperty("id").GetInt32())
            .Should().Equal(novi);
    }
}
