namespace EmbodySense.Core.Application.ContextualRoles.Models;

/// <summary>Provides bounded, non-secret diagnostic evidence for an unavailable or ambiguous contextual-role mutation.</summary>
/// <param name="Stage">The persistence stage that failed.</param>
/// <param name="NativeErrorKind">The native error-code namespace, or none when the stage failed without a native code.</param>
/// <param name="NativeErrorCode">The unsigned native status or non-negative platform error code, or <see langword="null"/> when unavailable.</param>
/// <remarks>The diagnostic never contains paths, artifact names, document content, exception messages, or stack traces.</remarks>
public sealed record ContextualRoleRevisionMutationDiagnostic(
    ContextualRolePersistenceDiagnosticStage Stage,
    ContextualRoleNativeErrorKind NativeErrorKind,
    long? NativeErrorCode);
