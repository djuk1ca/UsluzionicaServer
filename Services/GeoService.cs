using System.Text.Json;

namespace UsluzionicaServer.Services;

/// <summary>
/// Vraća naziv grada na osnovu IP adrese.
/// Koristi besplatni ip-api.com (bez API ključa, 45 zahteva/min).
/// </summary>
public sealed class GeoService(IHttpClientFactory httpClientFactory, ILogger<GeoService> logger)
{
    public async Task<string?> GetCityAsync(string? ipAddress)
    {
        // Lokalne i privatne adrese nemaju geolokaciju
        if (string.IsNullOrWhiteSpace(ipAddress) ||
            ipAddress is "::1" or "127.0.0.1" ||
            ipAddress.StartsWith("192.168.") ||
            ipAddress.StartsWith("10."))
        {
            logger.LogDebug("Geolokacija preskočena za lokalnu IP: {IP}", ipAddress);
            return null;
        }

        try
        {
            var client   = httpClientFactory.CreateClient("GeoApi");
            var response = await client.GetStringAsync($"http://ip-api.com/json/{ipAddress}?fields=city,status");
            var doc      = JsonDocument.Parse(response);

            if (doc.RootElement.GetProperty("status").GetString() == "success")
                return doc.RootElement.GetProperty("city").GetString();
        }
        catch (Exception ex)
        {
            // Geolokacija nije kritična — nikad ne smemo blokirati login zbog ovoga
            logger.LogWarning(ex, "Geolokacija nije uspela za IP: {IP}", ipAddress);
        }

        return null;
    }
}
