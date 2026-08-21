using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Loops.Retry.Models;
using EmbodySense.Core.Common.Loops.Execution.Retry;
using EmbodySense.Core.Common.Loops.Execution.Retry.Models;

namespace EmbodySense.Core.Application.Loops.Retry;

/// <summary>Evaluates opt-in retry policy without dispatching, sleeping, persisting, or granting authority.</summary>
public static class GovernedLoopRetryDecisionService
{
    /// <summary>Evaluates one exact failure and returns a schedule only when every retry-safety and hard-budget proof is affirmative.</summary>
    public static GovernedLoopRetryDecision Evaluate(GovernedLoopRetryEvaluationRequest? request)
    {
        if (!TryValidateRequest(request, out var series, out var invalidDetail))
        {
            var invalidStatus = string.Equals(invalidDetail, "failure-not-retry-safe", StringComparison.Ordinal)
                ? GovernedLoopRetryDecisionStatus.NoRetry
                : string.Equals(invalidDetail, "retry-series-substituted", StringComparison.Ordinal)
                    ? GovernedLoopRetryDecisionStatus.Conflict
                    : GovernedLoopRetryDecisionStatus.Invalid;
            return Decision(invalidStatus, detail: invalidDetail);
        }

        if (request!.LifecyclePosture == GovernedLoopRetryLifecyclePosture.Paused)
        {
            return Decision(GovernedLoopRetryDecisionStatus.Paused, series, detail: "current-run-paused");
        }
        if (request.LifecyclePosture == GovernedLoopRetryLifecyclePosture.Cancelled)
        {
            return Decision(GovernedLoopRetryDecisionStatus.Cancelled, series, detail: "current-run-cancelled");
        }
        if (request.LifecyclePosture == GovernedLoopRetryLifecyclePosture.ReviewBlocked)
        {
            return Decision(GovernedLoopRetryDecisionStatus.NeedsReview, series, detail: "current-run-review-blocked");
        }
        if (!request.CurrentLifecycleEligible || request.LifecyclePosture != GovernedLoopRetryLifecyclePosture.Active
            || !request.CurrentAuthorityEligible || !request.CurrentDependenciesEligible)
        {
            return Decision(GovernedLoopRetryDecisionStatus.NoRetry, series, detail: "current-posture-ineligible");
        }

        if (HardBudgetIsUnknown(request.Policy, request.Budget))
        {
            return Decision(GovernedLoopRetryDecisionStatus.NeedsReview, series, detail: "hard-budget-evidence-unavailable");
        }

        if (request.CurrentAttempt >= request.Policy.MaximumAttempts || HardBudgetIsExhausted(request.Policy, request.Budget))
        {
            return Decision(GovernedLoopRetryDecisionStatus.Exhausted, series, detail: "retry-budget-exhausted");
        }

        var nextAttempt = checked(request.CurrentAttempt + 1);
        var delay = GovernedLoopRetryContract.ComputeDelay(request.Policy, series!.SeriesId, nextAttempt);
        DateTimeOffset eligibleAtUtc;
        DateTimeOffset attemptDeadlineUtc;
        try
        {
            eligibleAtUtc = request.EvaluatedAtUtc.Add(delay);
            attemptDeadlineUtc = eligibleAtUtc.AddMilliseconds(request.Policy.PerAttemptTimeoutMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return Decision(GovernedLoopRetryDecisionStatus.Invalid, series, detail: "retry-time-overflow");
        }

        if (eligibleAtUtc > series.DeadlineUtc || attemptDeadlineUtc > series.DeadlineUtc)
        {
            return Decision(GovernedLoopRetryDecisionStatus.Exhausted, series, detail: "retry-deadline-exhausted");
        }

        var operationId = GovernedLoopRetryContract.CreateAttemptOperationId(series.SeriesId, nextAttempt);
        var status = eligibleAtUtc <= request.EvaluatedAtUtc ? GovernedLoopRetryDecisionStatus.Due : GovernedLoopRetryDecisionStatus.Schedule;
        return new GovernedLoopRetryDecision(status, series, nextAttempt, eligibleAtUtc, operationId, status == GovernedLoopRetryDecisionStatus.Due ? "retry-due-admitted" : "retry-schedule-admitted");
    }

    private static bool TryValidateRequest(
        GovernedLoopRetryEvaluationRequest? request,
        out GovernedLoopRetrySeriesIdentity? series,
        out string detail)
    {
        series = null;
        detail = "retry-request-invalid";
        if (request is null
            || !GovernedLoopRetryContract.IsValid(request.Policy)
            || request.Failure is null
            || request.Budget is null
            || request.CurrentAttempt is < 1 or > GovernedLoopRetryContractLimits.MaximumAttempts
            || request.Budget.Attempts != request.CurrentAttempt
            || request.Budget.Tokens is < 0 or > GovernedLoopRetryContractLimits.MaximumTokens
            || request.Budget.ToolCalls is < 0 or > GovernedLoopRetryContractLimits.MaximumToolCalls
            || request.Budget.CostMicrounits is < 0 or > GovernedLoopRetryContractLimits.MaximumCostMicrounits
            || (request.Budget.CostMicrounits is null) != (request.Budget.CostCurrency is null)
            || request.Policy.MaximumCostMicrounits is not null
                && request.Budget.CostMicrounits is not null
                && !string.Equals(request.Policy.MaximumCostCurrency, request.Budget.CostCurrency, StringComparison.Ordinal)
            || request.Budget.ResourceUnits is < 0 or > GovernedLoopRetryContractLimits.MaximumResourceUnits
            || request.SeriesStartedAtUtc.Offset != TimeSpan.Zero
            || request.EvaluatedAtUtc.Offset != TimeSpan.Zero
            || request.EvaluatedAtUtc < request.SeriesStartedAtUtc
            || request.EvaluatedAtUtc < request.Failure.ObservedAtUtc
            || request.EnclosingDeadlineUtc is { Offset: var offset } && offset != TimeSpan.Zero
            || !Enum.IsDefined(request.LifecyclePosture)
            || request.LifecyclePosture == GovernedLoopRetryLifecyclePosture.Unknown)
        {
            return false;
        }

        try
        {
            var evaluatedSeries = GovernedLoopRetryContract.CreateSeries(
                request.Policy,
                request.Failure,
                request.SeriesStartedAtUtc,
                request.EnclosingDeadlineUtc);
            series = request.ExistingSeries ?? evaluatedSeries;
        }
        catch (ArgumentException)
        {
            detail = "failure-not-retry-safe";
            return false;
        }

        if (!GovernedLoopRetryContract.IsValid(series)
            || !string.Equals(series.PolicyHash, request.Policy.ContentHash, StringComparison.Ordinal)
            || !string.Equals(series.PolicyId, request.Policy.PolicyId, StringComparison.Ordinal)
            || !string.Equals(series.WorkspaceId, request.Failure.WorkspaceId, StringComparison.Ordinal)
            || !string.Equals(series.RunId, request.Failure.RunId, StringComparison.Ordinal)
            || series.Revision != request.Failure.Revision
            || series.ExecutionGeneration != request.Failure.ExecutionGeneration
            || series.ActivationOrdinal != request.Failure.ActivationOrdinal
            || series.VisitOrdinal != request.Failure.VisitOrdinal
            || !string.Equals(series.NodeId, request.Failure.NodeId, StringComparison.Ordinal)
            || request.EvaluatedAtUtc < series.StartedAtUtc
            || request.EnclosingDeadlineUtc is { } enclosing && enclosing != series.DeadlineUtc && enclosing < series.DeadlineUtc)
        {
            detail = "retry-series-substituted";
            return false;
        }

        detail = string.Empty;
        return true;
    }

    private static bool HardBudgetIsUnknown(GovernedLoopRetryPolicy policy, GovernedLoopRetryBudgetSnapshot budget)
        => policy.MaximumTokens is not null && budget.Tokens is null
            || policy.MaximumToolCalls is not null && budget.ToolCalls is null
            || policy.MaximumCostMicrounits is not null && budget.CostMicrounits is null
            || policy.MaximumResourceUnits is not null && budget.ResourceUnits is null;

    private static bool HardBudgetIsExhausted(GovernedLoopRetryPolicy policy, GovernedLoopRetryBudgetSnapshot budget)
        => policy.MaximumTokens is { } maximumTokens && budget.Tokens >= maximumTokens
            || policy.MaximumToolCalls is { } maximumTools && budget.ToolCalls >= maximumTools
            || policy.MaximumCostMicrounits is { } maximumCost && budget.CostMicrounits >= maximumCost
            || policy.MaximumResourceUnits is { } maximumResources && budget.ResourceUnits >= maximumResources;

    private static GovernedLoopRetryDecision Decision(
        GovernedLoopRetryDecisionStatus status,
        GovernedLoopRetrySeriesIdentity? series = null,
        string detail = "retry-decision-unavailable")
        => new(status, series, null, null, null, detail);
}
