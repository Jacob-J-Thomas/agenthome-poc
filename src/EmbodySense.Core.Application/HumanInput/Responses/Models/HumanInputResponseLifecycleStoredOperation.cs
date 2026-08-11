using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Associates workspace-global response operation evidence with its exact request identity.</summary>
/// <param name="RequestId">The stable target request lifecycle.</param>
/// <param name="Evidence">The immutable terminal response operation evidence.</param>
public sealed record HumanInputResponseLifecycleStoredOperation(string RequestId, HumanInputResponseOperationEvidence Evidence);
