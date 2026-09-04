using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Resolves bounded server-owned reroute intents from one canonical Human Input request.</summary>
/// <remarks>The source receives no actor, grant, operation, browser, or surface input. Its result contains only
/// deterministic exclusion positions and hashes of canonical route entries. Startup validates the result again before
/// creating an opaque candidate, so a malformed, changed, or partial source result cannot publish a registry entry.</remarks>
public interface IHumanInputRouteIntentSource
{
    /// <summary>Reads the deterministic route alternatives for one already validated canonical request.</summary>
    /// <param name="request">The canonical request whose current route is the only source of alternatives.</param>
    /// <param name="cancellationToken">The token used to cancel the bounded source read.</param>
    /// <returns>A typed contract result with no public respondent or route values.</returns>
    Task<HumanInputRouteIntentSourceResult> ResolveAsync(HumanInputRequest request, CancellationToken cancellationToken = default);
}
