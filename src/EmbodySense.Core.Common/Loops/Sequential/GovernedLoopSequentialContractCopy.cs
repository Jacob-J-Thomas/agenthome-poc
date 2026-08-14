using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Common.Loops.Sequential;

internal static class GovernedLoopSequentialContractCopy
{
    internal static CustomLoopModelSnapshot Copy(CustomLoopModelSnapshot? value)
        => value is null ? null! : new CustomLoopModelSnapshot(value.Provider, value.Model);

    internal static CustomLoopConversationReference? Copy(CustomLoopConversationReference? value)
        => value is null ? null : new CustomLoopConversationReference(value.ConversationId, value.CapturedVersion, value.CapturedAtUtc);

    internal static IReadOnlyList<CustomLoopContextManifestSource> Copy(IReadOnlyList<CustomLoopContextManifestSource>? values)
    {
        if (values is null)
        {
            return null!;
        }

        var snapshot = values.Take(GovernedLoopSequentialContractLimits.MaxContextSources + 1)
            .Select(Copy)
            .ToArray();
        return Array.AsReadOnly(snapshot);
    }

    internal static GovernedLoopExecutionBinding Copy(GovernedLoopExecutionBinding? value)
        => value is null
            ? null!
            : GovernedLoopExecutionBinding.Create(value.SchemaVersion, value.RunId, value.Revision, value.ExecutionGeneration);

    private static CustomLoopContextManifestSource Copy(CustomLoopContextManifestSource? value)
        => value is null
            ? null!
            : new CustomLoopContextManifestSource(
                value.Order,
                value.SourceType,
                value.SourceId,
                value.SourcePath,
                value.Provenance,
                value.TrustClass,
                value.Role,
                value.Content,
                value.ContentHash,
                value.OriginalCharacterCount,
                value.UsedCharacterCount,
                value.Truncated,
                value.TruncationReason,
                value.OmissionReason,
                value.CapturedAtUtc);
}
