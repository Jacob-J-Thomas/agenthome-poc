using System.Text.Json;

namespace EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Defines exact per-attempt, per-node-series, and run-wide provider-usage ceilings.</summary>
public sealed class GovernedModelBudgetPolicy
{
    [System.Text.Json.Serialization.JsonConstructor]
    private GovernedModelBudgetPolicy(GovernedModelUsageCeiling perAttempt, GovernedModelUsageCeiling perNodeSeries, GovernedModelUsageCeiling perRun)
    {
        PerAttempt = perAttempt;
        PerNodeSeries = perNodeSeries;
        PerRun = perRun;
        ContentHash = GovernedModelContractHash.Compute("embodysense.model-budget-policy.v1", WriteCanonical);
    }

    /// <summary>Gets the per-provider-attempt ceiling.</summary>
    public GovernedModelUsageCeiling PerAttempt { get; }
    /// <summary>Gets the ceiling across one node's attempts.</summary>
    public GovernedModelUsageCeiling PerNodeSeries { get; }
    /// <summary>Gets the whole-run ceiling.</summary>
    public GovernedModelUsageCeiling PerRun { get; }
    /// <summary>Gets the canonical budget-policy hash.</summary>
    public string ContentHash { get; }

    /// <summary>Creates a validated nested budget policy.</summary>
    /// <remarks>When two levels bound the same dimension, an inner maximum cannot exceed its enclosing maximum.</remarks>
    public static GovernedModelBudgetPolicy Create(int schemaVersion, GovernedModelUsageCeiling perAttempt, GovernedModelUsageCeiling perNodeSeries, GovernedModelUsageCeiling perRun)
    {
        GovernedModelContractRules.RequireSchema(schemaVersion, nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(perAttempt);
        ArgumentNullException.ThrowIfNull(perNodeSeries);
        ArgumentNullException.ThrowIfNull(perRun);
        if (!GovernedModelContractValidator.IsValid(perAttempt) || !GovernedModelContractValidator.IsValid(perNodeSeries) || !GovernedModelContractValidator.IsValid(perRun))
        {
            throw new ArgumentException("Every nested usage ceiling must be canonical.");
        }
        RequireNested(perAttempt.InputTokens, perNodeSeries.InputTokens, perRun.InputTokens, "input tokens");
        RequireNested(perAttempt.OutputTokens, perNodeSeries.OutputTokens, perRun.OutputTokens, "output tokens");
        RequireNested(perAttempt.CachedTokens, perNodeSeries.CachedTokens, perRun.CachedTokens, "cached tokens");
        RequireNested(perAttempt.TotalTokens, perNodeSeries.TotalTokens, perRun.TotalTokens, "total tokens");
        RequireMonetaryNested(perAttempt.MonetaryCost, perNodeSeries.MonetaryCost, perRun.MonetaryCost);
        return new GovernedModelBudgetPolicy(perAttempt, perNodeSeries, perRun);
    }

    /// <summary>Returns whether a profile can affirmatively hard-enforce every bounded dimension before dispatch.</summary>
    public bool CanBeHardEnforcedBy(GovernedModelUsageSupportPolicy? support)
    {
        if (support is null)
        {
            return false;
        }

        return Supports(AnyBounded(value => value.InputTokens), support.InputTokens)
            && Supports(AnyBounded(value => value.OutputTokens), support.OutputTokens)
            && Supports(AnyBounded(value => value.CachedTokens), support.CachedTokens)
            && Supports(AnyBounded(value => value.TotalTokens), support.TotalTokens)
            && Supports(PerAttempt.MonetaryCost.IsBounded || PerNodeSeries.MonetaryCost.IsBounded || PerRun.MonetaryCost.IsBounded, support.MonetaryCost);
    }

    private bool AnyBounded(Func<GovernedModelUsageCeiling, GovernedModelUsageLimit> select)
        => select(PerAttempt).IsBounded || select(PerNodeSeries).IsBounded || select(PerRun).IsBounded;

    private static bool Supports(bool bounded, GovernedModelUsageSupport support)
        => !bounded || support == GovernedModelUsageSupport.AuthoritativeAndHardBoundedAtDispatch;

    private static void RequireNested(GovernedModelUsageLimit attempt, GovernedModelUsageLimit node, GovernedModelUsageLimit run, string dimension)
    {
        if (attempt.IsBounded && node.IsBounded && attempt.Maximum > node.Maximum
            || attempt.IsBounded && run.IsBounded && attempt.Maximum > run.Maximum
            || node.IsBounded && run.IsBounded && node.Maximum > run.Maximum)
        {
            throw new ArgumentException($"The {dimension} budget widens an enclosing ceiling.");
        }
    }

    private static void RequireMonetaryNested(GovernedModelMonetaryLimit attempt, GovernedModelMonetaryLimit node, GovernedModelMonetaryLimit run)
    {
        var bounded = new[] { attempt, node, run }.Where(value => value.IsBounded).ToArray();
        if (bounded.Select(value => value.Currency).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            throw new ArgumentException("All monetary budgets must use one exact currency.");
        }

        if (attempt.IsBounded && node.IsBounded && attempt.MaximumMicros > node.MaximumMicros
            || attempt.IsBounded && run.IsBounded && attempt.MaximumMicros > run.MaximumMicros
            || node.IsBounded && run.IsBounded && node.MaximumMicros > run.MaximumMicros)
        {
            throw new ArgumentException("The monetary budget widens an enclosing ceiling.");
        }
    }

    private void WriteCanonical(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("perAttemptHash", PerAttempt.ContentHash);
        writer.WriteString("perNodeSeriesHash", PerNodeSeries.ContentHash);
        writer.WriteString("perRunHash", PerRun.ContentHash);
        writer.WriteEndObject();
    }
}
