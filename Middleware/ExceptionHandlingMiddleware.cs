using System.Text.Json;

namespace UsluzionicaServer.Middleware;

/// <summary>
/// Globalni hvatač izuzetaka. Bez njega neuhvaćen izuzetak ide u default
/// framework ponašanje — u produkciji prazan 500 bez traga u logu koji bi se
/// mogao povezati sa prijavom korisnika.
///
/// Ovde svaki izuzetak dobija correlationId koji se i loguje i vraća klijentu,
/// pa korisnik može da ga prijavi a mi da ga nađemo u logovima. Odgovor prati
/// isti { success, message } oblik kao ostatak API-ja, da klijentski
/// ApiClient.TryReadError može da ga pročita bez posebnog slučaja.
/// </summary>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IHostEnvironment env)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var correlationId = context.TraceIdentifier;

            logger.LogError(ex,
                "Neuhvaćen izuzetak {CorrelationId} na {Method} {Path}",
                correlationId, context.Request.Method, context.Request.Path);

            // Ako je odgovor već krenuo, ne možemo ga prepisati — samo prekini.
            if (context.Response.HasStarted)
                throw;

            context.Response.Clear();
            context.Response.StatusCode  = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/json";

            // Detalje izuzetka vraćamo samo van produkcije.
            var message = env.IsProduction()
                ? $"Došlo je do greške na serveru. Šifra: {correlationId}"
                : $"{ex.GetType().Name}: {ex.Message}";

            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                success       = false,
                message,
                correlationId
            }));
        }
    }
}
