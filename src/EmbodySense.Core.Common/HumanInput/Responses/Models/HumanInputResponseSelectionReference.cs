using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

/// <summary>Identifies one exact immutable selected response set without exposing private response content or actor attribution.</summary>
/// <param name="SchemaVersion">The reference schema version.</param>
/// <param name="SelectionId">The stable selection identifier.</param>
/// <param name="Request">The exact immutable request version answered by the selection.</param>
/// <param name="SelectionHash">The canonical full selection digest.</param>
public sealed partial record HumanInputResponseSelectionReference(int SchemaVersion, string SelectionId, HumanInputRequestReference Request, string SelectionHash)
{
    /// <summary>The only supported selection-reference schema version.</summary>
    public const int CurrentSchemaVersion = HumanInputResponseContractLimits.CurrentSchemaVersion;
}
