namespace EmbodySense.Core.Startup.Capabilities.Models;

/// <summary>Projects one exact admitted, assigned, and currently authorized capability for model context.</summary>
/// <param name="Id">The exact assigned capability identity.</param>
/// <param name="Version">The exact admitted version.</param>
/// <param name="Kind">The closed capability kind.</param>
/// <param name="Description">The bounded admitted public description.</param>
public sealed record CapabilityModelPostureSnapshot(string Id, string Version, string Kind, string Description);
