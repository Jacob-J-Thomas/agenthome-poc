using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses;

/// <summary>Computes and verifies a canonical order-independent SHA-256 digest for one bounded typed response value.</summary>
public static class HumanInputResponseValueHash
{
    /// <summary>Computes the canonical lowercase SHA-256 digest, ordering structured fields by canonical field ID.</summary>
    /// <param name="value">The untrusted typed response value.</param>
    /// <returns>The canonical 64-character lowercase digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown before serialization when a value exceeds schema-1 bounds.</exception>
    public static string Compute(HumanInputResponseValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsBounded(value))
        {
            throw new ArgumentException("Human Input response value exceeds canonical schema-1 bounds.", nameof(value));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            HumanInputResponseCanonicalWriter.WriteValue(writer, value);
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>Determines whether the supplied digest exactly matches the canonical typed value.</summary>
    /// <param name="value">The typed response value.</param>
    /// <param name="valueHash">The candidate lowercase SHA-256 digest.</param>
    /// <returns><see langword="true"/> when the canonical digest matches in fixed time; otherwise, <see langword="false"/>.</returns>
    public static bool Matches(HumanInputResponseValue value, string? valueHash)
        => HumanInputResponseHashRules.IsSha256(valueHash) && HumanInputResponseHashRules.FixedEquals(Compute(value), valueHash);

    internal static bool IsBounded(HumanInputResponseValue value)
    {
        if (value.Text is { Length: > HumanInputLimits.MaxResponseTextCharacters }
            || value.ChoiceId is { Length: > HumanInputLimits.MaxIdentifierCharacters }
            || value.Reference?.Value is { Length: > HumanInputLimits.MaxReferenceCharacters }
            || value.StructuredFields is { IsDefault: true }
            || value.StructuredFields is { Length: > HumanInputLimits.MaxStructuredFields })
        {
            return false;
        }

        if (value.StructuredFields is not { } fields)
        {
            return true;
        }
        for (var index = 0; index < fields.Length; index++)
        {
            var field = fields[index];
            if (field is not null
                && (field.FieldId is { Length: > HumanInputLimits.MaxIdentifierCharacters }
                    || field.Text is { Length: > HumanInputLimits.MaxResponseTextCharacters }
                    || field.ChoiceId is { Length: > HumanInputLimits.MaxIdentifierCharacters }))
            {
                return false;
            }
        }
        return true;
    }
}
