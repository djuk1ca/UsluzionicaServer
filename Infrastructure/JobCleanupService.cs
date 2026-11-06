using UsluzionicaServer.Infrastructure.Redis;
using UsluzionicaServer.Services.Jobs;

namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Pokreće <see cref="JobOdrzavanje"/> na svakih 15 minuta (istek, podsetnici,
/// trajno brisanje poslova).
///
/// 15 minuta, kao podsetnik za usluge: aplikacija prikazuje odbrojavanje do
/// „Posao je izvršen" i „Ističe sutra" — obaveštenje sat posle nule izgleda
/// kao greška.
/// </summary>
public sealed class JobCleanupService(
    IServiceScopeFactory        scopeFactory,
    DistributedLock             locks,
    ILogger<JobCleanupService>  logger) : BackgroundService
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
            // Jedna instanca radi — sa dve bi autor dobio isti podsetnik dvaput.
            await using var lease = await locks.TryAcquireAsync("poslovi-odrzavanje", TimeSpan.FromMinutes(5));
            if (lease is null) return;

            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<JobOdrzavanje>().ProdjiAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Održavanje poslova nije uspelo.");
        }
    }
}
