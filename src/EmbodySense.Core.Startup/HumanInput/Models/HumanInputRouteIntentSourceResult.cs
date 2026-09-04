using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;

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
    string IntentHash)
{
    /// <summary>Creates one valid result with defensive immutable intent storage.</summary>
    public static HumanInputRouteIntentSourceResult Ready(IReadOnlyList<HumanInputRouteExclusionIntent> intents, string intentHash)
        => new(HumanInputRouteIntentSourceStatus.Ready, HumanInputRouteIntentContract.ContractId, HumanInputRouteIntentContract.Version, Array.AsReadOnly(intents.ToArray()), intentHash);

    /// <summary>Creates a value-free invalid result.</summary>
    public static HumanInputRouteIntentSourceResult Invalid()
        => new(HumanInputRouteIntentSourceStatus.Invalid, HumanInputRouteIntentContract.ContractId, HumanInputRouteIntentContract.Version, [], string.Empty);

    /// <summary>Creates a value-free unavailable result.</summary>
    public static HumanInputRouteIntentSourceResult Unavailable()
        => new(HumanInputRouteIntentSourceStatus.Unavailable, HumanInputRouteIntentContract.ContractId, HumanInputRouteIntentContract.Version, [], string.Empty);

    /// <summary>Creates a value-free ambiguous result.</summary>
    public static HumanInputRouteIntentSourceResult Ambiguous()
        => new(HumanInputRouteIntentSourceStatus.Ambiguous, HumanInputRouteIntentContract.ContractId, HumanInputRouteIntentContract.Version, [], string.Empty);

    /// <summary>Computes the aggregate digest over the exact canonical route and ordered exclusions.</summary>
    public static string ComputeIntentHash(string requestHash, IReadOnlyList<HumanInputRouteExclusionIntent> intents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        ArgumentNullException.ThrowIfNull(intents);
        var material = string.Join("\u001f", new[] { HumanInputRouteIntentContract.ContractId, HumanInputRouteIntentContract.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), requestHash }.Concat(intents.Select(intent => $"{intent.Ordinal}:{intent.RouteEntryHash}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }
}
