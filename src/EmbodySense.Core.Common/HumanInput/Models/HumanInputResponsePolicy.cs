namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Defines the explicit selection policy for response data; it grants no authority and performs no delivery or persistence.
/// </summary>
/// <param name="Kind">The supported response-selection policy.</param>
public sealed record HumanInputResponsePolicy(HumanInputResponsePolicyKind Kind);
