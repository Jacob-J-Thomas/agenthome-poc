using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities.Models;

internal sealed record CapabilityDependentDocument(CapabilityDependentKind Kind, string Identity, string Revision, CapabilityDependencyManifest Manifest, CapabilityAuthorityPosture AuthorityPosture);
