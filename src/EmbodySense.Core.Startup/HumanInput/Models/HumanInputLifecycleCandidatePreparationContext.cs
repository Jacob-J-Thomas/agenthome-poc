using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput.Models;

/// <summary>Retains one validated canonical pending request and its server-owned grant for candidate preparation.</summary>
internal sealed record HumanInputLifecycleCandidatePreparationContext(
    HumanInputRequest Request,
    HumanInputRequestReference ExpectedRequest,
    HumanInputRequestLifecycleHead Head,
    AuthorityGrantReference GrantReference);
