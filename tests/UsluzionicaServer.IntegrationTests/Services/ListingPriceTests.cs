using UsluzionicaServer.Domain.Enums;
using UsluzionicaServer.DTOs.Listings;
using UsluzionicaServer.IntegrationTests.Infrastructure;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.IntegrationTests.Services;

/// <summary>
/// Cena oglasa: „od" je obavezno za raspon, „do" je opciono.
/// „od 7.000 RSD" je potpuna cena; ako je „do" upisano, mora biti ≥ „od".
/// „Po dogovoru" (Negotiable) i dalje ne traži iznose.
/// </summary>
public class ListingPriceTests(DatabaseFixture fixture) : IntegrationTestBase(fixture)
{
    private static CreateListingDto Oglas(PriceMode mode, decimal? od = null, decimal? @do = null, decimal? fiksna = null) => new()
    {
        Title       = "Krečenje stana",
        Description = "Krečenje i gletovanje, sopstveni materijal.",
        Location    = TestData.ValidCity,
        CategoryId  = TestData.SeededCategoryId,
        PriceMode   = mode,
        FixedPrice  = fiksna,
        PriceFrom   = od,
        PriceTo     = @do
    };

    private async Task<(ListingDto? Result, string? Error)> Kreiraj(string email, CreateListingDto dto)
    {
        var (user, _) = await Data.CreateProviderAsync(email);
        return await WithService<ListingService, (ListingDto?, string?)>(svc => svc.CreateAsync(user.Id, dto));
    }

    [Fact]
    public async Task Raspon_SamoOd_SeCuvaBezDo()
    {
        var (oglas, error) = await Kreiraj("samo-od@test.rs", Oglas(PriceMode.Range, od: 7000m));

        error.Should().BeNull();
        oglas!.PriceFrom.Should().Be(7000m);
        oglas.PriceTo.Should().BeNull();
    }

    [Fact]
    public async Task Raspon_OdIDo_SeCuva()
    {
        var (oglas, error) = await Kreiraj("od-do@test.rs", Oglas(PriceMode.Range, od: 7000m, @do: 10000m));

        error.Should().BeNull();
        oglas!.PriceTo.Should().Be(10000m);
    }

    [Fact]
    public async Task Raspon_IstiOdIDo_JeDozvoljen()
    {
        var (_, error) = await Kreiraj("isto@test.rs", Oglas(PriceMode.Range, od: 7000m, @do: 7000m));

        error.Should().BeNull();
    }

    [Fact]
    public async Task Raspon_DoManjeOdOd_SeOdbija()
    {
        var (oglas, error) = await Kreiraj("obrnuto@test.rs", Oglas(PriceMode.Range, od: 7000m, @do: 5000m));

        oglas.Should().BeNull();
        error.Should().Contain("priceTo");
    }

    [Fact]
    public async Task Raspon_BezOd_SeOdbija()
    {
        var (oglas, error) = await Kreiraj("bez-od@test.rs", Oglas(PriceMode.Range, @do: 10000m));

        oglas.Should().BeNull();
        error.Should().Contain("priceFrom");
    }

    [Fact]
    public async Task PoDogovoru_NeTraziIznose()
    {
        var (oglas, error) = await Kreiraj("dogovor@test.rs", Oglas(PriceMode.Negotiable));

        error.Should().BeNull();
        oglas!.PriceMode.Should().Be(PriceMode.Negotiable);
    }

    [Fact]
    public async Task Izmena_UklanjaDo()
    {
        var (user, _) = await Data.CreateProviderAsync("izmena-do@test.rs");
        var (oglas, _) = await WithService<ListingService, (ListingDto?, string?)>(svc =>
            svc.CreateAsync(user.Id, Oglas(PriceMode.Range, od: 7000m, @do: 10000m)));

        var (ok, error) = await WithService<ListingService, (bool, string?)>(svc =>
            svc.UpdateAsync(oglas!.Id, user.Id, new UpdateListingDto
            {
                Title       = oglas.Title,
                Description = oglas.Description,
                Location    = oglas.Location,
                CategoryId  = TestData.SeededCategoryId,
                PriceMode   = PriceMode.Range,
                PriceFrom   = 7000m,
                PriceTo     = null
            }));

        error.Should().BeNull();
        ok.Should().BeTrue();
        var posle = await WithService<ListingService, ListingDto?>(svc => svc.GetByIdAsync(oglas!.Id));
        posle!.PriceTo.Should().BeNull();
    }

    [Fact]
    public async Task Pretraga_NalaziOglasBezDo()
    {
        await Kreiraj("pretraga-od@test.rs", Oglas(PriceMode.Range, od: 7000m));

        var result = await WithService<ListingService, PagedResult<ListingDto>>(svc =>
            svc.SearchAsync(new ListingQueryParams { Q = "krečenje", Page = 1, PageSize = 50 }));

        result.Items.Should().ContainSingle(l => l.PriceFrom == 7000m && l.PriceTo == null);
    }
}
