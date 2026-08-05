namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Defines bounded schema-version-1 catalog transport and retention limits.</summary>
public static class CapabilityCatalogLimits
{
    /// <summary>Gets the maximum entries retained by one workspace catalog.</summary>
    public const int MaximumEntries = 512;
    /// <summary>Gets the maximum durable operation receipts retained without eviction.</summary>
    public const int MaximumOperationReceipts = 4_096;
    /// <summary>Gets the maximum returned page size.</summary>
    public const int MaximumPageSize = 100;
    /// <summary>Gets the maximum operation identifier length.</summary>
    public const int MaximumOperationIdCharacters = 120;
    /// <summary>Gets the maximum canonical catalog artifact size.</summary>
    public const int MaximumArtifactUtf8Bytes = 4 * 1024 * 1024;
    /// <summary>Gets the maximum monotonic workspace anchors retained by one file-backed server trust root.</summary>
    public const int MaximumTrustAnchors = 1_024;
    /// <summary>Gets the maximum serialized size of one file-backed server trust anchor.</summary>
    public const int MaximumTrustAnchorUtf8Bytes = 16 * 1024;
    /// <summary>Gets the maximum aggregate bytes retained across one file-backed server trust root.</summary>
    public const int MaximumTrustAnchorRootBytes = 4 * 1024 * 1024;
}
