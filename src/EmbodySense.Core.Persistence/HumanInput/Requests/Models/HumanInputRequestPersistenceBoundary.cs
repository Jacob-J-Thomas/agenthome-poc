namespace EmbodySense.Core.Persistence.HumanInput.Requests.Models;

/// <summary>Identifies durable Human Input request-store boundaries exposed for recovery evaluation.</summary>
public enum HumanInputRequestPersistenceBoundary
{
    /// <summary>The server-owned monotonic trust anchor was initialized over the empty schema-1 document.</summary>
    TrustInitialized = 1,
    /// <summary>The last proved document was durably published before the candidate primary.</summary>
    ProofPublished = 2,
    /// <summary>The authenticated direct-successor primary was durably published.</summary>
    PrimaryPublished = 3,
    /// <summary>The server-owned monotonic trust anchor advanced to the published primary.</summary>
    TrustAdvanced = 4
}
