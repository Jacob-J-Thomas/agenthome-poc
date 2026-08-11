using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Requests one authenticated optimistic Human Input request lifecycle operation without accepting caller-owned actor, time, or workspace authority.</summary>
/// <param name="SchemaVersion">The command schema version, which must be 1.</param>
/// <param name="OperationId">The workspace-global idempotency identity.</param>
/// <param name="Kind">The requested lifecycle operation.</param>
/// <param name="RequestId">The stable target request identity.</param>
/// <param name="ExpectedLifecycleVersion">The exact current lifecycle version, or zero for creation.</param>
/// <param name="ExpectedLifecycleStatus">The exact current posture, or unknown for creation.</param>
/// <param name="ExpectedRequest">The exact current immutable request reference, or null for creation.</param>
/// <param name="ExpectedBinding">The exact current request binding, or null for creation.</param>
/// <param name="CandidateRequest">The immutable request candidate required by create, reroute, amend, and supersede.</param>
/// <param name="GrantReference">The exact active authority grant required by delivery-producing operations.</param>
/// <param name="Reason">The bounded non-secret lifecycle reason.</param>
/// <param name="RequestHash">The canonical exact-intent hash.</param>
public sealed partial record HumanInputRequestLifecycleCommand(
    int SchemaVersion,
    string OperationId,
    HumanInputRequestLifecycleOperationKind Kind,
    string RequestId,
    long ExpectedLifecycleVersion,
    HumanInputRequestLifecycleStatus ExpectedLifecycleStatus,
    HumanInputRequestReference? ExpectedRequest,
    HumanInputRequestBinding? ExpectedBinding,
    HumanInputRequest? CandidateRequest,
    AuthorityGrantReference? GrantReference,
    AuthorityPurpose Reason,
    string RequestHash)
{
    /// <summary>Gets the only supported command schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
