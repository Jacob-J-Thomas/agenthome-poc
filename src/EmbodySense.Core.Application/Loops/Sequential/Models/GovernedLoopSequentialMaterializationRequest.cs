using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Requests exact materialization only after a successful canonical admission outcome is committed.</summary>
/// <param name="SchemaVersion">The request schema version, which must be 1.</param>
/// <param name="AdmissionRequest">The exact caller-stable request committed by canonical admission.</param>
/// <param name="AdmissionReceipt">The exact immutable successful admission receipt.</param>
/// <param name="Artifact">The exact immutable graph artifact admitted by the receipt.</param>
/// <param name="Plan">The deterministic plan rebuilt from the artifact.</param>
/// <param name="InvocationSnapshot">The exact immutable pre-admission invocation payload.</param>
/// <param name="AdapterBinding">The server-derived binding over the committed receipt and immutable inputs.</param>
public sealed record GovernedLoopSequentialMaterializationRequest(
    int SchemaVersion,
    GovernedLoopAdmissionRequest AdmissionRequest,
    GovernedLoopAdmissionReceipt AdmissionReceipt,
    GovernedLoopGraphRevisionArtifact Artifact,
    GovernedLoopSequentialPlan Plan,
    GovernedLoopSequentialInvocationSnapshot InvocationSnapshot,
    GovernedLoopSequentialAdapterBinding AdapterBinding)
{
    /// <summary>Gets the only supported experimental request schema version.</summary>
    public const int CurrentSchemaVersion = 1;
}
