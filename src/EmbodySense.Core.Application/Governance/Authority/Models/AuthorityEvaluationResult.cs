using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Models;

/// <summary>
/// Represents the surface-independent outcome of an authority-profile evaluation.
/// </summary>
/// <param name="Intersection">The non-executing candidate/effective ceiling intersection.</param>
/// <param name="Projection">The boundary projection for a surface or audit consumer.</param>
public sealed record AuthorityEvaluationResult(AuthorityIntersectionResult Intersection, AuthorityBoundaryProjection Projection);
