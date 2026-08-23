using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

/// <summary>Returns one authenticated immutable before-state snapshot from the native workspace host.</summary>
/// <param name="BeforeEvidence">The complete value-free before evidence persisted under its content-addressed identifier.</param>
public sealed record WorkspaceActionNativePreparation(WorkspaceActionBeforeEvidence BeforeEvidence);
