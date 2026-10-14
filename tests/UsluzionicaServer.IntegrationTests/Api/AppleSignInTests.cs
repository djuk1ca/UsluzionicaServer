using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Domain.Entities;
using UsluzionicaServer.Infrastructure.Apple;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Api;

/// <summary>
/// Sign in with Apple kroz server: prva prijava → „Dovrši nalog" → nalog;
/// ponovna prijava → tokeni; brisanje naloga poništava prijavu i kod Apple-a.
/// </summary>
public class AppleSignInTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private static async Task<JsonElement> Podaci(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

    private Task<HttpResponseMessage> Prijava(HttpClient c, string idToken, string? kod = "kod-1", string? ime = null) =>
        c.PostAsJsonAsync("/api/auth/apple", new { identityToken = idToken, authorizationCode = kod, fullName = ime });

    [Fact]
    public async Task PrvaPrijava_DovrsiNalog_PonovnaDajeTokene_BrisanjePonistavaKodApplea()
    {
        var c = Factory.CreateClient();
        Factory.Apple.Nauci("tok-ana", new AppleIdentitet("apple-sub-ana", "ana@privaterelay.appleid.com", true));

        // 1. Prva prijava — nalog još ne postoji.
        var prva = await Prijava(c, "tok-ana", kod: "kod-ana", ime: "Ana Apple");
        prva.StatusCode.Should().Be(HttpStatusCode.OK);
        var signup = await Podaci(prva);
        signup.GetProperty("status").GetString().Should().Be("signup");
        signup.GetProperty("fullName").GetString().Should().Be("Ana Apple");

        // 2. „Dovrši nalog".
        var reg = await c.PostAsJsonAsync("/api/auth/external/register", new
        {
            signupToken    = signup.GetProperty("signupToken").GetString(),
            fullName       = "Ana Apple",
            city           = TestData.ValidCity,
            acceptedPolicy = true
        });
        reg.StatusCode.Should().Be(HttpStatusCode.OK);

        var user = await Query(db => db.Users.SingleAsync(u => u.Email == "ana@privaterelay.appleid.com"));
        user.EmailConfirmed.Should().BeTrue();

        // Refresh token je sačuvan — i šifrovan, ne u čistom obliku.
        var sacuvan = await Query(db => db.Set<IdentityUserToken<string>>()
            .Where(t => t.UserId == user.Id && t.LoginProvider == "apple").Select(t => t.Value).SingleAsync());
        sacuvan.Should().NotBeNullOrEmpty().And.NotContain("refresh-kod-ana");

        // 3. Ponovna prijava istim Apple nalogom → odmah tokeni.
        var druga = await Podaci(await Prijava(c, "tok-ana", kod: "kod-ana-2"));
        druga.GetProperty("status").GetString().Should().Be("ok");
        druga.GetProperty("auth").GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();

        // 4. Brisanje naloga → Apple-ov revoke sa POSLEDNJIM tokenom.
        var (ok, _) = await WithService<UserService, (bool, string?)>(
            s => s.DeleteAccountAsync(user.Id, null, UserService.PotvrdnaRec));
        ok.Should().BeTrue();
        Factory.Apple.Opozvani.Should().Contain("refresh-kod-ana-2");
    }

    [Fact]
    public async Task NevazeciToken_Odbijen()
    {
        var r = await Prijava(Factory.CreateClient(), "nepoznat-token");
        r.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ProvajderiUkljucujuApple_KadJePodesen()
    {
        var r = await Factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/auth/providers");
        r.GetProperty("data").EnumerateArray().Select(e => e.GetString()).Should().Contain("apple");
    }
}
