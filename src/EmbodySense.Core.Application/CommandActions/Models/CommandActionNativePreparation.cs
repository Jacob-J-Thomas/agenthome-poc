using EmbodySense.Core.Common.CommandActions.Models;

namespace EmbodySense.Core.Application.CommandActions.Models;

/// <summary>Returns exact retained value-free evidence after side-effect-free artifact preparation.</summary>
/// <param name="Evidence">The authenticated preparation evidence.</param>
public sealed record CommandActionNativePreparation(CommandActionPreparationEvidence Evidence);
