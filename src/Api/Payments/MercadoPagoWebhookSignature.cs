using System.Security.Cryptography;
using System.Text;

namespace CatCar.Billing.Api.Payments;

public static class MercadoPagoWebhookSignature
{
    public static bool IsValid(string? signature, string? requestId, string? dataId, string? secret)
    {
        // The payment ID must come from the signed query string, not the notification body.
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(dataId) || string.IsNullOrWhiteSpace(secret))
            return false;

        string? timestamp = null;
        string? digest = null;
        foreach (var part in signature.Split(','))
        {
            var separator = part.IndexOf('=');
            if (separator < 0)
                continue;
            var name = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (name == "ts")
            {
                if (timestamp is not null)
                    return false;
                timestamp = value;
            }
            else if (name == "v1")
            {
                if (digest is not null)
                    return false;
                digest = value;
            }
        }

        if (string.IsNullOrEmpty(timestamp) || !timestamp.All(char.IsAsciiDigit) || digest is null || digest.Length != 64)
            return false;

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(digest);
        }
        catch (FormatException)
        {
            return false;
        }

        var manifest = $"id:{dataId.ToLowerInvariant()};";
        if (!string.IsNullOrEmpty(requestId))
            manifest += $"request-id:{requestId};";
        manifest += $"ts:{timestamp};";
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(manifest));
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
