using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Common.Loops.Sequential.Models;

/// <summary>Captures the exact bounded non-secret invocation payload admitted to one sequential governed-loop run.</summary>
/// <param name="SchemaVersion">The snapshot schema version, which must be 1.</param>
/// <param name="TriggerPrompt">The exact normalized manual-trigger prompt.</param>
/// <param name="ModelSnapshot">The exact provider and optional model selection.</param>
/// <param name="InvokingConversation">The optional exact invoking-conversation version.</param>
/// <param name="ContextCapturedAtUtc">The trusted UTC time at which the context manifest was frozen.</param>
/// <param name="ContextManifest">The bounded ordered context sources, including explicit omissions and provenance.</param>
/// <param name="ContentHash">The canonical hash over every preceding field.</param>
/// <remarks>The snapshot contains no credentials or secret values and is immutable invocation evidence, not authority.</remarks>
public sealed record GovernedLoopSequentialInvocationSnapshot(
    int SchemaVersion,
    string TriggerPrompt,
    CustomLoopModelSnapshot ModelSnapshot,
    CustomLoopConversationReference? InvokingConversation,
    DateTimeOffset ContextCapturedAtUtc,
    IReadOnlyList<CustomLoopContextManifestSource> ContextManifest,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental snapshot schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopSequentialContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensively copied immutable model selection.</summary>
    public CustomLoopModelSnapshot ModelSnapshot { get; } = GovernedLoopSequentialContractCopy.Copy(ModelSnapshot);

    /// <summary>Gets a defensively copied immutable invoking-conversation reference.</summary>
    public CustomLoopConversationReference? InvokingConversation { get; } = GovernedLoopSequentialContractCopy.Copy(InvokingConversation);

    /// <summary>Gets a defensively copied bounded read-only context manifest.</summary>
    public IReadOnlyList<CustomLoopContextManifestSource> ContextManifest { get; } = GovernedLoopSequentialContractCopy.Copy(ContextManifest);
}
