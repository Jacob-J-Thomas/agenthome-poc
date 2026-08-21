namespace EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

/// <summary>Contains one exact literal or one value-free credential reference, never both.</summary>
/// <param name="Kind">The segment kind.</param>
/// <param name="Literal">The exact literal text when <paramref name="Kind"/> is <see cref="WorkspaceActionContentSegmentKind.LiteralUtf8"/>.</param>
/// <param name="CredentialReferenceId">The value-free reference when <paramref name="Kind"/> is <see cref="WorkspaceActionContentSegmentKind.CredentialReference"/>.</param>
public sealed record WorkspaceActionContentSegment(
    WorkspaceActionContentSegmentKind Kind,
    string? Literal,
    string? CredentialReferenceId);
