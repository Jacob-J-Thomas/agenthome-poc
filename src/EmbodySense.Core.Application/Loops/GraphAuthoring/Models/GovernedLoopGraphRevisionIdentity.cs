using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

/// <summary>Projects immutable revision and content identities without exposing mutable payload bytes.</summary>
public sealed record GovernedLoopGraphRevisionIdentity(
    GovernedLoopRevisionReference Revision,
    string LayoutHash,
    string ArtifactHash);
