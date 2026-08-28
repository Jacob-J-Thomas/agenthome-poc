using EmbodySense.Core.Common.Loops.HumanInput.Policies;

namespace EmbodySense.Core.Persistence.HumanInput.Policies.Models;

/// <summary>Captures the exact immutable policy identity committed into one schema-1 catalog generation.</summary>
internal sealed record HumanInputPolicyFileStoreCatalogEntry(HumanInputPolicyReference Reference, string ContentHash);
