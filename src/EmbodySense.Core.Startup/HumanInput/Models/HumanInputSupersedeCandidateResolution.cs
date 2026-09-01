using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Returns one registry-bound candidate and its exact grant to the canonical lifecycle facade.</summary>
/// <param name="CandidateRequest">The detached validated successor request.</param>
/// <param name="GrantReference">The exact grant captured from canonical lifecycle evidence.</param>
public sealed record HumanInputSupersedeCandidateResolution(HumanInputRequest CandidateRequest, AuthorityGrantReference GrantReference);
