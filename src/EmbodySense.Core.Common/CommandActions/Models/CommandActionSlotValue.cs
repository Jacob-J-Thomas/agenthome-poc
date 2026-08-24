namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Supplies one typed value for a server-declared command slot.</summary>
/// <param name="Name">The exact slot name.</param>
/// <param name="Kind">The exact declared slot kind.</param>
/// <param name="Value">The bounded canonical scalar or JSON value.</param>
public sealed record CommandActionSlotValue(string Name, CommandActionSlotKind Kind, string Value);
