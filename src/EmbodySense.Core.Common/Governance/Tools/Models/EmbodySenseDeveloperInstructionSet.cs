namespace EmbodySense.Core.Common.Governance.Tools.Models;

/// <summary>
/// Represents an EmbodySense developer instruction set.
/// </summary>
/// <param name="Version">The version.</param>
/// <param name="Content">The exact content.</param>
/// <param name="ContentHash">The lowercase SHA-256 digest of the exact content.</param>
public sealed record EmbodySenseDeveloperInstructionSet(
    string Version,
    string Content,
    string ContentHash);
