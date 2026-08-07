namespace EmbodySense.Core.Common.HumanInput.Models;

/// <summary>
/// Returns a typed pure-validation outcome for submitted untrusted response data.
/// </summary>
/// <param name="Kind">Whether the response is structurally valid.</param>
/// <param name="Response">The validated untrusted response when <paramref name="Kind"/> is <see cref="HumanInputResponseOutcomeKind.Valid"/>.</param>
/// <param name="Errors">The deterministic validation errors when <paramref name="Kind"/> is <see cref="HumanInputResponseOutcomeKind.Invalid"/>.</param>
public sealed record HumanInputResponseOutcome(HumanInputResponseOutcomeKind Kind, HumanInputResponse? Response, IReadOnlyList<HumanInputValidationError> Errors);
