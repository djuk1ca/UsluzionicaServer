using System.Security.Cryptography;
using System.Text;

namespace UsluzionicaServer.Infrastructure;

/// <summary>
/// Simetrična AES-256-CBC enkripcija teksta poruka.
///
/// Svaka poruka se enkriptuje sa random IV (Initialization Vector) koji se
/// dodaje ispred ciphertexta i sve zajedno se čuva kao Base64 string.
/// Format u bazi: Base64( [16 bajta IV] + [N bajta ciphertext] )
///
/// Ovo je "encryption at rest" — štiti sadržaj poruka ako neko dobije
/// direktan pristup SQL bazi. Server i dalje može da čita poruke (nije E2E).
/// </summary>
public sealed class MessageEncryption
{
    private readonly byte[] _key;

    public MessageEncryption(IConfiguration config)
    {
        var keyBase64 = config["Encryption:MessageKey"]
            ?? throw new InvalidOperationException("Encryption:MessageKey nije konfigurisan u appsettings.");

        try
        {
            _key = Convert.FromBase64String(keyBase64);
        }
        catch
        {
            throw new InvalidOperationException("Encryption:MessageKey mora biti validan Base64 string.");
        }

        if (_key.Length != 32)
            throw new InvalidOperationException(
                $"Encryption:MessageKey mora biti tačno 32 bajta (AES-256). Trenutno: {_key.Length} bajta.");
    }

    /// <summary>
    /// Enkriptuje plaintext i vraća Base64 string spreman za čuvanje u bazi.
    /// Svaki poziv generiše novi random IV — isti tekst = drugačiji ciphertext.
    /// </summary>
    public string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key  = _key;
        aes.Mode = CipherMode.CBC;
        aes.GenerateIV(); // 16 random bajta, novo za svaku poruku

        using var encryptor = aes.CreateEncryptor();
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext     = encryptor.TransformFinalBlock(plaintextBytes, 0, plaintextBytes.Length);

        // Spoji IV + ciphertext u jedan niz, pa enkoduj u Base64
        var result = new byte[16 + ciphertext.Length];
        Buffer.BlockCopy(aes.IV,    0, result, 0,  16);
        Buffer.BlockCopy(ciphertext, 0, result, 16, ciphertext.Length);

        return Convert.ToBase64String(result);
    }

    /// <summary>
    /// Dekriptuje Base64 string iz baze i vraća originalni tekst poruke.
    /// </summary>
    public string Decrypt(string encryptedBase64)
    {
        var data = Convert.FromBase64String(encryptedBase64);

        // Izvuci IV (prvih 16 bajta) i ciphertext (ostatak)
        var iv         = new byte[16];
        var ciphertext = new byte[data.Length - 16];
        Buffer.BlockCopy(data, 0,  iv,         0, 16);
        Buffer.BlockCopy(data, 16, ciphertext,  0, ciphertext.Length);

        using var aes = Aes.Create();
        aes.Key  = _key;
        aes.IV   = iv;
        aes.Mode = CipherMode.CBC;

        using var decryptor    = aes.CreateDecryptor();
        var plaintextBytes     = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return Encoding.UTF8.GetString(plaintextBytes);
    }

    /// <summary>
    /// Pokušava dekriptovanje — vraća placeholder ako nešto pođe po krivu
    /// (npr. stara poruka sa drugačijim ključem). Korisno za migracije.
    /// </summary>
    public string SafeDecrypt(string encryptedBase64)
    {
        try { return Decrypt(encryptedBase64); }
        catch { return "[poruka nije čitljiva]"; }
    }
}
