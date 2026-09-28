namespace UsluzionicaServer.Infrastructure.Push;

/// <summary>
/// Ne šalje ništa i nikad ne prijavljuje grešku.
///
/// Koristi se kad Firebase ključ nije podešen — dakle u razvoju, u testovima i
/// pri svakom pokretanju kod nekoga ko nema pristup produkcijskim tajnama.
///
/// Alternativa bi bila da <c>NotificationService</c> svuda proverava da li je
/// push uključen. To bi značilo granu u kodu koja se u produkciji nikad ne
/// izvršava, a u razvoju uvek — dakle granu koju niko ne testira tamo gde je
/// bitna. Prazna implementacija drži poziv istim u oba slučaja.
/// </summary>
public sealed class NoopPushSender(ILogger<NoopPushSender> logger) : IPushSender
{
    public Task<IReadOnlyList<string>> SendAsync(
        IReadOnlyList<string> tokens,
        PushMessage           message,
        CancellationToken     ct = default)
    {
        logger.LogDebug(
            "Push preskočen (Firebase nije podešen): {Kind} „{Title}“ za {Count} uređaja.",
            message.Kind, message.Title, tokens.Count);

        // Nijedan token nije mrtav — nismo ni pokušali da šaljemo.
        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}
