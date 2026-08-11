namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Classifies one exact run-anchor guard decision.</summary>
public enum GovernedLoopSequentialRunAnchorStatus
{
    /// <summary>No supported decision was produced.</summary>
    Unknown = 0,
    /// <summary>Every exact coordinate composes and a guarded anchor was issued.</summary>
    Ready,
    /// <summary>The adapter binding is invalid.</summary>
    InvalidAdapterBinding,
    /// <summary>The admission request is invalid.</summary>
    InvalidAdmissionRequest,
    /// <summary>The admission receipt is invalid.</summary>
    InvalidAdmissionReceipt,
    /// <summary>The invocation snapshot is invalid.</summary>
    InvalidInvocationSnapshot,
    /// <summary>The graph artifact is invalid.</summary>
    InvalidGraphArtifact,
    /// <summary>The workspace coordinate was substituted.</summary>
    WorkspaceMismatch,
    /// <summary>The admission operation coordinate was substituted.</summary>
    OperationMismatch,
    /// <summary>The admission request coordinate was substituted.</summary>
    RequestMismatch,
    /// <summary>The admission receipt coordinate was substituted.</summary>
    ReceiptMismatch,
    /// <summary>The invocation payload coordinate was substituted.</summary>
    InvocationMismatch,
    /// <summary>The run, revision, or generation coordinate was substituted.</summary>
    RunBindingMismatch,
    /// <summary>The graph artifact coordinate was substituted.</summary>
    GraphArtifactMismatch,
    /// <summary>The graph layout coordinate was substituted.</summary>
    GraphLayoutMismatch,
    /// <summary>The exact owning contextual-role coordinate was substituted.</summary>
    RoleMismatch,
    /// <summary>Another stable request coordinate was substituted.</summary>
    AdmissionCoordinateMismatch,
}
