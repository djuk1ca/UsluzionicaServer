using UsluzionicaServer.Infrastructure.Redis;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Pokreće <see cref="PodsetnikZaIzvrsenje"/> na svakih 15 minuta.
///
/// 15, a ne sat kao BoostExpiryService: aplikacija uslugodavcu prikazuje
/// odbrojavanje do „Izvršeno", pa obaveštenje koje stigne sat posle nule
/// izgleda kao greška. Upit je jeftin (indeks po statusu, mali skup).
/// </summary>
public sealed class PodsetnikZaIzvrsenjeService(
    IServiceScopeFactory                 scopeFactory,
    DistributedLock                      locks,
    ILogger<PodsetnikZaIzvrsenjeService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

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
            // Jedna instanca šalje — sa dve bi uslugodavac dobio isti podsetnik
            // dvaput, jer obe vide da još nije poslat.
            await using var lease = await locks.TryAcquireAsync("podsetnik-izvrsenje", TimeSpan.FromMinutes(5));
            if (lease is null) return;

            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<PodsetnikZaIzvrsenje>().PosaljiAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ne obara pozadinski servis — sledeći pokušaj za 15 minuta.
            logger.LogError(ex, "PodsetnikZaIzvrsenje nije uspeo.");
        }
    }
}
