using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text.Json;
using EmbodySense.Core.Persistence.Tests.Verification.Models;
using Xunit.Abstractions;

namespace EmbodySense.Core.Persistence.Tests.Verification;

internal sealed class VerificationPhaseProbe
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITestOutputHelper _output;
    private readonly string _testName;
    private readonly string _tier;
    private string _lastCompletedPhase = "none";

    public VerificationPhaseProbe(ITestOutputHelper output, string testName, string tier)
    {
        _output = output;
        _testName = testName;
        _tier = tier;
        WriteContext();
    }

    public T Run<T>(VerificationPhaseBudget budget, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var observation = Start(budget);
        try
        {
            var result = action();
            Complete(budget, observation);
            return result;
        }
        catch (Exception exception)
        {
            Fail(budget, observation, exception);
            throw;
        }
    }

    public void Run(VerificationPhaseBudget budget, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Run(budget, () =>
        {
            action();
            return true;
        });
    }

    public async Task<T> RunAsync<T>(VerificationPhaseBudget budget, Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var observation = Start(budget);
        try
        {
            var result = await action();
            Complete(budget, observation);
            return result;
        }
        catch (Exception exception)
        {
            Fail(budget, observation, exception);
            throw;
        }
    }

    public async Task RunAsync(VerificationPhaseBudget budget, Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        await RunAsync(budget, async () =>
        {
            await action();
            return true;
        });
    }

    private PhaseObservation Start(VerificationPhaseBudget budget)
    {
        ValidateBudget(budget);
        var observation = new PhaseObservation(Stopwatch.StartNew(), GC.GetTotalAllocatedBytes(precise: true));
        Write("VERIFY_TEST_PHASE_START", new
        {
            test = _testName,
            tier = _tier,
            phase = budget.Name,
            classification = budget.Classification.ToString(),
            proposedBudgetMilliseconds = (long)budget.ProposedBudget.TotalMilliseconds,
            diagnosticBoundMilliseconds = (long)budget.DiagnosticBound.TotalMilliseconds,
            budget.MaximumAllocatedBytes,
            startedAtUtc = DateTimeOffset.UtcNow,
            lastCompletedPhase = _lastCompletedPhase
        });
        return observation;
    }

    private void Complete(VerificationPhaseBudget budget, PhaseObservation observation)
    {
        observation.Stopwatch.Stop();
        var allocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: true) - observation.StartAllocatedBytes);
        if (observation.Stopwatch.Elapsed > budget.DiagnosticBound)
        {
            throw new TimeoutException($"Verification phase `{budget.Name}` completed in {observation.Stopwatch.Elapsed} after exceeding its diagnostic bound of {budget.DiagnosticBound}. Last completed phase: `{_lastCompletedPhase}`.");
        }

        if (budget.MaximumAllocatedBytes is { } maximumAllocatedBytes && allocatedBytes > maximumAllocatedBytes)
        {
            throw new InvalidOperationException($"Verification phase `{budget.Name}` allocated approximately {allocatedBytes:N0} bytes, exceeding its {maximumAllocatedBytes:N0}-byte maximum. Last completed phase: `{_lastCompletedPhase}`.");
        }

        _lastCompletedPhase = budget.Name;
        Write("VERIFY_TEST_PHASE_COMPLETE", new
        {
            test = _testName,
            tier = _tier,
            phase = budget.Name,
            classification = budget.Classification.ToString(),
            elapsedMilliseconds = observation.Stopwatch.ElapsedMilliseconds,
            allocatedBytes,
            proposedBudgetMilliseconds = (long)budget.ProposedBudget.TotalMilliseconds,
            diagnosticBoundMilliseconds = (long)budget.DiagnosticBound.TotalMilliseconds,
            budget.MaximumAllocatedBytes,
            completedAtUtc = DateTimeOffset.UtcNow,
            lastCompletedPhase = _lastCompletedPhase
        });
    }

    private void Fail(VerificationPhaseBudget budget, PhaseObservation observation, Exception exception)
    {
        observation.Stopwatch.Stop();
        Write("VERIFY_TEST_PHASE_FAILED", new
        {
            test = _testName,
            tier = _tier,
            phase = budget.Name,
            classification = budget.Classification.ToString(),
            elapsedMilliseconds = observation.Stopwatch.ElapsedMilliseconds,
            exceptionType = exception.GetType().FullName,
            exception.Message,
            failedAtUtc = DateTimeOffset.UtcNow,
            lastCompletedPhase = _lastCompletedPhase
        });
    }

    private void WriteContext()
    {
        Write("VERIFY_TEST_CONTEXT", new
        {
            schemaVersion = 1,
            test = _testName,
            tier = _tier,
            capturedAtUtc = DateTimeOffset.UtcNow,
            machineName = Environment.MachineName,
            RuntimeInformation.OSDescription,
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            Environment.Is64BitProcess,
            serverGarbageCollection = GCSettings.IsServerGC,
            processId = Environment.ProcessId,
            continuousIntegration = Environment.GetEnvironmentVariable("CI"),
            runnerName = Environment.GetEnvironmentVariable("RUNNER_NAME"),
            runnerOs = Environment.GetEnvironmentVariable("RUNNER_OS"),
            runnerArchitecture = Environment.GetEnvironmentVariable("RUNNER_ARCH"),
            githubRunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
            githubRunAttempt = Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"),
            githubSha = Environment.GetEnvironmentVariable("GITHUB_SHA")
        });
    }

    private void Write(string prefix, object value)
    {
        _output.WriteLine($"{prefix}={JsonSerializer.Serialize(value, _jsonOptions)}");
    }

    private static void ValidateBudget(VerificationPhaseBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (string.IsNullOrWhiteSpace(budget.Name))
        {
            throw new ArgumentException("Verification phase names cannot be empty.", nameof(budget));
        }

        if (budget.ProposedBudget <= TimeSpan.Zero || budget.DiagnosticBound < budget.ProposedBudget)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "Verification phase budgets must be positive and diagnostic bounds cannot be lower than proposed budgets.");
        }

        if (budget.MaximumAllocatedBytes is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "Verification phase allocation bounds must be positive when supplied.");
        }
    }

    private sealed record PhaseObservation(Stopwatch Stopwatch, long StartAllocatedBytes);
}
