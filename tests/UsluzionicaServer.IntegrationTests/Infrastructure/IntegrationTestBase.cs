using Microsoft.Extensions.DependencyInjection;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.IntegrationTests.Infrastructure;

/// <summary>
/// Zajednička osnova za integracione testove: čist reset baze pre svakog testa
/// i pomoćni metodi za rad sa DI scope-ovima.
/// </summary>
[Collection(IntegrationCollection.Name)]
public abstract class IntegrationTestBase(DatabaseFixture fixture) : IAsyncLifetime
{
    protected DatabaseFixture       Fixture => fixture;
    protected UsluzionicaWebFactory Factory => fixture.Factory;
    protected FakeEmailService      Email   => fixture.Factory.Email;

    protected TestData Data { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        Email.Clear();
        Data = new TestData(Factory);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Rad sa servisima ───────────────────────────────────────────────────

    /// <summary>
    /// Izvršava akciju nad servisom razrešenim iz PRAVOG DI kontejnera.
    ///
    /// Zašto ovako a ne `new BookingService(...)`: servisi su `sealed`, sa
    /// primary konstruktorom i bez interfejsa, i zavise od `NotificationService`
    /// koji zavisi od `IHubContext`. Ručno sklapanje bi značilo rekonstruisanje
    /// pola DI grafa — i test bi testirao tu rekonstrukciju, a ne pravu vezu.
    /// </summary>
    protected async Task<TResult> WithService<TService, TResult>(
        Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        using var scope = Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TService>();
        return await action(service);
    }

    protected async Task WithService<TService>(Func<TService, Task> action)
        where TService : notnull
    {
        using var scope = Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<TService>();
        await action(service);
    }

    // ── Provera stanja ─────────────────────────────────────────────────────

    /// <summary>
    /// Čita iz baze kroz NOV DbContext.
    ///
    /// ZAMKA KOJU MORAŠ RAZUMETI — EF-ov identity map:
    /// DbContext kešira entitete koje je učitao. Ako proveravaš rezultat kroz
    /// isti DbContext koji je napravio izmenu, `FindAsync` može vratiti
    /// keširanu instancu iz memorije i test prolazi i kad upis u bazu nije
    /// uspeo. To je najčešći razlog zašto integracioni test lažno "prolazi".
    ///
    /// Nov scope = nov DbContext = prazan keš = pravo čitanje iz baze.
    /// </summary>
    protected async Task<TResult> Query<TResult>(Func<AppDbContext, Task<TResult>> query)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await query(db);
    }
}
