using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using System.Text.Json.Serialization;

namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Represents a custom loop invocation operation.
/// </summary>
/// <param name="SchemaVersion">The persisted schema version.</param>
/// <param name="OperationId">The operation ID.</param>
/// <param name="RequestHash">The request hash.</param>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="ExpectedDefinitionVersion">The expected definition version.</param>
/// <param name="ExpectedDefinitionHash">The expected definition hash.</param>
/// <param name="Actor">The actor.</param>
/// <param name="Surface">The normalized owning runtime surface.</param>
/// <param name="CurrentRoleId">The current role ID.</param>
/// <param name="InvocationPromptHash">The invocation prompt hash.</param>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
/// <param name="BindingState">The binding state.</param>
/// <param name="InvokingConversationId">The invoking conversation ID.</param>
/// <param name="ContextIdentityHash">The context identity hash.</param>
/// <param name="CreatedAtUtc">The UTC creation time.</param>
/// <param name="UpdatedAtUtc">The UTC last-update time.</param>
/// <param name="State">The state.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="AdmissionStatus">The admission status.</param>
/// <param name="RunId">The unique run identifier.</param>
/// <param name="ValidationErrors">The validation errors.</param>
/// <param name="Detail">The detail.</param>
public sealed record CustomLoopInvocationOperation(
    int SchemaVersion,
    string OperationId,
    string RequestHash,
    string LoopId,
    int ExpectedDefinitionVersion,
    string ExpectedDefinitionHash,
    string Actor,
    string Surface,
    string CurrentRoleId,
    string InvocationPromptHash,
    string Provider,
    string? Model,
    CustomLoopInvocationBindingState BindingState,
    string? InvokingConversationId,
    string? ContextIdentityHash,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    CustomLoopInvocationOperationState State,
    CustomLoopInvocationOutcome Outcome,
    string AdmissionStatus,
    string? RunId,
    CustomLoopValidationError[] ValidationErrors,
    string Detail)
{
    /// <summary>
    /// Identifies the current schema version custom loop invocation operation.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Gets the exact canonical admission-request hash frozen before context capture, or null for the fenced legacy path.</summary>
    [JsonRequired]
    public string? SequentialAdmissionRequestHash { get; init; }

    /// <summary>Gets the exact immutable graph-artifact hash frozen before context capture, or null for the fenced legacy path.</summary>
    [JsonRequired]
    public string? SequentialArtifactHash { get; init; }

    /// <summary>Gets the exact bounded invocation payload frozen before canonical admission, or null for the fenced legacy path.</summary>
    [JsonRequired]
    public GovernedLoopSequentialInvocationSnapshot? SequentialInvocationSnapshot { get; init; }
}
