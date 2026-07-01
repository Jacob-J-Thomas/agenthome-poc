using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Runtime.Models;

namespace EmbodySense.Core.Application.Runtime.Models;

public sealed record RuntimeVerboseContext
{
    public RuntimeVerboseContext(
        LoopDefinition loopDefinition,
        LoopRunIdentity runIdentity,
        RuntimeSurfaceId surface,
        IReadOnlyList<RuntimeContextMessage> messages,
        IReadOnlyList<RuntimeContextOmission> omissions,
        string compactionStatus)
    {
        ArgumentNullException.ThrowIfNull(loopDefinition);
        ArgumentNullException.ThrowIfNull(runIdentity);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(omissions);
        ArgumentException.ThrowIfNullOrWhiteSpace(compactionStatus);

        LoopDefinition = loopDefinition;
        RunIdentity = runIdentity;
        Surface = surface;
        Messages = messages;
        Omissions = omissions;
        CompactionStatus = compactionStatus;
    }

    public LoopDefinition LoopDefinition { get; }

    public LoopRunIdentity RunIdentity { get; }

    public RuntimeSurfaceId Surface { get; }

    public IReadOnlyList<RuntimeContextMessage> Messages { get; }

    public IReadOnlyList<RuntimeContextOmission> Omissions { get; }

    public string CompactionStatus { get; }
}
