namespace EmbodySense.Core.Common.Governance.Tools.Models;

/// <summary>
/// Represents an EmbodySense trusted instruction.
/// </summary>
/// <param name="SourceId">The stable source identifier.</param>
/// <param name="Content">The exact content.</param>
public sealed record EmbodySenseTrustedInstruction(
    string SourceId,
    string Content);
