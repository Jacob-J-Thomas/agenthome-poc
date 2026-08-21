namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Declares one complete argument token fixed by the server or supplied by one typed slot.</summary>
/// <param name="Kind">How the token is supplied.</param>
/// <param name="Value">The exact fixed token or canonical slot name.</param>
public sealed record CommandActionArgumentPart(CommandActionArgumentPartKind Kind, string Value);
