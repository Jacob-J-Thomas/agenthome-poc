using System.Collections.Immutable;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.HumanInput.Responses.Models;

/// <summary>Retains one immutable deterministic selection of exact response references; it never synthesizes or reinterprets response content.</summary>
/// <param name="SchemaVersion">The selection schema version.</param>
/// <param name="SelectionId">The stable selection identifier.</param>
/// <param name="Request">The exact immutable request version answered by this selection.</param>
/// <param name="PolicyKind">The authored policy that proved this selection.</param>
/// <param name="Responses">The selected exact response references in deterministic policy order.</param>
/// <param name="SelectorActorId">The authenticated selector attribution required only for manual selection.</param>
/// <param name="SelectorRoleId">The authenticated selector role required only for manual selection.</param>
/// <param name="SelectedAtUtc">The trusted UTC selection time.</param>
/// <param name="SelectionHash">The canonical selection digest.</param>
public sealed partial record HumanInputResponseSelection(
    int SchemaVersion,
    string SelectionId,
    HumanInputRequestReference Request,
    HumanInputResponsePolicyKind PolicyKind,
    ImmutableArray<HumanInputResponseReference> Responses,
    AuthorityActorId? SelectorActorId,
    string? SelectorRoleId,
    DateTimeOffset SelectedAtUtc,
    string SelectionHash)
{
    /// <summary>The only supported selection schema version.</summary>
    public const int CurrentSchemaVersion = HumanInputResponseContractLimits.CurrentSchemaVersion;
}
