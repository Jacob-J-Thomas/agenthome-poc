using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Creates and hashes bounded route-intent source results.</summary>
/// <remarks>The result record remains a behaviorless detached model. This factory owns defensive copies and the
/// schema-versioned aggregate digest used to bind an exclusion set to one canonical request route.</remarks>
public static class HumanInputRouteIntentSourceResultFactory
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
