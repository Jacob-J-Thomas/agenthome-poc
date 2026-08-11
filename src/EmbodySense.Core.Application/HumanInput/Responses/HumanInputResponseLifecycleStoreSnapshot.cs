using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

public sealed partial record HumanInputResponseLifecycleStoreSnapshot
{
    /// <summary>Creates a defensive immutable response-store snapshot.</summary>
    /// <param name="request">The exact current request lifecycle snapshot.</param>
    /// <param name="responseRequest">The exact retained request version whose response history is projected.</param>
    /// <param name="responses">The retained valid response artifacts for <paramref name="responseRequest"/>.</param>
    /// <param name="operations">The retained response-operation evidence for <paramref name="responseRequest"/>.</param>
    /// <param name="selection">The retained selection artifact when this exact request version answered the lifecycle.</param>
    public HumanInputResponseLifecycleStoreSnapshot(
        HumanInputRequestLifecycleStoreSnapshot request,
        HumanInputRequestReference responseRequest,
        IReadOnlyList<HumanInputResponseArtifact> responses,
        IReadOnlyList<HumanInputResponseOperationEvidence> operations,
        HumanInputResponseSelection? selection)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        ResponseRequest = responseRequest is null
            ? throw new ArgumentNullException(nameof(responseRequest))
            : responseRequest with { };
        Responses = Array.AsReadOnly(responses?.ToArray() ?? throw new ArgumentNullException(nameof(responses)));
        Operations = Array.AsReadOnly(operations?.ToArray() ?? throw new ArgumentNullException(nameof(operations)));
        Selection = selection;
    }

    /// <inheritdoc />
    public override string ToString()
        => $"HumanInputResponseLifecycleStoreSnapshot {{ RequestId = {Request.Head.RequestId}, ResponseVersionId = {ResponseRequest.RequestVersionId}, ResponseCount = {Responses.Count}, OperationCount = {Operations.Count}, HasSelection = {Selection is not null} }}";
}
