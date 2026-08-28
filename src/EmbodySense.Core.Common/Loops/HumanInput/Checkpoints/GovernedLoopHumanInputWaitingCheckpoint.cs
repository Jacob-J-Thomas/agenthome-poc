using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;

/// <summary>Defines one immutable, restart-stable, data-only Human Input waiting checkpoint and its closed append-only evidence history.</summary>
/// <remarks>This contract never claims a response, schedules a notification, resumes execution, grants authority, or performs an effect.</remarks>
/// <param name="SchemaVersion">The checkpoint schema version, which must be 1.</param>
/// <param name="Binding">The exact graph, run, frontier activation, node visit, and generation binding.</param>
/// <param name="NodeConfiguration">The exact canonical Human Input node configuration captured for the checkpoint.</param>
/// <param name="ResolvedPolicy">The exact trusted-time timeout and terminal-disposition policy snapshot captured for the request.</param>
/// <param name="Request">The exact immutable request carrying the response schema, recipients, privacy, and response window.</param>
/// <param name="Posture">The current closed waiting-checkpoint posture.</param>
/// <param name="Evidence">The bounded append-only evidence history.</param>
/// <param name="CheckpointHash">The canonical hash over every behavior-affecting checkpoint field.</param>
public sealed record GovernedLoopHumanInputWaitingCheckpoint(
    int SchemaVersion,
    GovernedLoopHumanInputWaitingCheckpointBinding Binding,
    GovernedLoopHumanInputNodeConfiguration NodeConfiguration,
    HumanInputPolicyResolutionSnapshot ResolvedPolicy,
    HumanInputRequest Request,
    GovernedLoopHumanInputWaitingCheckpointPosture Posture,
    ImmutableArray<GovernedLoopHumanInputWaitingCheckpointEvidence> Evidence,
    string CheckpointHash)
{
    private readonly GovernedLoopHumanInputWaitingCheckpointBinding _binding = GovernedLoopHumanInputWaitingCheckpointContractCopy.Copy(Binding);
    private readonly GovernedLoopHumanInputNodeConfiguration _nodeConfiguration = GovernedLoopHumanInputWaitingCheckpointContractCopy.Copy(NodeConfiguration);
    private readonly HumanInputPolicyResolutionSnapshot _resolvedPolicy = GovernedLoopHumanInputWaitingCheckpointContractCopy.Copy(ResolvedPolicy);
    private readonly HumanInputRequest _request = GovernedLoopHumanInputWaitingCheckpointContractCopy.Copy(Request);
    private readonly ImmutableArray<GovernedLoopHumanInputWaitingCheckpointEvidence> _evidence = GovernedLoopHumanInputWaitingCheckpointContractCopy.Copy(Evidence);

    /// <summary>Gets the only supported Human Input waiting-checkpoint schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopHumanInputWaitingCheckpointContractLimits.CurrentSchemaVersion;

    /// <summary>Gets an independent exact binding snapshot.</summary>
    public GovernedLoopHumanInputWaitingCheckpointBinding Binding => GovernedLoopHumanInputWaitingCheckpointContractCopy.Copy(_binding);

    /// <summary>Gets an independent exact node-configuration snapshot.</summary>
    public GovernedLoopHumanInputNodeConfiguration NodeConfiguration => GovernedLoopHumanInputWaitingCheckpointContractCopy.Copy(_nodeConfiguration);

    /// <summary>Gets an independent exact trusted-time policy-resolution snapshot.</summary>
    public HumanInputPolicyResolutionSnapshot ResolvedPolicy => GovernedLoopHumanInputWaitingCheckpointContractCopy.Copy(_resolvedPolicy);

    /// <summary>Gets an independent exact immutable request snapshot.</summary>
    public HumanInputRequest Request => GovernedLoopHumanInputWaitingCheckpointContractCopy.Copy(_request);

    /// <summary>Gets an independent append-only evidence snapshot.</summary>
    public ImmutableArray<GovernedLoopHumanInputWaitingCheckpointEvidence> Evidence => GovernedLoopHumanInputWaitingCheckpointContractCopy.Copy(_evidence);
}
