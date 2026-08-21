using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.CommandActions.Models;

namespace EmbodySense.Core.Application.CommandActions.Models;

/// <summary>Binds one safe command template to its exact activated immutable artifact manifest.</summary>
/// <param name="Template">The immutable value-free process template.</param>
/// <param name="Manifest">The exact artifact manifest resolved only by server composition.</param>
public sealed record CommandActionRegistration(CommandActionTemplate Template, CapabilityArtifactManifest Manifest);
