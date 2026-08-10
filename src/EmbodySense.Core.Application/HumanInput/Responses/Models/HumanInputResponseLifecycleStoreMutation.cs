using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Requests one atomic append-only Human Input response commit.</summary>
/// <param name="ExpectedStoreGeneration">The exact workspace-global generation observed before durable intent.</param>
/// <param name="Operation">The exact terminal response operation evidence.</param>
/// <param name="ResponseToAppend">The immutable valid response appended by a committed Submit operation.</param>
/// <param name="SelectionToAppend">The immutable selection appended only when the operation answers the request.</param>
/// <param name="RequestHeadToWrite">The exact Answered request head written atomically with <paramref name="SelectionToAppend"/>.</param>
public sealed partial record HumanInputResponseLifecycleStoreMutation(
    long ExpectedStoreGeneration,
    HumanInputResponseOperationEvidence Operation,
    HumanInputResponseArtifact? ResponseToAppend,
    HumanInputResponseSelection? SelectionToAppend,
    HumanInputRequestLifecycleHead? RequestHeadToWrite);
