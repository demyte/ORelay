using System.Net;

namespace ORelay.Server;

public static class CallbackDestination
{
    public const int MaxLength = 2048;

    public static bool TryValidate(
        string? value,
        bool allowNonLoopback,
        out Uri? destination,
        out string errorCode)
    {
        destination = null;
        errorCode = "invalid_callback_url";

        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxLength || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) ||
            value.Contains('?') || value.Contains('#') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(parsed.Host) || parsed.Port is < 1 or > 65_535 ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            parsed.GetLeftPart(UriPartial.Authority).Contains('@', StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment) ||
            string.IsNullOrEmpty(parsed.AbsolutePath) || IsWildcardHost(parsed.Host))
        {
            return false;
        }

        if (!allowNonLoopback && !IsLoopbackHost(parsed.Host))
        {
            errorCode = "callback_must_be_loopback";
            return false;
        }

        destination = parsed;
        return true;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            return IPAddress.IsLoopback(address);
        }

        return false;
    }

    private static bool IsWildcardHost(string host) =>
        host.Trim('[', ']') is "0.0.0.0" or "::" or "*" or "+";
}
