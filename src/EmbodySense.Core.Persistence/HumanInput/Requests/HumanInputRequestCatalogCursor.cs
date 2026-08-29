using System.Text;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Persistence.HumanInput.Requests.Models;

namespace EmbodySense.Core.Persistence.HumanInput.Requests;

/// <summary>Encodes a bounded opaque cursor pinned to one authenticated Human Input ledger generation.</summary>
internal static class HumanInputRequestCatalogCursor
{
    private const string Version = "v1";

    internal static string Create(long generation, string contentDigest, string lastRequestId)
    {
        return string.Join(
            '.',
            Version,
            generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Encode(lastRequestId),
            Encode(contentDigest));
    }

    internal static bool TryParse(string value, out HumanInputRequestCatalogCursorValue cursor)
    {
        cursor = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
        {
            return false;
        }

        var parts = value.Split('.', StringSplitOptions.None);
        if (parts.Length != 4
            || !string.Equals(parts[0], Version, StringComparison.Ordinal)
            || !long.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var generation)
            || generation < 0
            || !TryDecode(parts[2], out var lastRequestId)
            || !TryDecode(parts[3], out var contentDigest)
            || !HumanInputIdentifier.IsValid(lastRequestId)
            || contentDigest is not { Length: 71 }
            || !contentDigest.StartsWith("sha256:", StringComparison.Ordinal)
            || !contentDigest[7..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return false;
        }

        cursor = new HumanInputRequestCatalogCursorValue(generation, contentDigest, lastRequestId);
        return true;
    }

    private static string Encode(string value)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecode(string value, out string decoded)
    {
        decoded = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return Encoding.UTF8.GetByteCount(decoded) <= 256;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
