using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;

internal static class GovernedLoopHumanInputWaitingCheckpointContractCopy
{
    internal static GovernedLoopHumanInputWaitingCheckpointBinding Copy(GovernedLoopHumanInputWaitingCheckpointBinding? value)
        => value is null
            ? null!
            : new GovernedLoopHumanInputWaitingCheckpointBinding(value.SchemaVersion, value.WorkspaceId, value.Execution, Copy(value.Publication), value.GraphArtifactHash, value.GraphLayoutHash, value.AdmissionReceiptHash, value.FrontierVersion, value.FrontierHash, value.ActivationOrdinal, value.CycleId, value.CycleIteration, value.NodeId, value.NodeVisitOrdinal, value.CheckpointId);

    internal static GovernedLoopHumanInputNodeConfiguration Copy(GovernedLoopHumanInputNodeConfiguration? value)
        => GovernedLoopHumanInputNodeConfigurationSnapshot.Copy(value)!;

    internal static HumanInputPolicyResolutionSnapshot Copy(HumanInputPolicyResolutionSnapshot? value)
        => value is null
            ? null!
            : new HumanInputPolicyResolutionSnapshot(value.SchemaVersion, value.WorkspaceId, value.GraphId, value.GraphRevisionId, value.NodeId, value.ActorId, Copy(value.TimeoutPolicy), Copy(value.FailurePolicy), value.ResolvedAtUtc, value.ExpiresAtUtc, value.TerminalDisposition, value.ResolutionHash);

    private static HumanInputPolicyArtifact Copy(HumanInputPolicyArtifact value)
        => new(value.SchemaVersion, value.PolicyId, value.RevisionId, value.Kind, value.WorkspaceId, value.GraphId, value.AuthorityActorId, value.ResponseWindowMilliseconds, value.TerminalDisposition, value.ContentHash);

    internal static HumanInputRequest Copy(HumanInputRequest? value)
        => value is null
            ? null!
            : new HumanInputRequest(value.SchemaVersion, value.RequestId, value.RequestVersionId, Copy(value.Binding), value.Purpose, value.Prompt, Copy(value.ResponseSchema), value.PrivacyClass, Copy(value.EligibleRespondents), Copy(value.Timing), Copy(value.ResponsePolicy), Copy(value.ContinuationBinding), value.RequestHash);

    internal static ImmutableArray<GovernedLoopHumanInputWaitingCheckpointEvidence> Copy(ImmutableArray<GovernedLoopHumanInputWaitingCheckpointEvidence> values)
        => values.IsDefault
            ? default
            : values.Take(GovernedLoopHumanInputWaitingCheckpointContractLimits.MaxEvidenceEntries + 1).Select(Copy).ToImmutableArray();

    internal static GovernedLoopHumanInputWaitingCheckpointEvidence Copy(GovernedLoopHumanInputWaitingCheckpointEvidence? value)
        => value is null
            ? null!
            : new GovernedLoopHumanInputWaitingCheckpointEvidence(value.SchemaVersion, value.Sequence, value.Kind, value.OccurredAtUtc, Copy(value.AnswerSelection), value.SupersedingCheckpointId, value.SupersedingCheckpointHash, value.TerminalizationReceiptId, value.TerminalizationReceiptHash, value.PreviousEvidenceHash, value.EvidenceHash);

    private static GovernedLoopRevisionPublicationPin Copy(GovernedLoopRevisionPublicationPin? value)
        => value is null ? null! : new GovernedLoopRevisionPublicationPin(value.SchemaVersion, value.Revision, value.PublicationOperationId, value.ValidationEvidenceHash);

    private static HumanInputRequestBinding Copy(HumanInputRequestBinding? value)
        => value is null ? null! : new HumanInputRequestBinding(value.WorkspaceId, value.LoopGraphId, value.LoopRevisionId, value.NodeId, value.RunId, value.CheckpointId);

    private static HumanInputResponseSchema Copy(HumanInputResponseSchema? value)
        => value is null ? null! : new HumanInputResponseSchema(value.Kind, value.MaxTextCharacters, Copy(value.Choices), Copy(value.StructuredFields), Copy(value.ReferencePolicy));

    private static HumanInputChoice[]? Copy(HumanInputChoice[]? values)
        => values?.Take(HumanInputLimits.MaxChoices + 1).Select(value => value is null ? null! : new HumanInputChoice(value.ChoiceId, value.DisplayText)).ToArray();

    private static HumanInputStructuredFieldSchema[]? Copy(HumanInputStructuredFieldSchema[]? values)
        => values?.Take(HumanInputLimits.MaxStructuredFields + 1).Select(value => value is null ? null! : new HumanInputStructuredFieldSchema(value.FieldId, value.Kind, value.Required, value.MaxTextCharacters, Copy(value.Choices))).ToArray();

    private static HumanInputReferencePolicy? Copy(HumanInputReferencePolicy? value)
        => value is null ? null : new HumanInputReferencePolicy(value.Kind, value.MaxReferenceCharacters);

    private static HumanInputEligibleRespondent[] Copy(HumanInputEligibleRespondent[]? values)
        => values?.Take(HumanInputLimits.MaxEligibleRespondents + 1).Select(value => value is null ? null! : new HumanInputEligibleRespondent(value.RespondentId, value.RespondentRoleId, value.RoutingReference)).ToArray()!;

    private static HumanInputTiming Copy(HumanInputTiming? value)
        => value is null ? null! : new HumanInputTiming(value.RequestedAtUtc, value.ExpiresAtUtc);

    private static HumanInputResponsePolicy Copy(HumanInputResponsePolicy? value)
        => value is null ? null! : new HumanInputResponsePolicy(value.Kind, value.RequiredResponseCount, value.OrderedRoleIds is { } roles ? roles.Take(HumanInputLimits.MaxResponsePolicyRoles + 1).ToImmutableArray() : null);

    private static HumanInputContinuationBinding Copy(HumanInputContinuationBinding? value)
        => value is null ? null! : new HumanInputContinuationBinding(value.Kind, value.NodeId, value.CheckpointId);

    private static HumanInputResponseSelectionReference? Copy(HumanInputResponseSelectionReference? value)
        => value is null ? null : new HumanInputResponseSelectionReference(value.SchemaVersion, value.SelectionId, Copy(value.Request), value.SelectionHash);

    private static HumanInputRequestReference Copy(HumanInputRequestReference? value)
        => value is null ? null! : new HumanInputRequestReference(value.SchemaVersion, value.RequestId, value.RequestVersionId, value.RequestHash);
}
