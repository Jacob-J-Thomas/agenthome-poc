using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

/// <summary>Describes one atomic generic lifecycle and canonical graph-payload commit.</summary>
public sealed record GovernedLoopGraphRevisionStoreMutation(
    GovernedLoopRevisionStoreMutation LifecycleMutation,
    GovernedLoopGraphDefinition? GraphToAppend,
    string AuthoringRequestHash,
    string? GraphValidationEvidenceHash);
