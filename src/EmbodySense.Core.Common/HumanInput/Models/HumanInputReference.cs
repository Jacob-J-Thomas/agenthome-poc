namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Contains a bounded opaque reference submitted as untrusted data; it cannot carry a path, URL, secret, or authority claim.
/// </summary>
/// <param name="Kind">The declared safe reference kind.</param>
/// <param name="Value">The bounded canonical opaque reference identifier.</param>
public sealed record HumanInputReference(HumanInputReferenceKind Kind, string Value);
