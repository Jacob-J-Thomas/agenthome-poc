namespace EmbodySense.Core.Common.Loops.Models;

/// <summary>
/// Defines canonical default conversation loop graph IDs.
/// </summary>
public static class DefaultConversationLoopGraphIds
{
    /// <summary>
    /// Identifies the accept user message graph-node ID.
    /// </summary>
    public const string AcceptUserMessage = "accept-user-message";
    /// <summary>
    /// Identifies the assemble context graph-node ID.
    /// </summary>
    public const string AssembleContext = "assemble-runtime-context";
    /// <summary>
    /// Identifies the dispatch inference graph-node ID.
    /// </summary>
    public const string DispatchInference = "dispatch-provider-inference";
    /// <summary>
    /// Identifies the persist transcript graph-node ID.
    /// </summary>
    public const string PersistTranscript = "persist-transcript";
    /// <summary>
    /// Identifies the complete run graph-node ID.
    /// </summary>
    public const string CompleteRun = "complete-run";
}
