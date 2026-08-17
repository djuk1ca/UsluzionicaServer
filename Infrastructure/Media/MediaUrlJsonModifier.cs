using System.Text.Json.Serialization.Metadata;

namespace UsluzionicaServer.Infrastructure.Media;

/// <summary>
/// Pretvara relativne putanje slika u pune URL-ove u TRENUTKU SERIJALIZACIJE.
///
/// Zašto ovako, a ne u svakom mapperu: postoji preko 20 mesta u servisima koja
/// pune *ImageUrl polja DTO-ova (ListingService, ProviderService, ReviewService,
/// ConversationService, BookingService, FavoriteService, UserService, ChatHub).
/// Ručna izmena svakog bi značila da jedno propušteno mesto tiho vraća
/// neupotrebljiv URL, i da svaki novi DTO mora da se seti istog koraka.
///
/// Ovako pravilo živi na jednom mestu i važi automatski za svako string polje
/// čije se ime završava na "ImageUrl" — i za sve buduće DTO-ove.
///
/// Transformacija je JEDNOSMERNA (samo pisanje/odgovor). Deserijalizacija
/// zahteva ostaje netaknuta, pa klijent koji slučajno pošalje pun URL nazad
/// neće upisati domen u bazu — za to se stara MediaUrls.ToRelative na upisu.
/// </summary>
public static class MediaUrlJsonModifier
{
    private const string Suffix = "ImageUrl";

    /// <summary>
    /// Pravi modifier vezan za dati baseUrl.
    /// Registruje se kroz DefaultJsonTypeInfoResolver.Modifiers.
    /// </summary>
    public static Action<JsonTypeInfo> Create(string baseUrl) => typeInfo =>
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object) return;

        foreach (var property in typeInfo.Properties)
        {
            if (property.PropertyType != typeof(string)) continue;
            if (!property.Name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) continue;

            var originalGet = property.Get;
            if (originalGet is null) continue;

            property.Get = obj => MediaUrls.ToAbsolute(originalGet(obj) as string, baseUrl);
        }
    };
}
