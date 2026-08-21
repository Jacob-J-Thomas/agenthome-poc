using System.Globalization;
using System.Text;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Secrets.Redaction;
using EmbodySense.Core.Common.Secrets.Redaction.Models;

namespace EmbodySense.Core.Common.CommandActions;

/// <summary>Creates and authenticates bounded value-free preparation and redacted process-outcome evidence.</summary>
public static class CommandActionEvidenceContract
{
    /// <summary>Normalizes retained command text and replaces unsafe Unicode controls, format characters, and noncharacters.</summary>
    public static string SanitizeRetainedText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value.IsNormalized(NormalizationForm.FormC) ? value : value.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var unsafeRune = category == UnicodeCategory.Format
                || category == UnicodeCategory.Control && rune.Value is not 0x09 and not 0x0a and not 0x0d
                || rune.Value is >= 0xfdd0 and <= 0xfdef
                || (rune.Value & 0xffff) is 0xfffe or 0xffff;
            builder.Append(unsafeRune ? Rune.ReplacementChar : rune);
        }
        return builder.ToString();
    }

    /// <summary>Creates content-addressed preparation evidence.</summary>
    public static CommandActionPreparationEvidence CreatePreparation(
        CommandActionTemplate template,
        string inputFingerprint,
        string targetFingerprint,
        string preconditionEvidenceHash,
        DateTimeOffset recordedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(template);
        var candidate = new CommandActionPreparationEvidence(
            1,
            template.TemplateId,
            template.TemplateVersion,
            template.ContentHash,
            template.ArtifactDigest,
            template.ActivationRevision,
            inputFingerprint,
            targetFingerprint,
            preconditionEvidenceHash,
            recordedAtUtc,
            string.Empty);
        var reasonCode = ValidatePreparationForId(candidate);
        if (reasonCode is not null)
        {
            throw new ArgumentException(reasonCode, nameof(template));
        }
        return candidate with { EvidenceId = PreparationId(candidate) };
    }

    /// <summary>Returns a reason code when preparation evidence is malformed or unauthentic.</summary>
    public static string? ValidatePreparation(CommandActionPreparationEvidence? evidence)
    {
        var reasonCode = ValidatePreparationForId(evidence);
        return reasonCode is not null
            ? reasonCode
            : string.Equals(evidence!.EvidenceId, PreparationId(evidence), StringComparison.Ordinal)
                ? null
                : "command-preparation-evidence-id-mismatch";
    }

    /// <summary>Creates content-addressed conclusive process outcome evidence.</summary>
    public static CommandActionOutcomeEvidence CreateOutcome(
        string effectId,
        string idempotencyOperationId,
        long effectGeneration,
        CommandActionTemplate template,
        string inputFingerprint,
        string targetFingerprint,
        string preconditionEvidenceHash,
        string beforeEvidenceId,
        CommandActionOutcomeKind outcome,
        CommandActionTerminationPosture termination,
        int? exitCode,
        string? retainedStandardOutput,
        string? retainedStandardError,
        int observedStandardOutputBytes,
        int observedStandardErrorBytes,
        long durationMilliseconds,
        DateTimeOffset recordedAtUtc,
        RedactionSummary? redactionSummary = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        var candidate = new CommandActionOutcomeEvidence(
            1,
            effectId,
            idempotencyOperationId,
            effectGeneration,
            template.TemplateId,
            template.TemplateVersion,
            template.ContentHash,
            template.ArtifactDigest,
            template.ActivationRevision,
            inputFingerprint,
            targetFingerprint,
            preconditionEvidenceHash,
            beforeEvidenceId,
            outcome,
            termination,
            exitCode,
            retainedStandardOutput,
            retainedStandardError,
            observedStandardOutputBytes,
            observedStandardErrorBytes,
            durationMilliseconds,
            true,
            redactionSummary ?? new RedactionSummary(RedactionStatus.Completed, 0, 0, 0, 0, 0),
            recordedAtUtc,
            string.Empty);
        var reasonCode = ValidateOutcomeForId(candidate);
        if (reasonCode is not null)
        {
            throw new ArgumentException(reasonCode, nameof(outcome));
        }
        return candidate with { EvidenceId = OutcomeId(candidate) };
    }

    /// <summary>Returns a reason code when outcome evidence is malformed or unauthentic.</summary>
    public static string? ValidateOutcome(CommandActionOutcomeEvidence? evidence)
    {
        var reasonCode = ValidateOutcomeForId(evidence);
        return reasonCode is not null
            ? reasonCode
            : string.Equals(evidence!.EvidenceId, OutcomeId(evidence), StringComparison.Ordinal)
                ? null
                : "command-outcome-evidence-id-mismatch";
    }

    private static string? ValidatePreparationForId(CommandActionPreparationEvidence? evidence)
    {
        if (evidence is null)
        {
            return "command-preparation-evidence-required";
        }
        return evidence.SchemaVersion != 1
            || !CommandActionTemplateContract.IsTemplateId(evidence.TemplateId)
            || evidence.TemplateVersion < 1
            || !CommandActionFingerprint.IsCanonicalSha256(evidence.TemplateHash)
            || evidence.ArtifactDigest is null
            || !CapabilityIntegrityDigest.TryParse(evidence.ArtifactDigest.Value, out _, out _)
            || evidence.ActivationRevision < 1
            || !CommandActionFingerprint.IsCanonicalSha256(evidence.InputFingerprint)
            || !CommandActionFingerprint.IsCanonicalSha256(evidence.TargetFingerprint)
            || !CommandActionFingerprint.IsCanonicalSha256(evidence.PreconditionEvidenceHash)
            || !IsUtc(evidence.RecordedAtUtc)
                ? "command-preparation-evidence-invalid"
                : null;
    }

    private static string? ValidateOutcomeForId(CommandActionOutcomeEvidence? evidence)
    {
        if (evidence is null)
        {
            return "command-outcome-evidence-required";
        }
        var terminalShape = evidence.Outcome switch
        {
            CommandActionOutcomeKind.Succeeded => evidence.Termination == CommandActionTerminationPosture.Exited && evidence.ExitCode == 0 && IsCanonicalJson(evidence.RetainedStandardOutput),
            CommandActionOutcomeKind.NonZeroExit => evidence.Termination == CommandActionTerminationPosture.Exited && evidence.ExitCode is not null and not 0,
            CommandActionOutcomeKind.MalformedResult or CommandActionOutcomeKind.InvalidEncoding => evidence.Termination == CommandActionTerminationPosture.Exited,
            CommandActionOutcomeKind.OutputLimitExceeded or CommandActionOutcomeKind.TimedOut or CommandActionOutcomeKind.Cancelled => evidence.Termination == CommandActionTerminationPosture.ProcessTreeTerminated,
            CommandActionOutcomeKind.IsolationRejected => evidence.Termination == CommandActionTerminationPosture.NotStarted && evidence.ExitCode is null,
            _ => false,
        };
        return evidence.SchemaVersion != 1
            || !CommandActionFingerprint.IsEvidenceIdentifier(evidence.EffectId)
            || !CommandActionFingerprint.IsEvidenceIdentifier(evidence.IdempotencyOperationId)
            || evidence.EffectGeneration < 1
            || !CommandActionTemplateContract.IsTemplateId(evidence.TemplateId)
            || evidence.TemplateVersion < 1
            || !CommandActionFingerprint.IsCanonicalSha256(evidence.TemplateHash)
            || evidence.ArtifactDigest is null
            || !CapabilityIntegrityDigest.TryParse(evidence.ArtifactDigest.Value, out _, out _)
            || evidence.ActivationRevision < 1
            || !CommandActionFingerprint.IsCanonicalSha256(evidence.InputFingerprint)
            || !CommandActionFingerprint.IsCanonicalSha256(evidence.TargetFingerprint)
            || !CommandActionFingerprint.IsCanonicalSha256(evidence.PreconditionEvidenceHash)
            || !CommandActionFingerprint.IsEvidenceIdentifier(evidence.BeforeEvidenceId)
            || !terminalShape
            || !IsRetainedText(evidence.RetainedStandardOutput)
            || !IsRetainedText(evidence.RetainedStandardError)
            || evidence.ObservedStandardOutputBytes is < 0 or > CommandActionContractLimits.MaxOutputBytes + 1
            || evidence.ObservedStandardErrorBytes is < 0 or > CommandActionContractLimits.MaxOutputBytes + 1
            || (long)evidence.ObservedStandardOutputBytes + evidence.ObservedStandardErrorBytes > CommandActionContractLimits.MaxOutputBytes + 1L
            || evidence.DurationMilliseconds is < 0 or > CommandActionContractLimits.MaxExecutionMilliseconds + CommandActionContractLimits.MaxTerminationMilliseconds
            || !evidence.RedactionApplied
            || !IsRedactionSummary(evidence.RedactionSummary)
            || !IsUtc(evidence.RecordedAtUtc)
                ? "command-outcome-evidence-invalid"
                : null;
    }

    private static string PreparationId(CommandActionPreparationEvidence evidence)
        => "command-before-" + CommandActionFingerprint.Compute(
            "embodysense.command-action-preparation-evidence.v1",
            evidence.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            evidence.TemplateId,
            evidence.TemplateVersion.ToString(CultureInfo.InvariantCulture),
            evidence.TemplateHash,
            evidence.ArtifactDigest.Value,
            evidence.ActivationRevision.ToString(CultureInfo.InvariantCulture),
            evidence.InputFingerprint,
            evidence.TargetFingerprint,
            evidence.PreconditionEvidenceHash,
            evidence.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture));

    private static string OutcomeId(CommandActionOutcomeEvidence evidence)
        => "command-outcome-" + CommandActionFingerprint.Compute(
            "embodysense.command-action-outcome-evidence.v1",
            evidence.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            evidence.EffectId,
            evidence.IdempotencyOperationId,
            evidence.EffectGeneration.ToString(CultureInfo.InvariantCulture),
            evidence.TemplateId,
            evidence.TemplateVersion.ToString(CultureInfo.InvariantCulture),
            evidence.TemplateHash,
            evidence.ArtifactDigest.Value,
            evidence.ActivationRevision.ToString(CultureInfo.InvariantCulture),
            evidence.InputFingerprint,
            evidence.TargetFingerprint,
            evidence.PreconditionEvidenceHash,
            evidence.BeforeEvidenceId,
            ((int)evidence.Outcome).ToString(CultureInfo.InvariantCulture),
            ((int)evidence.Termination).ToString(CultureInfo.InvariantCulture),
            evidence.ExitCode?.ToString(CultureInfo.InvariantCulture),
            evidence.RetainedStandardOutput,
            evidence.RetainedStandardError,
            evidence.ObservedStandardOutputBytes.ToString(CultureInfo.InvariantCulture),
            evidence.ObservedStandardErrorBytes.ToString(CultureInfo.InvariantCulture),
            evidence.DurationMilliseconds.ToString(CultureInfo.InvariantCulture),
            evidence.RedactionApplied ? "1" : "0",
            ((int)evidence.RedactionSummary.Status).ToString(CultureInfo.InvariantCulture),
            evidence.RedactionSummary.SensitiveValueCount.ToString(CultureInfo.InvariantCulture),
            evidence.RedactionSummary.IgnoredValueCount.ToString(CultureInfo.InvariantCulture),
            evidence.RedactionSummary.ReplacementCount.ToString(CultureInfo.InvariantCulture),
            evidence.RedactionSummary.ExaminedCharacterCount.ToString(CultureInfo.InvariantCulture),
            evidence.RedactionSummary.WorkUnitCount.ToString(CultureInfo.InvariantCulture),
            evidence.RecordedAtUtc.ToString("O", CultureInfo.InvariantCulture));

    private static bool IsRedactionSummary(RedactionSummary? summary)
        => summary is not null
            && Enum.IsDefined(summary.Status)
            && summary.SensitiveValueCount is >= 0 and <= RedactionLimits.AbsoluteMaxSensitiveValues
            && summary.IgnoredValueCount is >= 0 and <= RedactionLimits.AbsoluteMaxSensitiveValues
            && summary.ReplacementCount >= 0
            && summary.ExaminedCharacterCount is >= 0 and <= RedactionLimits.AbsoluteMaxProjectionCharacters * 2
            && summary.WorkUnitCount is >= 0 and <= RedactionLimits.AbsoluteMaxWorkUnits * 2;

    private static bool IsRetainedText(string? value)
        => value is null
            || value.Length <= CommandActionContractLimits.MaxRetainedOutputCharacters
                && CapabilityTextRules.IsSafeNormalized(value, CommandActionContractLimits.MaxRetainedOutputCharacters, allowEmpty: true);

    private static bool IsCanonicalJson(string? value)
        => value is not null
            && GovernedActuatorInputContract.TryCanonicalize(value, out var canonical, out _)
            && string.Equals(value, canonical!.CanonicalJson, StringComparison.Ordinal);

    private static bool IsUtc(DateTimeOffset value) => value != default && value.Offset == TimeSpan.Zero;
}
