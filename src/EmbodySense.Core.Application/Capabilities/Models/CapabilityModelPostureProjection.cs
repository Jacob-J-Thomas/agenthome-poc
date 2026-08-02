namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Projects only one exact admitted and currently authorized capability into model context.</summary>
/// <param name="Id">The exact assigned capability identity.</param>
/// <param name="Version">The exact admitted version.</param>
/// <param name="Kind">The closed capability kind.</param>
/// <param name="Description">The bounded admitted public description.</param>
public sealed record CapabilityModelPostureProjection(string Id, string Version, string Kind, string Description);
