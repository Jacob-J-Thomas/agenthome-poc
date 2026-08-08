namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>References one exact graph element without depending on persistence or presentation layout.</summary>
/// <param name="Kind">The element kind.</param>
/// <param name="Id">The stable element identity when one was supplied, otherwise <see langword="null"/>.</param>
/// <param name="Path">The bounded schema-relative path used to disambiguate local elements and fields.</param>
public sealed record GovernedLoopGraphElementReference(GovernedLoopGraphElementKind Kind, string? Id, string Path);
