using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>
/// Pins the reviewed capability descriptor and provider implementation used by a trigger adapter.
/// </summary>
/// <param name="Capability">The exact capability identity, version, and descriptor hash.</param>
/// <param name="Implementation">The exact provider and implementation path.</param>
public sealed record TriggerAdapterReference(CapabilityDescriptorIdentity Capability, CapabilityImplementationIdentity Implementation);
