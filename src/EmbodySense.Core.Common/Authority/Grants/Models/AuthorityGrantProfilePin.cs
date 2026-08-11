using EmbodySense.Core.Common.Authority.Models;

namespace EmbodySense.Core.Common.Authority.Grants.Models;

/// <summary>Binds a grant to one exact immutable authority-profile revision and canonical content hash.</summary>
/// <param name="Reference">The stable profile and exact revision.</param>
/// <param name="ContentHash">The canonical hash of that exact profile revision.</param>
public sealed record AuthorityGrantProfilePin(AuthorityProfileReference Reference, AuthorityProfileHash ContentHash);
