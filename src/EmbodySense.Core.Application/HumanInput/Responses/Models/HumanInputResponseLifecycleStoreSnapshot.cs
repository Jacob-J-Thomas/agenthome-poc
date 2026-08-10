using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Snapshots one exact request version and its complete bounded response history.</summary>
public sealed partial record HumanInputResponseLifecycleStoreSnapshot
{
    /// <summary>Gets the exact current request lifecycle and retained immutable request versions.</summary>
    public HumanInputRequestLifecycleStoreSnapshot Request { get; }

    /// <summary>Gets the exact retained immutable request version whose response history is projected.</summary>
    public HumanInputRequestReference ResponseRequest { get; }

    /// <summary>Gets defensive immutable copies of retained valid response artifacts for the exact current request version.</summary>
    public IReadOnlyList<HumanInputResponseArtifact> Responses { get; }

    /// <summary>Gets defensive immutable copies of retained response-operation evidence for the exact current request version.</summary>
    public IReadOnlyList<HumanInputResponseOperationEvidence> Operations { get; }

    /// <summary>Gets the immutable selected-response artifact when the request is answered.</summary>
    public HumanInputResponseSelection? Selection { get; }
}
