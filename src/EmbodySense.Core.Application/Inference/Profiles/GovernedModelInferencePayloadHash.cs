using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Inference.Profiles;

namespace EmbodySense.Core.Application.Inference.Profiles;

internal static class GovernedModelInferencePayloadHash
{
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    internal static bool TryCompute(LlmInferenceRequest? request, out string? contentHash)
    {
        contentHash = null;
        try
        {
            if (request?.Messages is not { Count: > 0 } messages
                || messages.Count > GovernedModelInferencePayloadLimits.MaxMessages
                || request.Options is null)
            {
                return false;
            }

            var budget = new PayloadBudget();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendTrusted(hash, "embodysense.model-attempt-input-payload.v1");
            AppendTrusted(hash, messages.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var message in messages)
            {
                if (message is null
                    || !Enum.IsDefined(message.Role)
                    || string.IsNullOrWhiteSpace(message.Content)
                    || !AppendPayload(hash, message.Content, ref budget))
                {
                    return false;
                }
                AppendTrusted(hash, ((int)message.Role).ToString(CultureInfo.InvariantCulture));
            }

            AppendTrusted(hash, request.Options.Temperature?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            AppendTrusted(hash, request.Options.MaxOutputTokenCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            var instructions = request.InstructionContext;
            AppendTrusted(hash, instructions is null ? "0" : "1");
            if (instructions is not null)
            {
                if (instructions.Governance is null
                    || instructions.TrustedInstructions is null
                    || instructions.TrustedInstructions.Count > GovernedModelInferencePayloadLimits.MaxTrustedInstructions
                    || !AppendPayload(hash, instructions.Governance.Version, ref budget)
                    || !AppendPayload(hash, instructions.Governance.Content, ref budget)
                    || !AppendPayload(hash, instructions.Governance.ContentHash, ref budget))
                {
                    return false;
                }
                AppendTrusted(hash, instructions.PreserveExactLogicalContext ? "1" : "0");
                AppendTrusted(hash, instructions.TrustedInstructions.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var instruction in instructions.TrustedInstructions)
                {
                    if (instruction is null
                        || !AppendPayload(hash, instruction.SourceId, ref budget)
                        || !AppendPayload(hash, instruction.Content, ref budget))
                    {
                        return false;
                    }
                }
            }

            contentHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool AppendPayload(IncrementalHash hash, string? value, ref PayloadBudget budget)
    {
        if (value is null
            || value.Length > GovernedModelInferencePayloadLimits.MaxSegmentCharacters
            || budget.Characters > GovernedModelInferencePayloadLimits.MaxAggregateCharacters - value.Length)
        {
            return false;
        }

        int byteCount;
        try
        {
            byteCount = _strictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        if (budget.Utf8Bytes > GovernedModelInferencePayloadLimits.MaxAggregateUtf8Bytes - byteCount)
        {
            return false;
        }

        budget = new PayloadBudget(budget.Characters + value.Length, budget.Utf8Bytes + byteCount);
        AppendTrusted(hash, value);
        return true;
    }

    private static void AppendTrusted(IncrementalHash hash, string value)
    {
        var bytes = _strictUtf8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private readonly record struct PayloadBudget(int Characters = 0, int Utf8Bytes = 0);
}
