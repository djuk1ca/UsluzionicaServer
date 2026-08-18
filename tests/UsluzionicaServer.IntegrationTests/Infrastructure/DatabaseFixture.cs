using Microsoft.Data.SqlClient;
using Respawn;
using Testcontainers.MsSql;

namespace UsluzionicaServer.IntegrationTests.Infrastructure;

/// <summary>
/// Podiže JEDAN SQL Server kontejner i JEDAN host za ceo test assembly.
///
/// Zašto kontejner a ne EF InMemory: InMemory nije baza nego rečnik u memoriji.
/// Ne poštuje unique indekse (npr. jedan ProviderProfile po korisniku), ne
/// podržava ExecuteDeleteAsync/ExecuteUpdateAsync koje kod koristi, i ne
/// izvršava SQL. Test bi prolazio dok bi produkcija pucala.
///
/// Zašto ICollectionFixture a ne po klasi: startovanje kontejnera + migracije
/// + seed od 188 kategorija traje 20-40 sekundi. Po klasi bi to bilo
/// neupotrebljivo sporo.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private MsSqlContainer _container = null!;
    private Respawner      _respawner = null!;

    public UsluzionicaWebFactory Factory { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Kontejner se gradi OVDE, ne u inicijalizatoru polja.
        // Razlog: inicijalizator polja se izvršava u konstruktoru, a xUnit
        // konstruiše fixture po testu koji ga traži — pa bi greška "Docker nije
        // pokrenut" izbila 12 puta sa 12 identičnih stack trace-ova, umesto
        // jednom sa jasnom porukom.
        try
        {
            _container = new MsSqlBuilder()
                .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
                .Build();

            await _container.StartAsync();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      || ex.Message.Contains("Docker", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                """

                ══════════════════════════════════════════════════════════════
                  INTEGRACIONI TESTOVI ZAHTEVAJU POKRENUT DOCKER
                ══════════════════════════════════════════════════════════════

                  Testcontainers podiže pravi SQL Server u kontejneru. Bez
                  pokrenutog Docker-a to nije moguće.

                  Šta uraditi:
                    1. Pokreni Docker Desktop i sačekaj da ikona bude zelena
                    2. Provera:  docker ps
                    3. Ponovo:   dotnet test

                  Ako ti Docker sada ne treba, pokreni samo unit testove:
                    dotnet test tests/UsluzionicaServer.UnitTests

                ══════════════════════════════════════════════════════════════
                """, ex);
        }

        ConnectionString = _container.GetConnectionString();

        // Env varijable se postavljaju PRE pravljenja hosta jer Program.cs
        // poziva AddEnvironmentVariables() na kraju konfiguracionog lanca —
        // one nadjačavaju sve ostalo. Ovo je jedini deterministički način da
        // se ovom Program.cs-u nametne connection string.
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", ConnectionString);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("Jwt__Secret",            TestSecrets.JwtSecret);
        Environment.SetEnvironmentVariable("Encryption__MessageKey", TestSecrets.AesKeyBase64);
        Environment.SetEnvironmentVariable("AdminSeed__Email",       TestSecrets.AdminEmail);
        Environment.SetEnvironmentVariable("AdminSeed__Password",    TestSecrets.AdminPassword);

        Factory = new UsluzionicaWebFactory(ConnectionString);

        // Prvi zahtev pokreće host, koji primenjuje migracije i seed.
        // Bez ovoga bi Respawner ispod pokušao da čita šemu koja još ne postoji.
        using (var client = Factory.CreateClient())
            await client.GetAsync("/health");

        _respawner = await Respawner.CreateAsync(ConnectionString, new RespawnerOptions
        {
            DbAdapter = DbAdapter.SqlServer,

            // Referentni podaci koji dolaze JEDNOM — iz migracija (Categories,
            // 188 redova preko HasData) i iz startup seed-a (role). Host se ne
            // pokreće ponovo između testova, pa bi njihovo brisanje ostavilo
            // sve naredne testove bez kategorija i bez rola.
            TablesToIgnore =
            [
                "__EFMigrationsHistory",
                "Categories",
                "AspNetRoles",
            ]
        });
    }

    /// <summary>
    /// Vraća bazu u čisto stanje. Poziva se pre svakog testa.
    ///
    /// Alternativa koju NE koristimo: transakcija po testu sa rollback-om.
    /// Ne radi ovde jer servisi zovu SaveChangesAsync više puta po operaciji
    /// (ProviderService.ActivateAsync tri puta), a deo ide kroz UserManager
    /// koji ima sopstveni životni ciklus konekcije.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await _respawner.ResetAsync(conn);
    }

    public async Task DisposeAsync()
    {
        // Null provere jer se DisposeAsync poziva i kad InitializeAsync padne
        // (npr. Docker nije pokrenut) — tada polja nisu popunjena i bez ovoga
        // bi NullReferenceException zamaskirao pravu grešku.
        if (Factory is not null)    await Factory.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}

/// <summary>
/// xUnit po podrazumevanom podešavanju izvršava različite test KLASE paralelno.
/// Sve klase u ovoj kolekciji dele isti kontejner i istu bazu, pa paralelno
/// izvršavanje uz Respawn reset znači da bi jedan test brisao podatke drugom.
/// Ova kolekcija ih serijalizuje.
/// </summary>
[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "Integration";
}
