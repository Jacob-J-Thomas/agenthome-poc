namespace EmbodySense.Core.Application.Credentials;

/// <summary>Represents a provider-issued opaque locator, never credential material, a key, or an envelope.</summary>
public sealed class CredentialProviderLocator : IEquatable<CredentialProviderLocator>
{
    private CredentialProviderLocator(string value) => Value = value;

    /// <summary>Gets the opaque provider-issued locator token.</summary>
    public string Value { get; }

    /// <summary>Parses the closed provider-issued opaque locator shape.</summary>
    public static bool TryParse(string? value, out CredentialProviderLocator? locator)
    {
        if (value is null || value.Length != 68 || !value.StartsWith("loc_", StringComparison.Ordinal) || !value.AsSpan(4).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            locator = null;
            return false;
        }

        locator = new CredentialProviderLocator(value);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(CredentialProviderLocator? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is CredentialProviderLocator other && Equals(other);
    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    /// <inheritdoc />
    public override string ToString() => Value;
}
