using System.Buffers;
using System.Globalization;
using System.Text;
using EmbodySense.Core.Common.Loops.Execution;

namespace EmbodySense.Core.Application.Loops.Compatibility;

internal static class GovernedLoopCompatibilityValueGuard
{
    internal static string RequireSourceIdentifier(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || !value.IsNormalized(NormalizationForm.FormC)
            || HasUnsafeOrMalformedUnicode(value))
        {
            throw new ArgumentException($"Compatibility source identities must be NFC, free of unsafe Unicode categories, and no longer than {GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters} characters.", parameterName);
        }

        return value;
    }

    internal static string? RequireOptionalSourceIdentifier(string? value, string parameterName)
    {
        return value is null ? null : RequireSourceIdentifier(value, parameterName);
    }

    internal static long RequireGeneration(long value, string parameterName)
    {
        if (value is < 1 or > GovernedLoopExecutionLimits.MaxExecutionGeneration)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Compatibility source generations must be positive and no greater than {GovernedLoopExecutionLimits.MaxExecutionGeneration}.");
        }

        return value;
    }

    internal static TEnum RequireConcrete<TEnum>(TEnum value, string parameterName) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value) || EqualityComparer<TEnum>.Default.Equals(value, default))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Choose a supported concrete compatibility classification.");
        }

        return value;
    }

    internal static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Compatibility observations require a non-default UTC source timestamp.");
        }

        return value;
    }

    private static bool IsUnsafe(Rune value)
    {
        return Rune.GetUnicodeCategory(value) is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.PrivateUse
            or UnicodeCategory.Surrogate
            or UnicodeCategory.OtherNotAssigned;
    }

    private static bool HasUnsafeOrMalformedUnicode(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done || consumed == 0 || IsUnsafe(rune))
            {
                return true;
            }

            remaining = remaining[consumed..];
        }

        return false;
    }
}
