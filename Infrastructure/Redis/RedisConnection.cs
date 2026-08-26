using StackExchange.Redis;

namespace UsluzionicaServer.Infrastructure.Redis;

/// <summary>
/// Jedna deljena veza ka Redis-u za ceo proces.
///
/// PRAVILO CELE FAZE B: Redis je UBRZANJE I DELJENO STANJE, NIKAD USLOV ZA RAD.
/// Aplikacija mora da se digne i da radi i kad Redis uopšte nije konfigurisan
/// (lokalni razvoj, integracioni testovi) i kad je konfigurisan ali mrtav
/// (ispao je u produkciji). Nijedan korisnički zahtev ne sme pasti zbog keša.
///
/// Dve stvari to omogućavaju:
///
/// 1. `AbortOnConnectFail = false` — bez toga `ConnectAsync` baca izuzetak ako
///    Redis nije tu u trenutku starta, i ceo proces umire. Sa njim, biblioteka
///    vrati objekat koji je „trenutno nepovezan" i sama se povezuje u pozadini
///    čim Redis oživi. Ovo je najvažnija jedna linija u celoj Fazi B.
///
/// 2. <see cref="IsAvailable"/> — svaki pozivalac prvo pita da li Redis uopšte
///    postoji, umesto da hvata izuzetke po celom kodu.
///
/// StackExchange.Redis je namerno singleton: `ConnectionMultiplexer` je
/// thread-safe, multipleksira sve komande preko JEDNE TCP veze i skup je za
/// pravljenje. Otvaranje veze po zahtevu je klasična greška koja obori Redis
/// brže nego bilo koje opterećenje.
/// </summary>
public sealed class RedisConnection : IAsyncDisposable
{
    private readonly IConnectionMultiplexer?  _mux;
    private readonly ILogger<RedisConnection> _logger;

    /// <summary>True samo ako je Redis konfigurisan I trenutno dostupan.</summary>
    public bool IsAvailable => _mux is { IsConnected: true };

    /// <summary>True ako je konekcija uopšte konfigurisana (bez obzira na trenutno stanje).</summary>
    public bool IsConfigured => _mux is not null;

    /// <summary>Multiplexer za slučajeve koji traže direktan pristup (SignalR, Data Protection).</summary>
    public IConnectionMultiplexer? Multiplexer => _mux;

    public RedisConnection(IConfiguration config, ILogger<RedisConnection> logger)
    {
        _logger = logger;

        var connectionString = config["Redis:Connection"];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Nije greška — ovo je podrazumevano stanje u testovima i pri
            // lokalnom pokretanju bez Docker-a.
            _logger.LogInformation(
                "Redis nije konfigurisan (Redis:Connection prazan). " +
                "Keš, deljeno stanje i backplane rade u režimu jedne instance.");
            return;
        }

        try
        {
            var options = ConfigurationOptions.Parse(connectionString);

            // Bez ovoga start aplikacije zavisi od toga da li je Redis živ.
            options.AbortOnConnectFail = false;

            // Kratki tajmauti: keš koji kasni je gori od keša kog nema, jer
            // usporava svaki zahtev umesto da ga samo ne ubrza.
            options.ConnectTimeout = 5_000;
            options.SyncTimeout    = 3_000;

            // Ime u `CLIENT LIST` — kad se debuguje na serveru, odmah se vidi
            // koja aplikacija drži koju vezu.
            options.ClientName = "usluzionica-api";

            _mux = ConnectionMultiplexer.Connect(options);

            _mux.ConnectionFailed  += (_, e) => _logger.LogWarning(
                "Redis veza pala ({Type}): {Message}. Prelazim na rad bez keša.",
                e.FailureType, e.Exception?.Message);

            _mux.ConnectionRestored += (_, _) => _logger.LogInformation(
                "Redis veza ponovo uspostavljena.");

            _logger.LogInformation(
                "Redis konfigurisan: {Endpoint} (povezan: {Connected})",
                connectionString, _mux.IsConnected);
        }
        catch (Exception ex)
        {
            // Čak i sa AbortOnConnectFail=false, Parse može pući na neispravnom
            // stringu. I to ne sme oboriti aplikaciju.
            _logger.LogError(ex,
                "Redis konekcija nije mogla biti napravljena. Nastavljam bez njega.");
            _mux = null;
        }
    }

    /// <summary>
    /// Baza za komande, ili null ako Redis nije dostupan.
    /// Pozivaoci treba da provere null i tiho preskoče — ne da bacaju.
    /// </summary>
    public IDatabase? GetDatabase() => IsAvailable ? _mux!.GetDatabase() : null;

    /// <summary>Subscriber za pub/sub (invalidacija keša), ili null.</summary>
    public ISubscriber? GetSubscriber() => IsAvailable ? _mux!.GetSubscriber() : null;

    public async ValueTask DisposeAsync()
    {
        if (_mux is not null)
            await _mux.CloseAsync();
    }
}
