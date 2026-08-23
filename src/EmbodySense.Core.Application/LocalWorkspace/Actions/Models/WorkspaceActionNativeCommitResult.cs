namespace EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

/// <summary>Returns a closed native workspace result without adapter-authored detail.</summary>
/// <param name="Status">Whether dispatch did not start or an outcome was observed.</param>
/// <param name="Outcome">The conclusive value-free outcome only when observed.</param>
public sealed record WorkspaceActionNativeCommitResult(
    WorkspaceActionNativeCommitStatus Status,
    WorkspaceActionNativeOutcome? Outcome);
