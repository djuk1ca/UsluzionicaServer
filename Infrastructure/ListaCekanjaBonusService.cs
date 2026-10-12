using UsluzionicaServer.Infrastructure.Redis;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Pokreće <see cref="ListaCekanjaBonus.ProdjiAsync"/> na svakih 30 minuta.
///
/// Glavni put bonusa je trenutak potvrde adrese (AuthController, AuthService,
/// ExternalLoginService). Ovaj prolaz hvata ostatak: nalog potvrđen PRE nego
/// što se adresa upisala na sajtu, ili pre ovog deploya. Prvi prolaz ide odmah
/// po startu, pa takvi nalozi bonus dobijaju već pri prvom deployu.
/// </summary>
public sealed class ListaCekanjaBonusService(
    IServiceScopeFactory              scopeFactory,
    DistributedLock                   locks,
    ILogger<ListaCekanjaBonusService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await JednomAsync(stoppingToken);

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task JednomAsync(CancellationToken ct)
    {
        try
        {
            // Jedna instanca prolazi listu. Dvostruku isplatu ionako sprečavaju
            // jedinstveni indeksi, ali dve instance bi radile isti posao i
            // punile log porukama „već isplaćen".
            await using var lease = await locks.TryAcquireAsync("lista-cekanja-bonus", TimeSpan.FromMinutes(10));
            if (lease is null) return;

            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ListaCekanjaBonus>().ProdjiAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ne obara pozadinski servis — sledeći pokušaj za 30 minuta.
            logger.LogError(ex, "Prolaz liste čekanja nije uspeo.");
        }
    }
}
