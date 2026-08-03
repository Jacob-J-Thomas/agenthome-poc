namespace EmbodySense.Core.Common.Loops.Models.Custom.Retention;

/// <summary>
/// Identifies a bounded custom-loop authoring or lifecycle receipt artifact class.
/// </summary>
public enum CustomLoopReceiptArtifactClass
{
    /// <summary>
    /// No artifact class was supplied.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// A create, update, or delete mutation receipt for a custom-loop definition.
    /// </summary>
    DefinitionMutationReceipt,

    /// <summary>
    /// A durable tombstone proving a custom-loop definition identity was deleted and cannot be reused.
    /// </summary>
    DefinitionTombstone,

    /// <summary>
    /// A pause, resume, or cancel lifecycle-control receipt.
    /// </summary>
    LifecycleControlReceipt
}
