using System.Security.Cryptography;

namespace ProMeter.Companion;

/// <summary>
/// Computes a Chromium unpacked-extension ID from a manifest "key" (base64 DER
/// SubjectPublicKeyInfo), using the same derivation Chromium uses for packed
/// extensions: SHA-256 of the public key bytes, first 16 bytes, each nibble
/// mapped from 0-9a-f to a-p. This makes an unpacked extension's ID depend only
/// on its manifest key, not on the filesystem path it was loaded from.
/// </summary>
public static class ChromiumExtensionId
{
    public static bool TryCompute(string? base64PublicKey, out string? extensionId)
    {
        extensionId = null;
        if (string.IsNullOrWhiteSpace(base64PublicKey))
        {
            return false;
        }

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(base64PublicKey.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        if (keyBytes.Length == 0)
        {
            return false;
        }

        var hash = SHA256.HashData(keyBytes);
        var id = new char[32];
        for (var i = 0; i < 16; i++)
        {
            var b = hash[i];
            id[i * 2] = (char)('a' + (b >> 4));
            id[i * 2 + 1] = (char)('a' + (b & 0x0F));
        }

        extensionId = new string(id);
        return true;
    }
}
