using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Describes one exact response operation without caller-owned actor or time authority.</summary>
/// <param name="SchemaVersion">The command schema version.</param>
/// <param name="OperationId">The workspace-global idempotency identity.</param>
/// <param name="Kind">The requested Submit, Withdraw, or Select operation.</param>
/// <param name="RequestId">The stable target request lifecycle.</param>
/// <param name="ExpectedLifecycleVersion">The exact optimistic request lifecycle version.</param>
/// <param name="ExpectedLifecycleStatus">The exact optimistic request lifecycle status.</param>
/// <param name="ExpectedRequest">The exact current immutable request reference.</param>
/// <param name="ExpectedBinding">The exact current request binding.</param>
/// <param name="ResponseId">The proposed immutable response identity for Submit; otherwise null.</param>
/// <param name="Value">The untrusted typed response value for Submit; otherwise null.</param>
/// <param name="Explanation">The optional bounded untrusted explanation for Submit; otherwise null.</param>
/// <param name="TargetResponses">The exact response target for Withdraw or Select; empty for Submit.</param>
/// <param name="CommandHash">The canonical hash of every behavior-affecting command field.</param>
public sealed partial record HumanInputResponseLifecycleCommand(
    int SchemaVersion,
    string OperationId,
    HumanInputResponseOperationKind Kind,
    string RequestId,
    long ExpectedLifecycleVersion,
    HumanInputRequestLifecycleStatus ExpectedLifecycleStatus,
    HumanInputRequestReference ExpectedRequest,
    HumanInputRequestBinding ExpectedBinding,
    string? ResponseId,
    HumanInputResponseValue? Value,
    string? Explanation,
    ImmutableArray<HumanInputResponseReference> TargetResponses,
    string CommandHash)
{
    /// <summary>Gets the only supported response command schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
