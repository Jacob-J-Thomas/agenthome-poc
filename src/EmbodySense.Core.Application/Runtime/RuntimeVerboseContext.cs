using EmbodySense.Core.Common.Loops;
using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Application.Runtime;

/// <summary>
/// Represents a runtime verbose context.
/// </summary>
public sealed record RuntimeVerboseContext
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeVerboseContext"/> type.
    /// </summary>
    /// <param name="loopDefinition">The loop definition.</param>
    /// <param name="runIdentity">The run identity.</param>
    /// <param name="surface">The surface.</param>
    /// <param name="messages">The messages.</param>
    /// <param name="omissions">The omissions.</param>
    /// <param name="compactionStatus">The compaction status.</param>
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

    /// <summary>
    /// Gets the loop definition.
    /// </summary>
    /// <value>The loop definition.</value>
    public LoopDefinition LoopDefinition { get; }

    /// <summary>
    /// Gets the loop run identity.
    /// </summary>
    /// <value>The loop run identity.</value>
    public LoopRunIdentity RunIdentity { get; }

    /// <summary>
    /// Gets the runtime surface ID.
    /// </summary>
    /// <value>The runtime surface ID.</value>
    public RuntimeSurfaceId Surface { get; }

    /// <summary>
    /// Gets the runtime context messages.
    /// </summary>
    /// <value>The runtime context messages.</value>
    public IReadOnlyList<RuntimeContextMessage> Messages { get; }

    /// <summary>
    /// Gets the runtime context omissions.
    /// </summary>
    /// <value>The runtime context omissions.</value>
    public IReadOnlyList<RuntimeContextOmission> Omissions { get; }

    /// <summary>
    /// Gets the compaction status.
    /// </summary>
    /// <value>The compaction status.</value>
    public string CompactionStatus { get; }
}
