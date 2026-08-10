using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses;

/// <summary>Computes, applies, and verifies the order-sensitive canonical digest for one immutable response selection.</summary>
public static class HumanInputResponseSelectionHash
{
    /// <summary>Computes a lowercase SHA-256 digest that preserves the exact selected reference order.</summary>
    /// <param name="selection">The selection to serialize canonically.</param>
    /// <returns>The canonical 64-character lowercase digest.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="selection"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown before serialization when the selection exceeds schema-1 bounds.</exception>
    public static string Compute(HumanInputResponseSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!IsBounded(selection))
        {
            throw new ArgumentException("Human Input response selection exceeds canonical schema-1 bounds.", nameof(selection));
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", selection.SchemaVersion);
            HumanInputResponseCanonicalWriter.WriteString(writer, "selectionId", selection.SelectionId);
            writer.WritePropertyName("request");
            HumanInputResponseCanonicalWriter.WriteRequestReference(writer, selection.Request);
            writer.WriteNumber("policyKind", (int)selection.PolicyKind);
            writer.WritePropertyName("responses");
            writer.WriteStartArray();
            foreach (var response in selection.Responses)
            {
                HumanInputResponseCanonicalWriter.WriteResponseReference(writer, response);
            }
            writer.WriteEndArray();
            HumanInputResponseCanonicalWriter.WriteString(writer, "selectorActorId", selection.SelectorActorId?.Value);
            HumanInputResponseCanonicalWriter.WriteString(writer, "selectorRoleId", selection.SelectorRoleId);
            HumanInputResponseCanonicalWriter.WriteUtc(writer, "selectedAtUtc", selection.SelectedAtUtc);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan)).ToLowerInvariant();
    }

    /// <summary>Returns a selection copy with its canonical digest applied.</summary>
    /// <param name="selection">The selection candidate.</param>
    /// <returns>The selection with its canonical hash.</returns>
    public static HumanInputResponseSelection Apply(HumanInputResponseSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var unhashed = selection with { SelectionHash = string.Empty };
        return unhashed with { SelectionHash = Compute(unhashed) };
    }

    /// <summary>Determines whether the stored selection digest matches the exact ordered selection.</summary>
    /// <param name="selection">The selection to verify.</param>
    /// <returns><see langword="true"/> when the digest matches in fixed time; otherwise, <see langword="false"/>.</returns>
    public static bool Matches(HumanInputResponseSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return HumanInputResponseHashRules.IsSha256(selection.SelectionHash)
            && HumanInputResponseHashRules.FixedEquals(Compute(selection), selection.SelectionHash);
    }

    internal static bool IsBounded(HumanInputResponseSelection selection)
    {
        if (selection.SelectionId is null or { Length: > HumanInputLimits.MaxIdentifierCharacters }
            || selection.Request is null
            || selection.Request.RequestId is null or { Length: > HumanInputLimits.MaxIdentifierCharacters }
            || selection.Request.RequestVersionId is null or { Length: > HumanInputLimits.MaxIdentifierCharacters }
            || selection.Request.RequestHash is null or { Length: > HumanInputLimits.Sha256HexCharacters }
            || selection.Responses.IsDefault
            || selection.Responses.Length > HumanInputResponseContractLimits.MaxSelectedResponses
            || selection.SelectorActorId?.Value.Length > HumanInputLimits.MaxIdentifierCharacters
            || selection.SelectorRoleId is { Length: > HumanInputLimits.MaxIdentifierCharacters })
        {
            return false;
        }

        for (var index = 0; index < selection.Responses.Length; index++)
        {
            var response = selection.Responses[index];
            if (response is null
                || response.ResponseId is null or { Length: > HumanInputLimits.MaxIdentifierCharacters }
                || response.ValueHash is null or { Length: > HumanInputLimits.Sha256HexCharacters }
                || response.ResponseHash is null or { Length: > HumanInputLimits.Sha256HexCharacters })
            {
                return false;
            }
        }
        return true;
    }
}
