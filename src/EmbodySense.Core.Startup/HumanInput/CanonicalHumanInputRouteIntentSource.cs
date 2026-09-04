using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Produces deterministic exclusion intents from the current canonical Human Input route.</summary>
/// <remarks>Each intent names a canonical array position and a digest of that complete route entry. The source does not
/// choose a new destination, authenticate a respondent, or receive operation/grant/browser authority.</remarks>
public sealed class CanonicalHumanInputRouteIntentSource : IHumanInputRouteIntentSource
{
    /// <inheritdoc />
    public Task<HumanInputRouteIntentSourceResult> ResolveAsync(HumanInputRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!HumanInputValidator.ValidateRequest(request).IsValid || request.EligibleRespondents is not { Length: > 0 and <= HumanInputLimits.MaxEligibleRespondents })
            {
                return Task.FromResult(HumanInputRouteIntentSourceResult.Invalid());
            }

            var intents = request.EligibleRespondents
                .Select((respondent, index) => new HumanInputRouteExclusionIntent(index, RouteEntryHash(respondent)))
                .ToArray();
            var intentHash = HumanInputRouteIntentSourceResult.ComputeIntentHash(request.RequestHash, intents);
            return Task.FromResult(HumanInputRouteIntentSourceResult.Ready(intents, intentHash));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(HumanInputRouteIntentSourceResult.Invalid());
        }
        catch (Exception)
        {
            return Task.FromResult(HumanInputRouteIntentSourceResult.Unavailable());
        }
    }

    internal static string RouteEntryHash(HumanInputEligibleRespondent respondent)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", respondent.RespondentId, respondent.RespondentRoleId, respondent.RoutingReference)))).ToLowerInvariant();
}
