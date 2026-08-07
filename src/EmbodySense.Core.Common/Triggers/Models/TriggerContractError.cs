namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>
/// Describes one stable trigger-contract rejection without retaining hostile input.
/// </summary>
/// <param name="Code">The stable machine-readable reason.</param>
/// <param name="Field">The canonical field path.</param>
public sealed record TriggerContractError(string Code, string Field);
