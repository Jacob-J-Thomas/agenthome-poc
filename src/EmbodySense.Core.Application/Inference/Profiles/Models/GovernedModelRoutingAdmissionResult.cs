using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission.Models;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Returns a structured routing-admission outcome and immutable snapshot when admitted.</summary>
/// <param name="Status">The structured status.</param>
/// <param name="Snapshot">The exact admitted snapshot when available.</param>
/// <param name="DenialProof">The exact structured proof only when current evidence definitively proves ineligibility.</param>
public sealed record GovernedModelRoutingAdmissionResult(GovernedModelRoutingAdmissionStatus Status, GovernedModelRoutingAdmissionSnapshot? Snapshot, GovernedLoopAdmissionModelRoutingDenialProof? DenialProof = null);
