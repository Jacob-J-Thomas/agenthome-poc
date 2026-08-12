namespace EmbodySense.Core.Common.Triggers;

/// <summary>Identifies the canonical trigger-worker operation identity shape.</summary>
public static class TriggerDispatchOperationId
{
    /// <summary>Gets the fixed prefix for trigger-worker dispatch operation identities.</summary>
    public const string Prefix = "trigger-";

    /// <summary>Determines whether a value is the exact trigger-worker operation identity shape.</summary>
    /// <param name="value">The candidate operation identity.</param>
    /// <returns><see langword="true"/> only for the fixed prefix followed by 64 lowercase hexadecimal characters.</returns>
    public static bool IsValid(string? value)
        => value?.Length == Prefix.Length + TriggerDeliveryLimits.Sha256HexCharacters
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && value[Prefix.Length..].All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
