using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints;

/// <summary>Computes and verifies canonical schema-1 hashes for immutable Human Input waiting checkpoints and their append-only evidence.</summary>
public static class GovernedLoopHumanInputWaitingCheckpointContractHash
{
    /// <summary>Computes the canonical checkpoint hash excluding only its self-referential hash field.</summary>
    /// <param name="checkpoint">The checkpoint to hash.</param>
    /// <returns>The lowercase SHA-256 hash of every behavior-affecting checkpoint coordinate.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    public static string ComputeCheckpointHash(GovernedLoopHumanInputWaitingCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var builder = Start("governed-loop-human-input-waiting-checkpoint-v1");
        Append(builder, checkpoint.SchemaVersion);
        AppendBinding(builder, checkpoint.Binding);
        AppendConfiguration(builder, checkpoint.NodeConfiguration);
        AppendRequest(builder, checkpoint.Request);
        Append(builder, (int)checkpoint.Posture);
        AppendEvidenceHistory(builder, checkpoint.Evidence);
        return Digest(builder);
    }

    /// <summary>Returns a checkpoint with every nested evidence hash and its canonical checkpoint hash applied.</summary>
    /// <param name="checkpoint">The checkpoint to hash.</param>
    /// <returns>A detached checkpoint with canonical hashes applied.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="checkpoint"/> is <see langword="null"/>.</exception>
    public static GovernedLoopHumanInputWaitingCheckpoint Apply(GovernedLoopHumanInputWaitingCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var evidence = checkpoint.Evidence.IsDefault ? default : checkpoint.Evidence.Select(Apply).ToImmutableArray();
        var prepared = new GovernedLoopHumanInputWaitingCheckpoint(checkpoint.SchemaVersion, checkpoint.Binding, checkpoint.NodeConfiguration, checkpoint.Request, checkpoint.Posture, evidence, string.Empty);
        return new GovernedLoopHumanInputWaitingCheckpoint(prepared.SchemaVersion, prepared.Binding, prepared.NodeConfiguration, prepared.Request, prepared.Posture, prepared.Evidence, ComputeCheckpointHash(prepared));
    }

    /// <summary>Gets whether a checkpoint retains exact nested evidence hashes and its canonical checkpoint hash.</summary>
    /// <param name="checkpoint">The checkpoint to verify.</param>
    /// <returns><see langword="true"/> only when every stored hash is canonical and exact.</returns>
    public static bool Matches(GovernedLoopHumanInputWaitingCheckpoint? checkpoint)
    {
        if (checkpoint is null || !IsSha256(checkpoint.CheckpointHash) || checkpoint.Evidence.IsDefault || checkpoint.Evidence.Any(value => !Matches(value)))
        {
            return false;
        }

        try
        {
            return FixedEquals(ComputeCheckpointHash(checkpoint), checkpoint.CheckpointHash);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            return false;
        }
    }

    /// <summary>Computes the canonical hash for one append-only evidence record excluding only its self-referential hash field.</summary>
    /// <param name="evidence">The evidence record to hash.</param>
    /// <returns>The lowercase SHA-256 hash of the evidence record.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="evidence"/> is <see langword="null"/>.</exception>
    public static string ComputeEvidenceHash(GovernedLoopHumanInputWaitingCheckpointEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var builder = Start("governed-loop-human-input-waiting-checkpoint-evidence-v1");
        AppendEvidence(builder, evidence, includeHash: false);
        return Digest(builder);
    }

    /// <summary>Returns one evidence record with its canonical hash applied.</summary>
    /// <param name="evidence">The evidence record to hash.</param>
    /// <returns>A copy carrying its exact evidence hash.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="evidence"/> is <see langword="null"/>.</exception>
    public static GovernedLoopHumanInputWaitingCheckpointEvidence Apply(GovernedLoopHumanInputWaitingCheckpointEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence with { EvidenceHash = ComputeEvidenceHash(evidence) };
    }

    /// <summary>Gets whether one evidence record retains its exact canonical hash.</summary>
    /// <param name="evidence">The evidence record to verify.</param>
    /// <returns><see langword="true"/> only when the stored evidence hash matches canonical content.</returns>
    public static bool Matches(GovernedLoopHumanInputWaitingCheckpointEvidence? evidence)
    {
        if (evidence is null || !IsSha256(evidence.EvidenceHash))
        {
            return false;
        }

        try
        {
            return FixedEquals(ComputeEvidenceHash(evidence), evidence.EvidenceHash);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IndexOutOfRangeException or NullReferenceException)
        {
            return false;
        }
    }

    /// <summary>Gets whether a value is one canonical lowercase SHA-256 hash.</summary>
    /// <param name="value">The candidate hash.</param>
    /// <returns><see langword="true"/> when the value is exactly 64 lowercase hexadecimal characters.</returns>
    public static bool IsSha256(string? value)
        => value is { Length: GovernedLoopHumanInputWaitingCheckpointContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static StringBuilder Start(string domain)
    {
        var builder = new StringBuilder(2048);
        Append(builder, domain);
        return builder;
    }

    private static string Digest(StringBuilder builder) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();

    private static bool FixedEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static void AppendBinding(StringBuilder builder, GovernedLoopHumanInputWaitingCheckpointBinding? binding)
    {
        if (binding is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, binding.SchemaVersion);
        Append(builder, binding.WorkspaceId);
        AppendExecution(builder, binding.Execution);
        AppendPublication(builder, binding.Publication);
        Append(builder, binding.GraphArtifactHash);
        Append(builder, binding.GraphLayoutHash);
        Append(builder, binding.AdmissionReceiptHash);
        Append(builder, binding.FrontierVersion);
        Append(builder, binding.FrontierHash);
        Append(builder, binding.ActivationOrdinal);
        Append(builder, binding.CycleId);
        AppendOptionalInt(builder, binding.CycleIteration);
        Append(builder, binding.NodeId);
        Append(builder, binding.NodeVisitOrdinal);
        Append(builder, binding.CheckpointId);
    }

    private static void AppendExecution(StringBuilder builder, EmbodySense.Core.Common.Loops.Execution.GovernedLoopExecutionBinding? value)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, value.SchemaVersion);
        Append(builder, value.RunId);
        AppendRevision(builder, value.Revision);
        Append(builder, value.ExecutionGeneration);
    }

    private static void AppendPublication(StringBuilder builder, EmbodySense.Core.Common.Loops.Revisions.Models.GovernedLoopRevisionPublicationPin? value)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, value.SchemaVersion);
        AppendRevision(builder, value.Revision);
        Append(builder, value.PublicationOperationId);
        Append(builder, value.ValidationEvidenceHash);
    }

    private static void AppendRevision(StringBuilder builder, GovernedLoopRevisionReference? value)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, value.SchemaVersion);
        Append(builder, value.GraphId);
        Append(builder, value.RevisionId);
        Append(builder, value.ExecutableHash);
    }

    private static void AppendConfiguration(StringBuilder builder, GovernedLoopHumanInputNodeConfiguration? value)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, value.SchemaVersion);
        Append(builder, value.RequestSchemaReference);
        Append(builder, value.Purpose);
        Append(builder, value.Prompt);
        AppendResponseSchema(builder, value.ResponseSchema);
        Append(builder, (int)value.PrivacyClass);
        AppendRespondents(builder, value.EligibleRespondents);
        AppendResponsePolicy(builder, value.ResponsePolicy);
        Append(builder, value.TimeoutPolicyReference);
        Append(builder, value.FailurePolicyReference);
    }

    private static void AppendRequest(StringBuilder builder, HumanInputRequest? value)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, value.SchemaVersion);
        Append(builder, value.RequestId);
        Append(builder, value.RequestVersionId);
        AppendRequestBinding(builder, value.Binding);
        Append(builder, value.Purpose);
        Append(builder, value.Prompt);
        AppendResponseSchema(builder, value.ResponseSchema);
        Append(builder, (int)value.PrivacyClass);
        AppendRespondents(builder, value.EligibleRespondents);
        AppendTiming(builder, value.Timing);
        AppendResponsePolicy(builder, value.ResponsePolicy);
        AppendContinuationBinding(builder, value.ContinuationBinding);
        Append(builder, value.RequestHash);
    }

    private static void AppendRequestBinding(StringBuilder builder, HumanInputRequestBinding? value)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, value.WorkspaceId);
        Append(builder, value.LoopGraphId);
        Append(builder, value.LoopRevisionId);
        Append(builder, value.NodeId);
        Append(builder, value.RunId);
        Append(builder, value.CheckpointId);
    }

    private static void AppendResponseSchema(StringBuilder builder, HumanInputResponseSchema? value)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, (int)value.Kind);
        AppendOptionalInt(builder, value.MaxTextCharacters);
        AppendChoices(builder, value.Choices);
        AppendStructuredFields(builder, value.StructuredFields);
        AppendReferencePolicy(builder, value.ReferencePolicy);
    }

    private static void AppendChoices(StringBuilder builder, IEnumerable<HumanInputChoice?>? values)
    {
        if (values is null)
        {
            Append(builder, null);
            return;
        }

        var copied = values.Take(EmbodySense.Core.Common.HumanInput.HumanInputLimits.MaxChoices + 1).ToArray();
        Append(builder, copied.Length);
        foreach (var value in copied)
        {
            if (value is null)
            {
                Append(builder, null);
                continue;
            }

            Append(builder, value.ChoiceId);
            Append(builder, value.DisplayText);
        }
    }

    private static void AppendStructuredFields(StringBuilder builder, IEnumerable<HumanInputStructuredFieldSchema?>? values)
    {
        if (values is null)
        {
            Append(builder, null);
            return;
        }

        var copied = values.Take(EmbodySense.Core.Common.HumanInput.HumanInputLimits.MaxStructuredFields + 1).ToArray();
        Append(builder, copied.Length);
        foreach (var value in copied)
        {
            if (value is null)
            {
                Append(builder, null);
                continue;
            }

            Append(builder, value.FieldId);
            Append(builder, (int)value.Kind);
            Append(builder, value.Required);
            AppendOptionalInt(builder, value.MaxTextCharacters);
            AppendChoices(builder, value.Choices);
        }
    }

    private static void AppendReferencePolicy(StringBuilder builder, HumanInputReferencePolicy? value)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, (int)value.Kind);
        AppendOptionalInt(builder, value.MaxReferenceCharacters);
    }

    private static void AppendRespondents(StringBuilder builder, IEnumerable<HumanInputEligibleRespondent?>? values)
    {
        if (values is null)
        {
            Append(builder, null);
            return;
        }

        var copied = values.Take(EmbodySense.Core.Common.HumanInput.HumanInputLimits.MaxEligibleRespondents + 1).ToArray();
        Append(builder, copied.Length);
        foreach (var value in copied)
        {
            if (value is null)
            {
                Append(builder, null);
                continue;
            }

            Append(builder, value.RespondentId);
            Append(builder, value.RespondentRoleId);
            Append(builder, value.RoutingReference);
        }
    }

    private static void AppendTiming(StringBuilder builder, HumanInputTiming? value)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, value.RequestedAtUtc);
        Append(builder, value.ExpiresAtUtc);
    }

    private static void AppendResponsePolicy(StringBuilder builder, HumanInputResponsePolicy? value)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, (int)value.Kind);
        AppendOptionalInt(builder, value.RequiredResponseCount);
        if (value.OrderedRoleIds is not { } roles)
        {
            Append(builder, null);
            return;
        }

        if (roles.IsDefault)
        {
            Append(builder, "default");
            return;
        }

        var copied = roles.Take(EmbodySense.Core.Common.HumanInput.HumanInputLimits.MaxResponsePolicyRoles + 1).ToArray();
        Append(builder, copied.Length);
        foreach (var role in copied) Append(builder, role);
    }

    private static void AppendContinuationBinding(StringBuilder builder, HumanInputContinuationBinding? value)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, (int)value.Kind);
        Append(builder, value.NodeId);
        Append(builder, value.CheckpointId);
    }

    private static void AppendEvidenceHistory(StringBuilder builder, ImmutableArray<GovernedLoopHumanInputWaitingCheckpointEvidence> values)
    {
        if (values.IsDefault)
        {
            Append(builder, null);
            return;
        }

        Append(builder, values.Length);
        foreach (var value in values) AppendEvidence(builder, value, includeHash: true);
    }

    private static void AppendEvidence(StringBuilder builder, GovernedLoopHumanInputWaitingCheckpointEvidence? value, bool includeHash)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, value.SchemaVersion);
        Append(builder, value.Sequence);
        Append(builder, (int)value.Kind);
        Append(builder, value.OccurredAtUtc);
        AppendAnswerSelection(builder, value.AnswerSelection);
        Append(builder, value.SupersedingCheckpointId);
        Append(builder, value.SupersedingCheckpointHash);
        Append(builder, value.TerminalizationReceiptId);
        Append(builder, value.TerminalizationReceiptHash);
        Append(builder, value.PreviousEvidenceHash);
        if (includeHash) Append(builder, value.EvidenceHash);
    }

    private static void AppendAnswerSelection(StringBuilder builder, HumanInputResponseSelectionReference? value)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, value.SchemaVersion);
        Append(builder, value.SelectionId);
        AppendRequestReference(builder, value.Request);
        Append(builder, value.SelectionHash);
    }

    private static void AppendRequestReference(StringBuilder builder, HumanInputRequestReference? value)
    {
        if (value is null)
        {
            Append(builder, null);
            return;
        }

        Append(builder, value.SchemaVersion);
        Append(builder, value.RequestId);
        Append(builder, value.RequestVersionId);
        Append(builder, value.RequestHash);
    }

    private static void Append(StringBuilder builder, DateTimeOffset value) => Append(builder, value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    private static void Append(StringBuilder builder, int value) => Append(builder, value.ToString(CultureInfo.InvariantCulture));
    private static void Append(StringBuilder builder, long value) => Append(builder, value.ToString(CultureInfo.InvariantCulture));
    private static void Append(StringBuilder builder, bool value) => Append(builder, value ? "true" : "false");
    private static void AppendOptionalInt(StringBuilder builder, int? value) => Append(builder, value?.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }

        var normalized = value.Normalize(NormalizationForm.FormC);
        builder.Append(Encoding.UTF8.GetByteCount(normalized).ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(normalized);
    }
}
