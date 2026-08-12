using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Requests canonical admission and ordered execution from exact pre-captured immutable inputs.</summary>
/// <param name="SchemaVersion">The request schema version, which must be 1.</param>
/// <param name="AdmissionRequest">The exact caller-stable canonical admission request.</param>
/// <param name="Artifact">The exact immutable published graph artifact.</param>
/// <param name="Plan">The deterministic plan rebuilt from that artifact.</param>
/// <param name="InvocationSnapshot">The exact bounded invocation payload captured before admission.</param>
/// <remarks>The server-owned run identity and adapter binding are intentionally absent and derive only from a committed admission receipt.</remarks>
public sealed record GovernedLoopSequentialInvocationRequest(
    int SchemaVersion,
    GovernedLoopAdmissionRequest AdmissionRequest,
    GovernedLoopGraphRevisionArtifact Artifact,
    GovernedLoopSequentialPlan Plan,
    GovernedLoopSequentialInvocationSnapshot InvocationSnapshot)
{
    /// <summary>Gets the only supported experimental request schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
