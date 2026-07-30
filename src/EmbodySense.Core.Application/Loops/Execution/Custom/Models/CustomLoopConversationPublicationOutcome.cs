namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Identifies the supported custom loop conversation publication outcome values.
/// </summary>
public enum CustomLoopConversationPublicationOutcome
{
    /// <summary>
    /// Identifies the published custom loop conversation publication outcome.
    /// </summary>
    Published = 1,
    /// <summary>
    /// Identifies the already published custom loop conversation publication outcome.
    /// </summary>
    AlreadyPublished = 2,
    /// <summary>
    /// Identifies the definitely failed custom loop conversation publication outcome.
    /// </summary>
    DefinitelyFailed = 3,
    /// <summary>
    /// Identifies the uncertain custom loop conversation publication outcome.
    /// </summary>
    Uncertain = 4
}
