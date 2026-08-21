namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>Configures finite operational-control receipt storage.</summary>
public sealed record GovernedLoopOperationalControlReceiptStoreOptions
{
    /// <summary>Gets or initializes the maximum retained receipt count.</summary>
    public int MaxReceipts { get; init; } = 4_096;

    /// <summary>Gets or initializes the maximum canonical UTF-8 bytes per receipt.</summary>
    public int MaxReceiptUtf8Bytes { get; init; } = 128 * 1024;
}
