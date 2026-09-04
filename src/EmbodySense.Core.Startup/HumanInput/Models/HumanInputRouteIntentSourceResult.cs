namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Returns a bounded, value-free route-intent source result.</summary>
/// <param name="Status">The typed source disposition.</param>
/// <param name="ContractId">The stable versioned route-intent contract identifier.</param>
/// <param name="ContractVersion">The supported route-intent contract version.</param>
/// <param name="Intents">The ordered internal exclusions, never respondent or route values.</param>
/// <param name="IntentHash">The aggregate hash bound to the exact canonical request route.</param>
public sealed record HumanInputRouteIntentSourceResult(
    HumanInputRouteIntentSourceStatus Status,
    string ContractId,
    int ContractVersion,
    IReadOnlyList<HumanInputRouteExclusionIntent> Intents,
    string IntentHash);
