using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Izvoz podataka (ZZPL, GDPR čl. 15 i 20).
///
/// Dve stvari su ovde bitne i obe su lako pokvariti: da izvoz sadrži
/// korisnikove podatke, i da NE sadrži tuđe. Druga je opasnija — izvoz koji
/// nosi tuđe oglase ili poruke je curenje podataka, ne usluga.
/// </summary>
public class IzvozPodatakaTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    // Bez ovoga serijalizator eskejpuje „đ" i „ć" (kao \u-kod), pa bi
    // NotContain("Tuđ oglas") prolazio uvek, čak i kad oglas jeste u izvozu.
    private static readonly JsonSerializerOptions BezEskejpa =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private async Task<string?> IzveziAsync(string userId)
    {
        var podaci = await WithService<IzvozPodataka, object?>(svc => svc.NapraviAsync(userId));
        return podaci is null ? null : JsonSerializer.Serialize(podaci, BezEskejpa);
    }

    [Fact]
    public async Task Izvoz_SadrziNalogIOglaseKorisnika()
    {
        var (ja, _) = await Data.CreateProviderAsync("izvoz@test.rs", fullName: "Petar Izvozić");
        await Data.CreateActiveListingAsync(ja.Id, title: "Moj oglas za izvoz");

        var json = await IzveziAsync(ja.Id);

        json.Should().NotBeNull();
        json.Should().Contain("izvoz@test.rs");
        json.Should().Contain("Petar Izvozić");
        json.Should().Contain("Moj oglas za izvoz");
    }

    [Fact]
    public async Task Izvoz_NeSadrziTudjeOglase()
    {
        // NEGATIVAN par testu iznad: bez njega bi prošao i izvoz koji vraća
        // SVE oglase iz baze.
        var (ja, _)   = await Data.CreateProviderAsync("izvoz-ja@test.rs");
        var (drugi, _) = await Data.CreateProviderAsync("izvoz-drugi@test.rs");
        await Data.CreateActiveListingAsync(drugi.Id, title: "Tuđ oglas");

        var json = await IzveziAsync(ja.Id);

        json.Should().NotContain("Tuđ oglas");
        json.Should().NotContain("izvoz-drugi@test.rs");
    }

    [Fact]
    public async Task Izvoz_ObrisanNalog_Null()
    {
        // Posle brisanja ostaju samo anonimizovani ostaci — nema šta da se izveze,
        // a stari JWT ne sme da otvori ni to.
        var ja = await Data.CreateConfirmedUserAsync("izvoz-obrisan@test.rs");
        await Query(async db =>
        {
            var u = await db.Users.SingleAsync(x => x.Id == ja.Id);
            u.IsActive = false;
            return await db.SaveChangesAsync();
        });

        (await IzveziAsync(ja.Id)).Should().BeNull();
    }
}
