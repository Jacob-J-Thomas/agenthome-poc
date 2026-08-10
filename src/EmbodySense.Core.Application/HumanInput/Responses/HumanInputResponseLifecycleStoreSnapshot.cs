using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseLifecycleStoreSnapshot
{
    /// <summary>Creates a defensive immutable response-store snapshot.</summary>
    /// <param name="request">The exact current request lifecycle snapshot.</param>
    /// <param name="responses">The retained valid response artifacts for the current request version.</param>
    /// <param name="operations">The retained response-operation evidence for the current request version.</param>
    /// <param name="selection">The retained selection artifact when answered.</param>
    public HumanInputResponseLifecycleStoreSnapshot(
        HumanInputRequestLifecycleStoreSnapshot request,
        IReadOnlyList<HumanInputResponseArtifact> responses,
        IReadOnlyList<HumanInputResponseOperationEvidence> operations,
        HumanInputResponseSelection? selection)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Responses = Array.AsReadOnly(responses?.ToArray() ?? throw new ArgumentNullException(nameof(responses)));
        Operations = Array.AsReadOnly(operations?.ToArray() ?? throw new ArgumentNullException(nameof(operations)));
        Selection = selection;
    }

    /// <inheritdoc />
    public override string ToString()
        => $"HumanInputResponseLifecycleStoreSnapshot {{ RequestId = {Request.Head.RequestId}, ResponseCount = {Responses.Count}, OperationCount = {Operations.Count}, HasSelection = {Selection is not null} }}";
}
