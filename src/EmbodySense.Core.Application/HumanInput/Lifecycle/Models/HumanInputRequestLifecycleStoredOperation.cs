using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Associates workspace-global lifecycle operation evidence with its exact primary request identity.</summary>
/// <param name="RequestId">The exact affected primary request lifecycle.</param>
/// <param name="Evidence">The immutable terminal operation evidence, including any related supersede request.</param>
public sealed record HumanInputRequestLifecycleStoredOperation(string RequestId, HumanInputRequestLifecycleOperationEvidence Evidence);
