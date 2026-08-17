namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Proverava na startu da su sve obavezne tajne popunjene i da nisu ostale
/// na placeholder vrednostima. Pada odmah, sa jasnom porukom šta nedostaje —
/// bolje nego da server krene pa tek na prvom loginu pukne, ili (gore) da
/// radi sa slabim/podrazumevanim ključem u produkciji.
/// </summary>
public static class SecretsGuard
{
    /// <summary>Vrednosti koje su nekad bile commit-ovane u repo — ne smeju se
    /// koristiti nigde više, a posebno ne u produkciji.</summary>
    private static readonly string[] KnownLeaked =
    [
        "CHANGE_THIS_TO_A_LONG_RANDOM_SECRET_KEY_MIN_32_CHARS",
        "GhlRzVRWbwzeGsLSaDYJPgwax76kKwai0jFwBjmowRA=",
        "dGhpcyBpcyBhIGRldiBrZXkgb2YgMzIgYnl0ZXMhISE="
    ];

    public static void Validate(IConfiguration config, IHostEnvironment env)
    {
        var missing = new List<string>();

        Require(config, "ConnectionStrings:DefaultConnection", "ConnectionStrings__DefaultConnection", missing);
        Require(config, "Jwt:Secret",                          "Jwt__Secret",                          missing);
        Require(config, "Encryption:MessageKey",               "Encryption__MessageKey",               missing);
        Require(config, "AdminSeed:Password",                  "AdminSeed__Password",                  missing);

        // Email nije kritičan za start u Development-u (registracija tolerише
        // pad slanja), ali u produkciji bez njega niko ne može da potvrdi nalog
        // niti da resetuje lozinku.
        if (env.IsProduction())
        {
            Require(config, "Email:Host",     "Email__Host",     missing);
            Require(config, "Email:Password", "Email__Password", missing);
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Nedostaju obavezne konfiguracione vrednosti:" + Environment.NewLine +
                string.Join(Environment.NewLine, missing.Select(m => "  • " + m)) + Environment.NewLine +
                Environment.NewLine +
                "Lokalni razvoj: popuni ih u appsettings.Local.json " +
                "(šablon: appsettings.Local.json.example)." + Environment.NewLine +
                "Produkcija: postavi ih kao environment varijable.");
        }

        // Jwt:Secret mora biti dovoljno dugačak za HMAC-SHA256.
        var jwtSecret = config["Jwt:Secret"]!;
        if (jwtSecret.Length < 32)
        {
            throw new InvalidOperationException(
                $"Jwt:Secret je prekratak ({jwtSecret.Length} znakova). " +
                "Potrebno je najmanje 32 znaka za HMAC-SHA256.");
        }

        // Kompromitovane vrednosti — u produkciji tvrda greška, u razvoju
        // upozorenje (dev ključ je namerno zadržan da lokalne poruke ostanu čitljive).
        var leaked = new[] { "Jwt:Secret", "Encryption:MessageKey" }
            .Where(key => KnownLeaked.Contains(config[key]))
            .ToList();

        if (leaked.Count > 0)
        {
            var msg = "Konfiguracija koristi vrednosti koje su bile commit-ovane u git: " +
                      string.Join(", ", leaked) + ". Zameni ih novim, nasumičnim vrednostima.";

            if (env.IsProduction())
                throw new InvalidOperationException(msg);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("[UPOZORENJE] " + msg);
            Console.ResetColor();
        }
    }

    private static void Require(IConfiguration config, string key, string envVarName, List<string> missing)
    {
        if (string.IsNullOrWhiteSpace(config[key]))
            missing.Add($"{key}  (env: {envVarName})");
    }
}
