using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation;

/// <summary>Computes and verifies domain-separated canonical hashes for effect-reconciliation evidence.</summary>
public static class GovernedLoopEffectReconciliationContractHash
{
    /// <summary>Computes the canonical hash of one structurally valid exact binding.</summary>
    public static string Compute(GovernedLoopEffectReconciliationBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        GovernedLoopEffectReconciliationContractValidator.ThrowIfInvalidForHash(binding, nameof(binding));
        var canonical = Start("embodysense.governed-loop-effect-reconciliation-binding.v1");
        AppendBinding(canonical, binding);
        return Finish(canonical);
    }

    /// <summary>Computes the canonical hash of one structurally valid actuator reconciliation contract.</summary>
    public static string Compute(GovernedLoopEffectReconciliationContractMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        GovernedLoopEffectReconciliationContractValidator.ThrowIfInvalidForHash(metadata, nameof(metadata));
        var canonical = Start("embodysense.governed-loop-effect-reconciliation-contract.v1");
        Append(canonical, metadata.SchemaVersion);
        Append(canonical, metadata.ContractId);
        Append(canonical, metadata.ContractVersion);
        Append(canonical, metadata.Capability.Id.Value);
        Append(canonical, metadata.Capability.Version.Value);
        Append(canonical, metadata.Capability.Hash.Value);
        Append(canonical, metadata.Implementation.ProviderId.Value);
        Append(canonical, metadata.Implementation.ImplementationId);
        Append(canonical, metadata.ActuatorOperationId);
        Append(canonical, metadata.OperationDescriptorHash);
        Append(canonical, metadata.ProbeContractId);
        Append(canonical, metadata.ProbeContractVersion);
        Append(canonical, metadata.ProbeContractHash);
        return Finish(canonical);
    }

    /// <summary>Computes the canonical hash of one structurally valid source registration.</summary>
    public static string Compute(GovernedLoopEffectReconciliationEvidenceSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        GovernedLoopEffectReconciliationContractValidator.ThrowIfInvalidForHash(source, nameof(source));
        var canonical = Start("embodysense.governed-loop-effect-reconciliation-source.v1");
        Append(canonical, source.SchemaVersion);
        Append(canonical, source.CaseId);
        Append(canonical, source.BindingHash);
        Append(canonical, source.SourceId);
        Append(canonical, (int)source.Kind);
        Append(canonical, (int)source.ReliabilityPosture);
        Append(canonical, source.ReconciliationContractId);
        Append(canonical, source.ReconciliationContractVersion);
        Append(canonical, source.ReconciliationContractHash);
        Append(canonical, source.RegistrationEvidenceHash);
        Append(canonical, source.RegisteredAtUtc);
        Append(canonical, source.RetiredAtUtc);
        return Finish(canonical);
    }

    /// <summary>Computes the canonical hash of one structurally valid observation.</summary>
    public static string Compute(GovernedLoopEffectReconciliationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        GovernedLoopEffectReconciliationContractValidator.ThrowIfInvalidForHash(observation, nameof(observation));
        var canonical = Start("embodysense.governed-loop-effect-reconciliation-observation.v1");
        Append(canonical, observation.SchemaVersion);
        Append(canonical, observation.CaseId);
        Append(canonical, observation.BindingHash);
        Append(canonical, observation.ObservationId);
        Append(canonical, observation.SourceId);
        Append(canonical, observation.SourceRegistrationHash);
        Append(canonical, (int)observation.Kind);
        Append(canonical, (int)observation.ReliabilityPosture);
        Append(canonical, (int)observation.ObservedOutcome);
        Append(canonical, observation.EvidenceReference);
        Append(canonical, observation.EvidenceHash);
        Append(canonical, observation.ObservedAtUtc);
        Append(canonical, observation.RecordedAtUtc);
        Append(canonical, observation.SafeSummary);
        return Finish(canonical);
    }

    /// <summary>Computes the canonical hash of one structurally valid assessment.</summary>
    public static string Compute(GovernedLoopEffectReconciliationAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        GovernedLoopEffectReconciliationContractValidator.ThrowIfInvalidForHash(assessment, nameof(assessment));
        var canonical = Start("embodysense.governed-loop-effect-reconciliation-assessment.v1");
        Append(canonical, assessment.SchemaVersion);
        Append(canonical, assessment.CaseId);
        Append(canonical, assessment.BindingHash);
        Append(canonical, assessment.AssessmentId);
        Append(canonical, (int)assessment.Kind);
        Append(canonical, assessment.ObservationHashes);
        Append(canonical, assessment.AuthorityEvidenceHash);
        Append(canonical, assessment.AssessedAtUtc);
        Append(canonical, assessment.SafeDetail);
        return Finish(canonical);
    }

    /// <summary>Computes the canonical hash of one structurally valid disposition.</summary>
    public static string Compute(GovernedLoopEffectReconciliationDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        GovernedLoopEffectReconciliationContractValidator.ThrowIfInvalidForHash(disposition, nameof(disposition));
        var canonical = Start("embodysense.governed-loop-effect-reconciliation-disposition.v1");
        Append(canonical, disposition.SchemaVersion);
        Append(canonical, disposition.CaseId);
        Append(canonical, disposition.BindingHash);
        Append(canonical, disposition.DispositionId);
        Append(canonical, (int)disposition.Kind);
        Append(canonical, disposition.AssessmentHash);
        Append(canonical, disposition.AuthorityEvidenceHash);
        Append(canonical, disposition.DisposedAtUtc);
        Append(canonical, disposition.SafeDetail);
        return Finish(canonical);
    }

    /// <summary>Computes the canonical hash of one structurally valid resolution.</summary>
    public static string Compute(GovernedLoopEffectReconciliationResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        GovernedLoopEffectReconciliationContractValidator.ThrowIfInvalidForHash(resolution, nameof(resolution));
        var canonical = Start("embodysense.governed-loop-effect-reconciliation-resolution.v1");
        Append(canonical, resolution.SchemaVersion);
        Append(canonical, resolution.CaseId);
        Append(canonical, resolution.BindingHash);
        Append(canonical, resolution.ResolutionId);
        Append(canonical, resolution.AssessmentHash);
        Append(canonical, resolution.DispositionHash);
        Append(canonical, (int)resolution.Outcome);
        Append(canonical, resolution.OutcomeEvidenceId);
        Append(canonical, resolution.OutcomeEvidenceHash);
        Append(canonical, resolution.AuthorityEvidenceHash);
        Append(canonical, resolution.ResolvedAtUtc);
        Append(canonical, resolution.SafeDetail);
        return Finish(canonical);
    }

    /// <summary>Computes the canonical hash of one structurally valid complete reconciliation case.</summary>
    public static string Compute(GovernedLoopEffectReconciliationCase reconciliationCase)
    {
        ArgumentNullException.ThrowIfNull(reconciliationCase);
        GovernedLoopEffectReconciliationContractValidator.ThrowIfInvalidForHash(reconciliationCase, nameof(reconciliationCase));
        var canonical = Start("embodysense.governed-loop-effect-reconciliation-case.v1");
        Append(canonical, reconciliationCase.SchemaVersion);
        Append(canonical, reconciliationCase.CaseId);
        Append(canonical, reconciliationCase.CaseVersion);
        AppendBinding(canonical, reconciliationCase.Binding);
        Append(canonical, reconciliationCase.ContractMetadata.ContentHash);
        Append(canonical, reconciliationCase.EvidenceSources.Select(value => value.ContentHash).ToArray());
        Append(canonical, reconciliationCase.ObservationHistory.Select(value => value.ContentHash).ToArray());
        Append(canonical, reconciliationCase.AssessmentHistory.Select(value => value.ContentHash).ToArray());
        Append(canonical, reconciliationCase.CurrentAssessmentHash);
        Append(canonical, reconciliationCase.Disposition is not null);
        if (reconciliationCase.Disposition is not null)
        {
            Append(canonical, reconciliationCase.Disposition.ContentHash);
        }

        Append(canonical, reconciliationCase.Resolution is not null);
        if (reconciliationCase.Resolution is not null)
        {
            Append(canonical, reconciliationCase.Resolution.ContentHash);
        }

        Append(canonical, reconciliationCase.CaseReceiptHashes);
        Append(canonical, reconciliationCase.PreviousContentHash);
        Append(canonical, reconciliationCase.OpenedAtUtc);
        Append(canonical, reconciliationCase.UpdatedAtUtc);
        return Finish(canonical);
    }

    /// <summary>Returns a defensive exact binding copy carrying its canonical hash.</summary>
    public static GovernedLoopEffectReconciliationBinding Apply(GovernedLoopEffectReconciliationBinding binding)
        => GovernedLoopEffectReconciliationContractCopy.Copy(binding) with { ContentHash = Compute(binding) };

    /// <summary>Returns a defensive actuator reconciliation contract copy carrying its canonical hash.</summary>
    public static GovernedLoopEffectReconciliationContractMetadata Apply(GovernedLoopEffectReconciliationContractMetadata metadata)
        => GovernedLoopEffectReconciliationContractCopy.Copy(metadata) with { ContentHash = Compute(metadata) };

    /// <summary>Returns a defensive source-registration copy carrying its canonical hash.</summary>
    public static GovernedLoopEffectReconciliationEvidenceSource Apply(GovernedLoopEffectReconciliationEvidenceSource source)
        => GovernedLoopEffectReconciliationContractCopy.Copy(source) with { ContentHash = Compute(source) };

    /// <summary>Returns a defensive observation copy carrying its canonical hash.</summary>
    public static GovernedLoopEffectReconciliationObservation Apply(GovernedLoopEffectReconciliationObservation observation)
        => GovernedLoopEffectReconciliationContractCopy.Copy(observation) with { ContentHash = Compute(observation) };

    /// <summary>Returns a defensive assessment copy carrying its canonical hash.</summary>
    public static GovernedLoopEffectReconciliationAssessment Apply(GovernedLoopEffectReconciliationAssessment assessment)
        => GovernedLoopEffectReconciliationContractCopy.Copy(assessment) with { ContentHash = Compute(assessment) };

    /// <summary>Returns a defensive disposition copy carrying its canonical hash.</summary>
    public static GovernedLoopEffectReconciliationDisposition Apply(GovernedLoopEffectReconciliationDisposition disposition)
        => GovernedLoopEffectReconciliationContractCopy.Copy(disposition)! with { ContentHash = Compute(disposition) };

    /// <summary>Returns a defensive resolution copy carrying its canonical hash.</summary>
    public static GovernedLoopEffectReconciliationResolution Apply(GovernedLoopEffectReconciliationResolution resolution)
        => GovernedLoopEffectReconciliationContractCopy.Copy(resolution)! with { ContentHash = Compute(resolution) };

    /// <summary>Returns a defensive complete case copy carrying its canonical hash.</summary>
    public static GovernedLoopEffectReconciliationCase Apply(GovernedLoopEffectReconciliationCase reconciliationCase)
        => GovernedLoopEffectReconciliationContractCopy.Copy(reconciliationCase) with { ContentHash = Compute(reconciliationCase) };

    /// <summary>Gets whether a binding carries its exact canonical hash.</summary>
    public static bool Matches(GovernedLoopEffectReconciliationBinding? binding)
        => MatchesCore(binding?.ContentHash, () => Compute(binding!));

    /// <summary>Gets whether actuator reconciliation metadata carries its exact canonical hash.</summary>
    public static bool Matches(GovernedLoopEffectReconciliationContractMetadata? metadata)
        => MatchesCore(metadata?.ContentHash, () => Compute(metadata!));

    /// <summary>Gets whether a source registration carries its exact canonical hash.</summary>
    public static bool Matches(GovernedLoopEffectReconciliationEvidenceSource? source)
        => MatchesCore(source?.ContentHash, () => Compute(source!));

    /// <summary>Gets whether an observation carries its exact canonical hash.</summary>
    public static bool Matches(GovernedLoopEffectReconciliationObservation? observation)
        => MatchesCore(observation?.ContentHash, () => Compute(observation!));

    /// <summary>Gets whether an assessment carries its exact canonical hash.</summary>
    public static bool Matches(GovernedLoopEffectReconciliationAssessment? assessment)
        => MatchesCore(assessment?.ContentHash, () => Compute(assessment!));

    /// <summary>Gets whether a disposition carries its exact canonical hash.</summary>
    public static bool Matches(GovernedLoopEffectReconciliationDisposition? disposition)
        => MatchesCore(disposition?.ContentHash, () => Compute(disposition!));

    /// <summary>Gets whether a resolution carries its exact canonical hash.</summary>
    public static bool Matches(GovernedLoopEffectReconciliationResolution? resolution)
        => MatchesCore(resolution?.ContentHash, () => Compute(resolution!));

    /// <summary>Gets whether a case carries its exact canonical hash.</summary>
    public static bool Matches(GovernedLoopEffectReconciliationCase? reconciliationCase)
        => MatchesCore(reconciliationCase?.ContentHash, () => Compute(reconciliationCase!));

    private static StringBuilder Start(string domain)
    {
        var canonical = new StringBuilder(4_096);
        Append(canonical, domain);
        return canonical;
    }

    private static string Finish(StringBuilder canonical)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();

    private static void AppendBinding(StringBuilder canonical, GovernedLoopEffectReconciliationBinding binding)
    {
        Append(canonical, binding.SchemaVersion);
        Append(canonical, binding.WorkspaceId);
        Append(canonical, binding.Execution.SchemaVersion);
        Append(canonical, binding.Execution.RunId);
        Append(canonical, binding.Execution.Revision.SchemaVersion);
        Append(canonical, binding.Execution.Revision.GraphId);
        Append(canonical, binding.Execution.Revision.RevisionId);
        Append(canonical, binding.Execution.Revision.ExecutableHash);
        Append(canonical, binding.Execution.ExecutionGeneration);
        Append(canonical, binding.NodeId);
        Append(canonical, binding.ActivationOrdinal);
        Append(canonical, binding.VisitOrdinal);
        Append(canonical, binding.NodeAttempt);
        Append(canonical, binding.EffectId);
        Append(canonical, binding.OperationId);
        Append(canonical, binding.EffectGeneration);
        Append(canonical, binding.IntentHash);
        Append(canonical, binding.CurrentAttemptHash);
    }

    private static bool MatchesCore(string? actual, Func<string> compute)
    {
        if (!GovernedLoopEffectReconciliationContractValidator.IsCanonicalSha256(actual))
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual!), Encoding.ASCII.GetBytes(compute()));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Append(StringBuilder canonical, IReadOnlyList<string> values)
    {
        Append(canonical, values.Count);
        foreach (var value in values)
        {
            Append(canonical, value);
        }
    }

    private static void Append(StringBuilder canonical, bool value) => Append(canonical, value ? "true" : "false");

    private static void Append(StringBuilder canonical, int value) => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, long value) => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, DateTimeOffset value) => Append(canonical, value.ToString("O", CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, DateTimeOffset? value)
    {
        Append(canonical, value is not null);
        if (value is not null)
        {
            Append(canonical, value.Value);
        }
    }

    private static void Append(StringBuilder canonical, string? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
            return;
        }

        canonical.Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(value);
    }
}
