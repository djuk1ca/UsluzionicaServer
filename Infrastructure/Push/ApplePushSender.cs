using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using UsluzionicaServer.Infrastructure.Apple;

namespace UsluzionicaServer.Infrastructure.Push;

/// <summary>
/// Push na iPhone — direktno preko APNs-a, bez Firebase iOS SDK-a.
///
/// Zašto ne Firebase i za iOS: aplikacija bi tada morala da nosi Firebase iOS
/// biblioteku (binding za .NET je nepouzdan), samo da bi APNs token pretvorila
/// u FCM token. iOS sam daje APNs token, a slanje je jedan HTTP/2 poziv
/// potpisan istim `.p8` ključem kao Sign in with Apple (<see cref="AppleKljuc"/>).
///
/// Isti ugovor kao <see cref="IPushSender"/>: vraća tokene koje je APNs
/// odbio kao nepostojeće, a brisanje radi <c>NotificationService</c>.
/// </summary>
public interface IApplePushSender
{
    Task<IReadOnlyList<string>> SendAsync(
        IReadOnlyList<string> tokens,
        PushMessage           message,
        CancellationToken     ct = default);
}

public sealed class ApplePushSender(
    IHttpClientFactory        http,
    AppleKljuc                kljuc,
    IConfiguration            config,
    ILogger<ApplePushSender>  logger) : IApplePushSender
{
    public const string HttpKlijent = "apns";

    /// <summary>
    /// Produkcijski APNs važi za TestFlight i App Store. Sandbox samo za build
    /// pokrenut iz Xcode-a/Visual Studio-a na Mac-u — tamo se postavlja
    /// Apns:Production=false. Token iz pogrešnog okruženja APNs odbija sa
    /// BadDeviceToken, pa se pri mešanju samo obriše, ništa ne puca.
    /// </summary>
    private bool Produkcija => config.GetValue("Apns:Production", true);

    private string Host => Produkcija ? "https://api.push.apple.com" : "https://api.sandbox.push.apple.com";

    /// <summary>Razlozi posle kojih token više nikad neće raditi.</summary>
    private static readonly HashSet<string> MrtviRazlozi = new(StringComparer.Ordinal)
    {
        "BadDeviceToken", "Unregistered", "ExpiredToken"
    };

    public async Task<IReadOnlyList<string>> SendAsync(
        IReadOnlyList<string> tokens,
        PushMessage           message,
        CancellationToken     ct = default)
    {
        if (tokens.Count == 0) return [];

        var telo = Telo(message);
        var mrtvi = new List<string>();
        var klijent = http.CreateClient(HttpKlijent);

        foreach (var token in tokens)
        {
            using var zahtev = new HttpRequestMessage(HttpMethod.Post, $"{Host}/3/device/{token}")
            {
                Version       = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content       = JsonContent.Create(telo)
            };
            zahtev.Headers.Authorization = new AuthenticationHeaderValue("bearer", kljuc.ApnsToken());
            zahtev.Headers.Add("apns-topic", kljuc.BundleId);
            zahtev.Headers.Add("apns-push-type", "alert");
            zahtev.Headers.Add("apns-priority", "10");

            try
            {
                using var odgovor = await klijent.SendAsync(zahtev, ct);
                if (odgovor.IsSuccessStatusCode) continue;

                var razlog = await Razlog(odgovor, ct);

                // 410 = uređaj je odjavio aplikaciju (obrisana); ostalo po razlogu.
                if (odgovor.StatusCode == HttpStatusCode.Gone || MrtviRazlozi.Contains(razlog ?? ""))
                {
                    mrtvi.Add(token);
                    continue;
                }

                // Sve drugo je naša greška (ključ, topic) ili prolazna — token se
                // NE briše, samo se beleži.
                logger.LogWarning("APNs odbio push ({Status} {Razlog}).", (int)odgovor.StatusCode, razlog);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "APNs nije dostupan.");
            }
        }

        return mrtvi;
    }

    /// <summary>
    /// Isti podaci kao FCM poruka: naslov i telo za obaveštenje, a `kind` i
    /// `referenceId` van `aps` — aplikacija iz njih pravi putanju na tap
    /// (DeepLinks.ZaPayload), kao na Androidu.
    /// </summary>
    public static Dictionary<string, object?> Telo(PushMessage m) => new()
    {
        ["aps"] = new Dictionary<string, object?>
        {
            ["alert"] = new Dictionary<string, string> { ["title"] = m.Title, ["body"] = m.Body },
            ["sound"] = "default"
        },
        ["kind"]           = m.Kind,
        ["referenceId"]    = m.ReferenceId?.ToString(),
        ["notificationId"] = m.NotificationId.ToString()
    };

    private static async Task<string?> Razlog(HttpResponseMessage odgovor, CancellationToken ct)
    {
        try
        {
            var json = await odgovor.Content.ReadFromJsonAsync<JsonElement>(ct);
            return json.TryGetProperty("reason", out var r) ? r.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>Kad Apple ključ nije podešen — kao <see cref="NoopPushSender"/>.</summary>
public sealed class NoopApplePushSender(ILogger<NoopApplePushSender> logger) : IApplePushSender
{
    public Task<IReadOnlyList<string>> SendAsync(
        IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default)
    {
        logger.LogDebug("iOS push preskočen (Apple ključ nije podešen): {Kind} za {Count} uređaja.",
            message.Kind, tokens.Count);
        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}
