namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>Identifies one exact immutable Human Input request version without exposing its private content.</summary>
/// <param name="SchemaVersion">The reference schema version, which must be 1.</param>
/// <param name="RequestId">The stable request identifier.</param>
/// <param name="RequestVersionId">The exact immutable request-version identifier.</param>
/// <param name="RequestHash">The canonical request-content hash.</param>
public sealed partial record HumanInputRequestReference(int SchemaVersion, string RequestId, string RequestVersionId, string RequestHash)
{
    /// <summary>The only supported reference schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
