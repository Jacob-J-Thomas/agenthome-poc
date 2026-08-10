using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.HumanInput.Responses;

internal sealed class ChronologyContext
{
    internal ChronologyContext(HumanInputResponseLifecycleStoreSnapshot snapshot)
    {
        Snapshot = snapshot;
        OperationIndexes = snapshot.Operations
            .Select((operation, index) => (operation.OperationId, Index: index))
            .ToDictionary(entry => entry.OperationId, entry => entry.Index, StringComparer.Ordinal);
        ResponsesById = snapshot.Responses.ToDictionary(response => response.ResponseId, StringComparer.Ordinal);
    }

    internal HumanInputResponseLifecycleStoreSnapshot Snapshot { get; }

    internal IReadOnlyDictionary<string, int> OperationIndexes { get; }

    internal IReadOnlyDictionary<string, HumanInputResponseArtifact> ResponsesById { get; }

    internal List<HumanInputResponseArtifact> RetainedResponses { get; } = [];

    internal List<HumanInputResponseArtifact> ActiveResponses { get; } = [];

    internal int NextOperationIndex { get; set; }
}
