using Microsoft.EntityFrameworkCore;
using UsluzionicaServer.Persistence;

namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Background servis koji svake noći u ponoć briše poruke starije od N dana.
/// N se konfiguriše u appsettings.json kao "MessageRetentionDays" (default: 14).
///
/// Briše se samo sadržaj poruka (Message entiteti) — Conversation zapisi
/// ostaju zauvek, tako da korisnici mogu videti sa kim su pričali.
///
/// Koristi IServiceScopeFactory jer je BackgroundService singleton,
/// a AppDbContext je scoped — ne smeju se mešati životni vekovi.
/// </summary>
public sealed class MessageCleanupService(
    IServiceScopeFactory scopeFactory,
    IConfiguration       config,
    ILogger<MessageCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MessageCleanupService pokrenut.");

        // Odmah jednom počisti (ako je server bio dugo ugašen)
        await CleanupAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Čekaj do sledećeg ponoća UTC
            var now     = DateTime.UtcNow;
            var nextRun = now.Date.AddDays(1); // sutra u 00:00:00 UTC
            var delay   = nextRun - now;

            logger.LogInformation(
                "Sledeće čišćenje poruka zakazano za: {NextRun} UTC (za {Hours:F1}h)",
                nextRun, delay.TotalHours);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break; // aplikacija se gasi
            }

            await CleanupAsync(stoppingToken);
        }

        logger.LogInformation("MessageCleanupService zaustavljen.");
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
        var retentionDays = config.GetValue<int>("MessageRetentionDays", 14);
        var cutoff        = DateTime.UtcNow.AddDays(-retentionDays);

        try
        {
            // Kreiramo novi scope za svaki poziv (jer je DbContext scoped)
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // ExecuteDeleteAsync = direktan SQL DELETE bez učitavanja entiteta u memoriju
            var deleted = await db.Messages
                .Where(m => m.SentAt < cutoff)
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
                logger.LogInformation(
                    "Obrisano {Count} poruka starijih od {Days} dana (pre {Cutoff:yyyy-MM-dd})",
                    deleted, retentionDays, cutoff);
            else
                logger.LogDebug("Nema poruka za brisanje (starijih od {Cutoff:yyyy-MM-dd})", cutoff);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ne ubijamo background service zbog greške u čišćenju —
            // probaće ponovo sledeće noći.
            logger.LogError(ex, "Greška pri čišćenju starih poruka.");
        }
    }
}
