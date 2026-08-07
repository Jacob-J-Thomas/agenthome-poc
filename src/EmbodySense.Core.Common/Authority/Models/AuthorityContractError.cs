namespace EmbodySense.Core.Common.Authority.Models;

/// <summary>
/// Describes one structured, value-free authority-contract rejection.
/// </summary>
/// <param name="Code">The stable rejection code.</param>
/// <param name="Field">The closed field location associated with the rejection.</param>
public sealed record AuthorityContractError(AuthorityContractErrorCode Code, AuthorityContractField Field);
