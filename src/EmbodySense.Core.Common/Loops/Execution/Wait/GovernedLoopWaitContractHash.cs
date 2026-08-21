using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Loops.Execution.Wait.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Wait;

/// <summary>Computes and verifies canonical Wait condition, park, and continuation hashes.</summary>
public static class GovernedLoopWaitContractHash
{
    /// <summary>Computes the canonical condition hash, excluding only its content-hash field.</summary>
    public static string Compute(GovernedLoopWaitCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(condition.Descriptor, nameof(condition));
        RequireBounded(condition.Descriptor.TypeId, GovernedLoopWaitContractLimits.MaxIdentifierCharacters, nameof(condition));
        RequireBounded(condition.AuthenticatedEventReference, GovernedLoopWaitContractLimits.MaxEventReferenceCharacters, nameof(condition));
        var canonical = Start("governed-loop-wait-condition-v1");
        Append(canonical, condition.SchemaVersion);
        Append(canonical, (int)condition.Descriptor.Kind);
        Append(canonical, condition.Descriptor.TypeId);
        Append(canonical, condition.Descriptor.Version);
        Append(canonical, (int)condition.ParameterKind);
        Append(canonical, condition.WakeDeadlineUtc);
        Append(canonical, condition.AuthenticatedEventReference);
        return Digest(canonical);
    }

    /// <summary>Returns a condition carrying its exact canonical content hash.</summary>
    public static GovernedLoopWaitCondition Apply(GovernedLoopWaitCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return condition with { ContentHash = Compute(condition) };
    }

    /// <summary>Gets whether a condition retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopWaitCondition? condition)
        => condition is not null && FixedEquals(condition.ContentHash, () => Compute(condition));

    /// <summary>Computes the canonical park-evidence hash, excluding only its content-hash field.</summary>
    public static string Compute(GovernedLoopWaitParkEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.Condition, nameof(evidence));
        ArgumentNullException.ThrowIfNull(evidence.Checkpoint, nameof(evidence));
        RequireHash(evidence.Condition.ContentHash, nameof(evidence));
        RequireHash(evidence.Checkpoint.CheckpointId, nameof(evidence));
        RequireHash(evidence.Checkpoint.ContentHash, nameof(evidence));
        var canonical = Start("governed-loop-wait-park-evidence-v1");
        Append(canonical, evidence.SchemaVersion);
        Append(canonical, evidence.Condition.ContentHash);
        Append(canonical, evidence.Checkpoint.CheckpointId);
        Append(canonical, evidence.Checkpoint.ContentHash);
        Append(canonical, evidence.ParkedAtUtc);
        return Digest(canonical);
    }

    /// <summary>Returns park evidence carrying its exact canonical content hash.</summary>
    public static GovernedLoopWaitParkEvidence Apply(GovernedLoopWaitParkEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence with { ContentHash = Compute(evidence) };
    }

    /// <summary>Gets whether park evidence retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopWaitParkEvidence? evidence)
        => evidence is not null && FixedEquals(evidence.ContentHash, () => Compute(evidence));

    /// <summary>Computes the canonical continuation-evidence hash, excluding only its content-hash field.</summary>
    public static string Compute(GovernedLoopWaitContinuationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.PreparedWakeEvidence, nameof(evidence));
        RequireHash(evidence.ParkEvidenceHash, nameof(evidence));
        RequireHash(evidence.PreparedWakeEvidence.ContentHash, nameof(evidence));
        RequireHash(evidence.PreResumeFrontierHash, nameof(evidence));
        RequireHash(evidence.ResumedFrontierHash, nameof(evidence));
        var canonical = Start("governed-loop-wait-continuation-evidence-v1");
        Append(canonical, evidence.SchemaVersion);
        Append(canonical, evidence.ParkEvidenceHash);
        Append(canonical, evidence.PreparedWakeEvidence.ContentHash);
        Append(canonical, evidence.PreResumeFrontierVersion);
        Append(canonical, evidence.PreResumeFrontierHash);
        Append(canonical, evidence.ResumedFrontierVersion);
        Append(canonical, evidence.ResumedFrontierHash);
        Append(canonical, evidence.ResumedAtUtc);
        return Digest(canonical);
    }

    /// <summary>Returns continuation evidence carrying its exact canonical content hash.</summary>
    public static GovernedLoopWaitContinuationEvidence Apply(GovernedLoopWaitContinuationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence with { ContentHash = Compute(evidence) };
    }

    /// <summary>Gets whether continuation evidence retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopWaitContinuationEvidence? evidence)
        => evidence is not null && FixedEquals(evidence.ContentHash, () => Compute(evidence));

    /// <summary>Computes the canonical activation-evidence hash, excluding only its content-hash field.</summary>
    public static string Compute(GovernedLoopWaitExecutionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(evidence.Condition, nameof(evidence));
        RequireHash(evidence.Condition.ContentHash, nameof(evidence));
        if (evidence.ParkEvidence is { } parkEvidence)
        {
            RequireHash(parkEvidence.ContentHash, nameof(evidence));
        }

        if (evidence.ContinuationEvidence is { } continuationEvidence)
        {
            RequireHash(continuationEvidence.ContentHash, nameof(evidence));
        }

        RequireBounded(evidence.NodeId, GovernedLoopWaitContractLimits.MaxIdentifierCharacters, nameof(evidence));
        RequireBounded(evidence.CycleId, GovernedLoopWaitContractLimits.MaxIdentifierCharacters, nameof(evidence));
        RequireBounded(evidence.WaitOperationId, GovernedLoopWaitContractLimits.MaxIdentifierCharacters, nameof(evidence));
        RequireHash(evidence.ParkedFrontierHash, nameof(evidence));
        var canonical = Start("governed-loop-wait-execution-evidence-v1");
        Append(canonical, evidence.SchemaVersion);
        Append(canonical, evidence.ActivationOrdinal);
        Append(canonical, evidence.NodeId);
        Append(canonical, evidence.NodeVisitOrdinal);
        Append(canonical, evidence.CycleId);
        Append(canonical, evidence.CycleIteration);
        Append(canonical, evidence.WaitAttempt);
        Append(canonical, evidence.WaitOperationId);
        Append(canonical, evidence.Condition.ContentHash);
        Append(canonical, evidence.ParkedAtUtc);
        Append(canonical, evidence.ParkedFrontierVersion);
        Append(canonical, evidence.ParkedFrontierHash);
        Append(canonical, evidence.ParkEvidence?.ContentHash);
        Append(canonical, evidence.ContinuationEvidence?.ContentHash);
        return Digest(canonical);
    }

    /// <summary>Returns activation evidence carrying its exact canonical content hash.</summary>
    public static GovernedLoopWaitExecutionEvidence Apply(GovernedLoopWaitExecutionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence with { ContentHash = Compute(evidence) };
    }

    /// <summary>Gets whether activation evidence retains its exact canonical content hash.</summary>
    public static bool Matches(GovernedLoopWaitExecutionEvidence? evidence)
        => evidence is not null && FixedEquals(evidence.ContentHash, () => Compute(evidence));

    private static StringBuilder Start(string domain)
    {
        var canonical = new StringBuilder(512);
        Append(canonical, domain);
        return canonical;
    }

    private static string Digest(StringBuilder canonical)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();

    private static bool FixedEquals(string? actual, Func<string> compute)
    {
        if (!IsHash(actual))
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual!), Encoding.ASCII.GetBytes(compute()));
        }
        catch (Exception exception) when (exception is ArgumentException or NullReferenceException)
        {
            return false;
        }
    }

    private static void RequireBounded(string? value, int maximumCharacters, string parameterName)
    {
        if (value?.Length > maximumCharacters)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Canonical hash inputs may contain at most {maximumCharacters} characters per field.");
        }
    }

    private static void RequireHash(string? value, string parameterName)
    {
        if (!IsHash(value))
        {
            throw new ArgumentException("Canonical hash inputs must be lowercase SHA-256 evidence.", parameterName);
        }
    }

    private static bool IsHash(string? value)
        => value?.Length == GovernedLoopWaitContractLimits.Sha256HexCharacters
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Append(StringBuilder canonical, DateTimeOffset value)
        => Append(canonical, value.ToString("O", CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, DateTimeOffset? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
        }
        else
        {
            Append(canonical, value.Value);
        }
    }

    private static void Append(StringBuilder canonical, int value)
        => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, long value)
        => Append(canonical, value.ToString(CultureInfo.InvariantCulture));

    private static void Append(StringBuilder canonical, int? value)
    {
        if (value is null)
        {
            canonical.Append("-1:");
        }
        else
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

        var normalized = value.Normalize(NormalizationForm.FormC);
        canonical.Append(Encoding.UTF8.GetByteCount(normalized).ToString(CultureInfo.InvariantCulture));
        canonical.Append(':');
        canonical.Append(normalized);
    }
}
