namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>Carries one browser-safe exact authority-grant reference using only closed primitive fields.</summary>
/// <param name="GrantId">The stable grant identifier.</param>
/// <param name="Revision">The exact positive grant revision.</param>
/// <param name="ContentHash">The exact immutable grant content hash.</param>
public sealed record GovernedLoopAuthorityGrantInput(string GrantId, int Revision, string ContentHash);
