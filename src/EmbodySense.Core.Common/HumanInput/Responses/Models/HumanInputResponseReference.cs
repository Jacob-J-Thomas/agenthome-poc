using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

/// <summary>Identifies one exact immutable response without exposing private response content, explanation, or actor attribution.</summary>
/// <param name="SchemaVersion">The reference schema version.</param>
/// <param name="ResponseId">The stable response identifier.</param>
/// <param name="Request">The exact immutable request version.</param>
/// <param name="ValueHash">The canonical response-value digest used by deterministic policy.</param>
/// <param name="ResponseHash">The canonical full response-artifact digest.</param>
public sealed partial record HumanInputResponseReference(int SchemaVersion, string ResponseId, HumanInputRequestReference Request, string ValueHash, string ResponseHash)
{
    /// <summary>The only supported response-reference schema version.</summary>
    public const int CurrentSchemaVersion = HumanInputResponseContractLimits.CurrentSchemaVersion;
}
