using System.Security.Cryptography;
using System.Text;

namespace WebhookRelay.Api.Services;

// Checks that a webhook really came from someone who knows the shared secret.
// The sender computes HMAC-SHA256 over the raw body and sends it in a header
// as "sha256=<hex>". We compute the same thing and compare.
public static class SignatureValidator
{
    private const string Prefix = "sha256=";

    public static bool IsValid(string payload, string secret, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader))
        {
            return false;
        }

        if (!signatureHeader.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] receivedHash;
        try
        {
            receivedHash = Convert.FromHexString(signatureHeader.Substring(Prefix.Length));
        }
        catch (FormatException)
        {
            // Not valid hex
            return false;
        }

        byte[] expectedHash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(payload));

        // FixedTimeEquals takes the same time whether the first byte or the last
        // byte is wrong, so an attacker can't guess the signature a byte at a time.
        return CryptographicOperations.FixedTimeEquals(receivedHash, expectedHash);
    }

    // Builds the header value for a payload. Used by the tests, and handy for
    // trying the API by hand.
    public static string ComputeHeader(string payload, string secret)
    {
        byte[] hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(payload));

        return Prefix + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
