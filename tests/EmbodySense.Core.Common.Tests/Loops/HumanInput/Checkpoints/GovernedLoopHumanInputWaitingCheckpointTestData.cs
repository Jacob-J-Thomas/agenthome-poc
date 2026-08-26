using System.Collections.Immutable;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Tests.Loops.HumanInput.Checkpoints;

internal static class GovernedLoopHumanInputWaitingCheckpointTestData
{
    internal static readonly DateTimeOffset RequestedAtUtc = new(2026, 8, 26, 15, 0, 0, TimeSpan.Zero);

    internal static string Hash(char value) => new(value, GovernedLoopHumanInputWaitingCheckpointContractLimits.Sha256HexCharacters);

    internal static GovernedLoopHumanInputWaitingCheckpointBinding Binding(long generation = 1, string? frontierHash = null)
    {
        var revision = GovernedLoopRevisionReference.Create(1, "graph-one", "revision-one", Hash('a'));
        return new GovernedLoopHumanInputWaitingCheckpointBinding(
            1,
            "workspace-one",
            GovernedLoopExecutionBinding.Create(1, "run-one", revision, generation),
            new GovernedLoopRevisionPublicationPin(1, revision, "publication-one", Hash('b')),
            Hash('c'),
            Hash('d'),
            Hash('e'),
            7,
            frontierHash ?? Hash('f'),
            0,
            null,
            null,
            "human-input",
            1,
            "checkpoint-one");
    }

    internal static GovernedLoopHumanInputNodeConfiguration Configuration(
        string purpose = "Collect one bounded preference.",
        string prompt = "Choose one safe response.",
        string timeoutPolicyReference = "timeout-policy-one")
        => new(
            1,
            "response-schema-one",
            purpose,
            prompt,
            new HumanInputResponseSchema(HumanInputResponseKind.Choice, null, [new HumanInputChoice("choice-one", "Choice one"), new HumanInputChoice("choice-two", "Choice two")], null, null),
            HumanInputPrivacyClass.Private,
            [new HumanInputEligibleRespondent("actor-one", "role-one", "route-one")],
            new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null),
            timeoutPolicyReference,
            "failure-policy-one");

    internal static GovernedLoopHumanInputNodeConfiguration ConfigurationFor(HumanInputResponseKind kind)
    {
        var schema = kind switch
        {
            HumanInputResponseKind.Text => new HumanInputResponseSchema(HumanInputResponseKind.Text, 128, null, null, null),
            HumanInputResponseKind.Choice => new HumanInputResponseSchema(HumanInputResponseKind.Choice, null, [new HumanInputChoice("choice-one", "Choice one"), new HumanInputChoice("choice-two", "Choice two")], null, null),
            HumanInputResponseKind.Confirmation => new HumanInputResponseSchema(HumanInputResponseKind.Confirmation, null, null, null, null),
            HumanInputResponseKind.Structured => new HumanInputResponseSchema(HumanInputResponseKind.Structured, null, null, [new HumanInputStructuredFieldSchema("field-one", HumanInputStructuredFieldKind.Text, true, 128, null), new HumanInputStructuredFieldSchema("field-two", HumanInputStructuredFieldKind.Choice, false, null, [new HumanInputChoice("choice-one", "Choice one"), new HumanInputChoice("choice-two", "Choice two")])], null),
            HumanInputResponseKind.Reference => new HumanInputResponseSchema(HumanInputResponseKind.Reference, null, null, null, new HumanInputReferencePolicy(HumanInputReferenceKind.Artifact, 128)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A supported Human Input response kind is required.")
        };
        return new GovernedLoopHumanInputNodeConfiguration(1, "response-schema-one", "Collect one bounded preference.", "Choose one safe response.", schema, HumanInputPrivacyClass.Private, [new HumanInputEligibleRespondent("actor-one", "role-one", "route-one")], new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null), "timeout-policy-one", "failure-policy-one");
    }

    internal static HumanInputRequest Request(GovernedLoopHumanInputWaitingCheckpointBinding binding, GovernedLoopHumanInputNodeConfiguration? configuration = null)
    {
        var config = configuration ?? Configuration();
        return HumanInputRequestHash.Apply(new HumanInputRequest(
            1,
            "request-one",
            "request-version-one",
            new HumanInputRequestBinding(binding.WorkspaceId, binding.Execution.Revision.GraphId, binding.Execution.Revision.RevisionId, binding.NodeId, binding.Execution.RunId, binding.CheckpointId),
            config.Purpose!,
            config.Prompt!,
            config.ResponseSchema!,
            config.PrivacyClass,
            config.EligibleRespondents!.Cast<HumanInputEligibleRespondent>().ToArray(),
            new HumanInputTiming(RequestedAtUtc, RequestedAtUtc.AddHours(1)),
            config.ResponsePolicy!,
            new HumanInputContinuationBinding(HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly, binding.NodeId, binding.CheckpointId),
            string.Empty));
    }

    internal static GovernedLoopHumanInputWaitingCheckpoint Pending(
        GovernedLoopHumanInputWaitingCheckpointBinding? binding = null,
        GovernedLoopHumanInputNodeConfiguration? configuration = null,
        HumanInputRequest? request = null)
    {
        var exactBinding = binding ?? Binding();
        var exactConfiguration = configuration ?? Configuration();
        var exactRequest = request ?? Request(exactBinding, exactConfiguration);
        var published = Evidence(1, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Published, exactRequest.Timing.RequestedAtUtc, null, null, null, null, null, string.Empty);
        return GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(1, exactBinding, exactConfiguration, exactRequest, GovernedLoopHumanInputWaitingCheckpointPosture.Pending, [published], string.Empty));
    }

    internal static GovernedLoopHumanInputWaitingCheckpoint Answered(GovernedLoopHumanInputWaitingCheckpoint? pending = null)
    {
        var previous = pending ?? Pending();
        var answer = Evidence(2, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Answered, RequestedAtUtc.AddMinutes(10), Selection(previous.Request), null, null, null, null, previous.Evidence[0].EvidenceHash);
        return Checkpoint(previous, GovernedLoopHumanInputWaitingCheckpointPosture.AnsweredNotResumed, [previous.Evidence[0], answer]);
    }

    internal static GovernedLoopHumanInputWaitingCheckpoint Terminal(GovernedLoopHumanInputWaitingCheckpoint? answered = null)
    {
        var previous = answered ?? Answered();
        var terminal = Evidence(3, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Terminalized, RequestedAtUtc.AddMinutes(11), null, null, null, "terminal-receipt-one", Hash('c'), previous.Evidence[1].EvidenceHash);
        return Checkpoint(previous, GovernedLoopHumanInputWaitingCheckpointPosture.Terminal, [previous.Evidence[0], previous.Evidence[1], terminal]);
    }

    internal static GovernedLoopHumanInputWaitingCheckpoint Expired(GovernedLoopHumanInputWaitingCheckpoint? pending = null)
    {
        var previous = pending ?? Pending();
        var expired = Evidence(2, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Expired, previous.Request.Timing.ExpiresAtUtc, null, null, null, null, null, previous.Evidence[0].EvidenceHash);
        return Checkpoint(previous, GovernedLoopHumanInputWaitingCheckpointPosture.Expired, [previous.Evidence[0], expired]);
    }

    internal static GovernedLoopHumanInputWaitingCheckpoint Cancelled(GovernedLoopHumanInputWaitingCheckpoint? pending = null)
    {
        var previous = pending ?? Pending();
        var cancelled = Evidence(2, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Cancelled, RequestedAtUtc.AddMinutes(5), null, null, null, null, null, previous.Evidence[0].EvidenceHash);
        return Checkpoint(previous, GovernedLoopHumanInputWaitingCheckpointPosture.Cancelled, [previous.Evidence[0], cancelled]);
    }

    internal static GovernedLoopHumanInputWaitingCheckpoint Superseded(GovernedLoopHumanInputWaitingCheckpoint? pending = null)
    {
        var previous = pending ?? Pending();
        var superseded = Evidence(2, GovernedLoopHumanInputWaitingCheckpointEvidenceKind.Superseded, RequestedAtUtc.AddMinutes(5), null, "checkpoint-two", Hash('b'), null, null, previous.Evidence[0].EvidenceHash);
        return Checkpoint(previous, GovernedLoopHumanInputWaitingCheckpointPosture.Superseded, [previous.Evidence[0], superseded]);
    }

    internal static HumanInputResponseSelectionReference Selection(HumanInputRequest request)
        => new(1, "selection-one", new HumanInputRequestReference(1, request.RequestId, request.RequestVersionId, request.RequestHash), Hash('a'));

    private static GovernedLoopHumanInputWaitingCheckpoint Checkpoint(GovernedLoopHumanInputWaitingCheckpoint previous, GovernedLoopHumanInputWaitingCheckpointPosture posture, ImmutableArray<GovernedLoopHumanInputWaitingCheckpointEvidence> evidence)
        => GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpoint(1, previous.Binding, previous.NodeConfiguration, previous.Request, posture, evidence, string.Empty));

    private static GovernedLoopHumanInputWaitingCheckpointEvidence Evidence(
        long sequence,
        GovernedLoopHumanInputWaitingCheckpointEvidenceKind kind,
        DateTimeOffset occurredAtUtc,
        HumanInputResponseSelectionReference? selection,
        string? supersedingId,
        string? supersedingHash,
        string? terminalId,
        string? terminalHash,
        string previousHash)
        => GovernedLoopHumanInputWaitingCheckpointContractHash.Apply(new GovernedLoopHumanInputWaitingCheckpointEvidence(1, sequence, kind, occurredAtUtc, selection, supersedingId, supersedingHash, terminalId, terminalHash, previousHash, string.Empty));
}
