using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Snapshots one Human Input request's complete bounded immutable lifecycle.</summary>
/// <param name="Head">The exact current lifecycle head.</param>
/// <param name="RequestVersions">All retained immutable request versions in append order.</param>
/// <param name="Operations">All retained lifecycle operations for this request in durable order.</param>
/// <param name="AnswerOperation">The exact privacy-safe response operation that atomically answered this request, when answered.</param>
public sealed record HumanInputRequestLifecycleStoreSnapshot(
    HumanInputRequestLifecycleHead Head,
    IReadOnlyList<HumanInputRequest> RequestVersions,
    IReadOnlyList<HumanInputRequestLifecycleOperationEvidence> Operations,
    HumanInputResponseOperationEvidence? AnswerOperation = null)
{
    /// <summary>Gets a defensive immutable copy of retained request versions.</summary>
    public IReadOnlyList<HumanInputRequest> RequestVersions { get; } = RequestVersions is null ? null! : Array.AsReadOnly(RequestVersions.ToArray());

    /// <summary>Gets a defensive immutable copy of retained operations.</summary>
    public IReadOnlyList<HumanInputRequestLifecycleOperationEvidence> Operations { get; } = Operations is null ? null! : Array.AsReadOnly(Operations.ToArray());
}
