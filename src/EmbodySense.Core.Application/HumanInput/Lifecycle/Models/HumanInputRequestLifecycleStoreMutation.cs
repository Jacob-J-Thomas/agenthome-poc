using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Requests one atomic append-only Human Input request lifecycle commit.</summary>
/// <param name="ExpectedStoreGeneration">The exact workspace-global generation observed before durable intent.</param>
/// <param name="Operation">The exact terminal lifecycle operation evidence.</param>
/// <param name="RequestToAppend">The immutable request version to append, when this operation creates one.</param>
/// <param name="PrimaryHeadToWrite">The exact target lifecycle head to write.</param>
/// <param name="SecondaryHeadToWrite">The exact related lifecycle head written atomically only for supersede.</param>
public sealed record HumanInputRequestLifecycleStoreMutation(
    long ExpectedStoreGeneration,
    HumanInputRequestLifecycleOperationEvidence Operation,
    HumanInputRequest? RequestToAppend,
    HumanInputRequestLifecycleHead? PrimaryHeadToWrite,
    HumanInputRequestLifecycleHead? SecondaryHeadToWrite);
