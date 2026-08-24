namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Declares one fixed non-secret environment value owned by server registration.</summary>
/// <param name="Name">The exact portable environment-variable name.</param>
/// <param name="Value">The bounded non-secret value.</param>
public sealed record CommandActionEnvironmentEntry(string Name, string Value);
