using UsluzionicaServer.Infrastructure.Redis;
using UsluzionicaServer.Services;

namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Objavljuje ocene kojima je prošao rok (14 dana od izvršenja), a druga
/// strana nije ocenila — vidi <see cref="ReviewService.ObjaviIstekleAsync"/>.
/// </summary>
public sealed class ReviewRevealService(
    IServiceScopeFactory          scopeFactory,
    DistributedLock               locks,
    ILogger<ReviewRevealService>  logger) : BackgroundService
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
            // Jedna instanca — sa dve bi ocenjeni dobio isto obaveštenje dvaput.
            await using var lease = await locks.TryAcquireAsync("ocene-objava", TimeSpan.FromMinutes(5));
            if (lease is null) return;

            await using var scope = scopeFactory.CreateAsyncScope();
            var objavljeno = await scope.ServiceProvider.GetRequiredService<ReviewService>().ObjaviIstekleAsync(ct);
            if (objavljeno > 0)
                logger.LogInformation("Objavljeno {Broj} ocena kojima je prošao rok.", objavljeno);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Objavljivanje ocena nije uspelo.");
        }
    }
}
