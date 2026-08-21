namespace EmbodySense.Core.Common.Credentials.Leases;

/// <summary>Defines the closed schema-1 bounds for value-free credential leases.</summary>
public static class CredentialLeaseContractLimits
{
    /// <summary>Gets the maximum time between trusted issuance and redemption-boundary entry.</summary>
    public static readonly TimeSpan MaximumEntryLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Gets the maximum retained phase versions for one lease attempt.</summary>
    public const int MaximumVersions = 8;

    /// <summary>Gets the maximum encoded attempt-history size.</summary>
    public const int MaximumRecordUtf8Bytes = 65_536;

    /// <summary>Gets the maximum safe purpose length.</summary>
    public const int MaximumPurposeCharacters = 1_024;
}
