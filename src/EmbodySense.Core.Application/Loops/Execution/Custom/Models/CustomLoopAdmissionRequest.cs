using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Represents a custom loop admission request.
/// </summary>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="ExpectedDefinitionVersion">The expected definition version.</param>
/// <param name="ExpectedDefinitionHash">The expected definition hash.</param>
/// <param name="OperationId">The operation ID.</param>
/// <param name="Actor">The actor.</param>
/// <param name="Surface">The normalized owning runtime surface.</param>
/// <param name="CurrentRoleId">The current role ID.</param>
/// <param name="InvocationPrompt">The invocation prompt.</param>
/// <param name="ModelSnapshot">The provider and model identity admitted for the run.</param>
/// <param name="InvokingConversation">The optional immutable conversation reference captured at admission.</param>
/// <param name="ContextSnapshot">The immutable provenance-tagged context captured at admission.</param>
public sealed record CustomLoopAdmissionRequest(
    string LoopId,
    int ExpectedDefinitionVersion,
    string ExpectedDefinitionHash,
    string OperationId,
    string Actor,
    string Surface,
    string CurrentRoleId,
    string? InvocationPrompt,
    CustomLoopModelSnapshot ModelSnapshot,
    CustomLoopConversationReference? InvokingConversation,
    CustomLoopContextSnapshot ContextSnapshot);
