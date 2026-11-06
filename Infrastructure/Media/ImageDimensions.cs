using System.Buffers.Binary;

namespace UsluzionicaServer.Infrastructure.Media;

/// <summary>
/// Širina i visina slike iz zaglavlja — JPEG, PNG i WebP, bez biblioteke za
/// slike. Treba samo za proporciju balona u četu, pa se piksel ne dekodira.
/// Formate je već proverio <see cref="ImageUploads"/>; ovde neispravno
/// zaglavlje znači samo (0, 0), a ne grešku.
/// </summary>
public static class ImageDimensions
{
    public static (int Width, int Height) Read(ReadOnlySpan<byte> b)
    {
        try
        {
            if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
                return ((int)BinaryPrimitives.ReadUInt32BigEndian(b[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(b[20..]));

            if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
                return Jpeg(b);

            if (b.Length > 30 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8))
                return WebP(b);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Odsečen fajl — dimenzije nepoznate.
        }
        return (0, 0);
    }

    static (int, int) Jpeg(ReadOnlySpan<byte> b)
    {
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }

            var duzina = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);

            // SOF0–SOF15, osim DHT (C4), JPG (C8) i DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                return (BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]));

            i += 2 + duzina;
        }
        return (0, 0);
    }

    static (int, int) WebP(ReadOnlySpan<byte> b)
    {
        var chunk = b[12..16];
        if (chunk.SequenceEqual("VP8X"u8))
            return (1 + (b[24] | b[25] << 8 | b[26] << 16), 1 + (b[27] | b[28] << 8 | b[29] << 16));
        if (chunk.SequenceEqual("VP8L"u8))
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);
            return ((int)(v & 0x3FFF) + 1, (int)((v >> 14) & 0x3FFF) + 1);
        }
        if (chunk.SequenceEqual("VP8 "u8))
            return (BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFF);
        return (0, 0);
    }
}
