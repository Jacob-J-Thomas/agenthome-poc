using System.Globalization;
using System.Text;
using EmbodySense.Core.Common.Loops.Posture;
using EmbodySense.Core.Common.Triggers;

namespace EmbodySense.Core.Persistence.Triggers;

/// <summary>Encodes and validates generation-bound queue evidence cursors.</summary>
internal static class TriggerQueueOperationalCursor
{
    private const string Prefix = "q1";
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    /// <summary>Creates the exact cursor for the next canonical snapshot offset.</summary>
    public static string Create(long generation, int nextIndex, TriggerDeliveryId previousDeliveryId)
    {
        ArgumentNullException.ThrowIfNull(previousDeliveryId);
        if (generation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }
        if (nextIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(nextIndex));
        }

        var identity = Base64UrlEncode(Encoding.UTF8.GetBytes(previousDeliveryId.Value));
        return string.Join('.', Prefix, generation.ToString(CultureInfo.InvariantCulture), nextIndex.ToString(CultureInfo.InvariantCulture), identity);
    }

    /// <summary>Parses only the canonical schema-1 cursor spelling.</summary>
    public static bool TryParse(string? value, out long generation, out int nextIndex, out TriggerDeliveryId? previousDeliveryId)
    {
        generation = 0;
        nextIndex = 0;
        previousDeliveryId = null;
        if (!GovernedLoopOperationalContract.IsQueueCursor(value))
        {
            return false;
        }

        var parts = value!.Split('.');
        if (parts.Length != 4
            || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out generation)
            || generation < 0
            || !string.Equals(parts[1], generation.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out nextIndex)
            || nextIndex < 1
            || !string.Equals(parts[2], nextIndex.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            || !TryBase64UrlDecode(parts[3], out var identityBytes))
        {
            return false;
        }

        try
        {
            var identity = _strictUtf8.GetString(identityBytes);
            return TriggerDeliveryId.TryParse(identity, out previousDeliveryId)
                && string.Equals(identity, previousDeliveryId!.Value, StringComparison.Ordinal)
                && string.Equals(value, Create(generation, nextIndex, previousDeliveryId), StringComparison.Ordinal);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] result)
    {
        result = [];
        if (value.Length == 0 || value.Length % 4 == 1 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            result = Convert.FromBase64String(padded);
            return string.Equals(value, Base64UrlEncode(result), StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
