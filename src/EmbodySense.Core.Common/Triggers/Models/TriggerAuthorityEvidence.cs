using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>
/// Captures one exact authority profile revision and its non-executing boundary receipt.
/// </summary>
/// <remarks>A direct receipt remains evidence only; this type never grants or executes an effect.</remarks>
/// <param name="Profile">The exact authority profile reference.</param>
/// <param name="BoundaryReceipt">The exact bounded boundary evidence.</param>
public sealed record TriggerAuthorityEvidence(AuthorityProfileReference Profile, AuthorityBoundaryReceipt BoundaryReceipt);
