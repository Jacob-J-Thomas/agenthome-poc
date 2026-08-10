using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring.Models;

/// <summary>Returns one exact immutable graph payload without current-revision selection.</summary>
public sealed record GovernedLoopGraphRevisionArtifactReadResult(
    GovernedLoopRevisionStoreReadStatus Status,
    long StoreGeneration,
    GovernedLoopGraphRevisionArtifact? Artifact);
